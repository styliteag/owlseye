// Sim provider: same logic as on Windows (AdProvider) against emulated AD and file system.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using static Owlseye.Providers.Demo;

namespace Owlseye.Tests;

public sealed class AclSimTests : TestBase
{
    const string OPS = "Operations";

    /// <summary>A seeded sim directory.</summary>
    readonly string simDir;

    public AclSimTests() => simDir = SimSeed.Seed(Path.Combine(Tmp, "sim"));

    string Share => Path.Combine(simDir, SimProvider.ShareDir);

    /// <summary>Edit state.json by hand (like a change in ADUC/Explorer).</summary>
    static void State(string simDir, Action<JsonObject> change)
    {
        var p = Path.Combine(simDir, SimState.FileName);
        var s = (JsonObject)JsonNode.Parse(File.ReadAllText(p))!;
        change(s);
        File.WriteAllText(p, s.ToJsonString());
    }

    static string N(string? v) => v ?? "<None>";

    /// <summary>Comparable form of a scan: cells, user rights, findings, principals, folders (each sorted).</summary>
    static List<List<string>> Outcome(Snapshot s)
    {
        var c = Rights.Matrix(s);
        return
        [
            c.Select(kv => $"{kv.Key.Sid}|{kv.Key.Path}|{N(kv.Value.Direct)}|{N(kv.Value.Effective)}|{N(kv.Value.Source)}|{kv.Value.Standard}")
                .Order(StringComparer.Ordinal).ToList(),
            Rights.UserRights(s, cells: c).SelectMany(u => u.Value.Select(r => $"{u.Key}|{r.Key}|{r.Value}").Append($"{u.Key}|<user>"))
                .Order(StringComparer.Ordinal).ToList(),
            Rights.Findings(s, cells: c).Select(f => $"{f.Severity}|{f.Path}|{f.Text}").Order(StringComparer.Ordinal).ToList(),
            s.Principals.Values.Select(p => $"{p.Sid}|{p.Name}|{p.Kind}|{p.Dn}").Distinct().Order(StringComparer.Ordinal).ToList(),
            s.Folders.Values.Select(f => $"{f.Path}|{f.Level}|{f.Protected}").Distinct().Order(StringComparer.Ordinal).ToList(),
        ];
    }

    [Fact]
    public void ScanMatchesDemo()
    {
        // Walking folders, interpreting ACLs, resolving accounts and members gives the same as the demo data.
        var sim = Outcome(new SimProvider(simDir).Scan());
        var demo = Outcome(new DemoProvider().Scan());
        Assert.Equal(demo.Count, sim.Count);
        for (var i = 0; i < demo.Count; i++) Assert.Equal(demo[i], sim[i]);
    }

    [Fact]
    public void UnknownSidAndUserEntry()
    {
        var s = new SimProvider(simDir).Scan();
        Assert.Equal("unknown", s.Principals["S-1-5-21-1-2-3-4711"].Kind);
        Assert.Equal("user", s.Principals.Values.First(p => p.Short == "mmoore").Kind);
    }

    [Fact]
    public void ExplicitEntriesCarryNamesAndKinds()
    {
        var @explicit = new Dictionary<string, string>();
        foreach (var a in new SimProvider(simDir).Scan().Folders["HR"].Explicit) @explicit[a.Name] = a.Kind;
        Assert.Equal(new Dictionary<string, string>
        {
            ["NT AUTHORITY\\SYSTEM"] = "wellknown",
            ["BUILTIN\\Administrators"] = "wellknown",
            ["DEMO\\G-HR"] = "group",
        }, @explicit);
    }

    [Fact]
    public void NewFolderOnDiskShowsUpAndInherits()
    {
        Directory.CreateDirectory(Path.Combine(Share, "Programs", "New"));
        var s = new SimProvider(simDir).Scan();
        Assert.Equal("R", Rights.Matrix(s)[(Gsid("G-AllUsers"), @"Programs\New")].Effective);
    }

    [Fact]
    public void ApplyPlanPersists()
    {
        var p = new SimProvider(simDir);
        var plan = Planner.Build(p.Scan(), new Dictionary<(string, string), string?> { [(Gsid("G-Interns"), $@"{OPS}\Sales-QA")] = "R" });
        foreach (var o in plan.AclOps)
            p.SetFolderAcl(o.Path, o.ProtectedAfter, o.After.ToList());
        var s = new SimProvider(simDir).Scan();
        var c = Rights.Matrix(s);
        Assert.Equal("R", c[(Gsid("G-Interns"), $@"{OPS}\Sales-QA")].Direct);
        Assert.Equal("R|", c[(Gsid("G-Interns"), OPS)].Direct);
        Assert.True(Planner.Build(s, new Dictionary<(string, string), string?> { [(Gsid("G-Interns"), $@"{OPS}\Sales-QA")] = "R" }).Empty);
    }

    [Fact]
    public void FindGroupsByPrefix()
    {
        var hits = new SimProvider(simDir).FindGroups("g-se");
        Assert.Equal(["G-Service-Lead", "G-Service-QA", "G-Service-Staff"], hits.Select(h => h.Short).ToList());
        Assert.All(hits, h => Assert.True(h.Dn != "" && h.Sid.StartsWith("S-1-5-21-")));
        Assert.Empty(new SimProvider(simDir).FindGroups("  "));
    }

    [Fact]
    public void FolderWithOtherAceTypesIsRefused()
    {
        State(simDir, s => s["acls"]!["HR"]!["aces"]!.AsArray().Add(new JsonArray(9, 3, 0x1301BF, "S-1-5-11")));
        var p = new SimProvider(simDir);
        var s = p.Scan();
        Assert.True(s.Folders["HR"].OtherAces);
        Assert.Throws<PlanError>(() => Planner.Build(s, new Dictionary<(string, string), string?> { [(Gsid("G-Management"), "HR")] = "R" }));
        Assert.Throws<UnauthorizedAccessException>(() => p.SetFolderAcl("HR", true, []));
    }

    [Fact]
    public void UnresolvedInheritedSidBlocksBreak()
    {
        State(simDir, s => s["sids"]!.AsObject().Remove(Gsid("G-Management")));
        var e = Assert.Throws<PlanError>(() => Planner.Build(new SimProvider(simDir).Scan(),
            new Dictionary<(string, string), string?>(), new Dictionary<string, bool> { [$@"{OPS}\Service-Staff"] = true }));
        Assert.Contains("unresolved", e.Message);
    }

    [Theory]
    [InlineData("HR.")]
    [InlineData("HR ")]
    [InlineData("M\u0001AV")]
    [InlineData(@"HR\..\x")]
    public void BadPathsAreRefused(string path)
    {
        Assert.Throws<UnauthorizedAccessException>(() => new SimProvider(simDir).SetFolderAcl(path, true, []));
    }

    [Fact]
    public void BreakAndRestoreAreWrittenThroughTheProvider()
    {
        var p = new SimProvider(simDir);
        var path = $@"{OPS}\Service-Staff";
        var op = Assert.Single(Planner.Build(p.Scan(), new Dictionary<(string, string), string?>(), new Dictionary<string, bool> { [path] = true }).AclOps);
        p.SetFolderAcl(path, true, op.After.ToList());
        var f = new SimProvider(simDir).Scan().Folders[path];
        Assert.True(f.Protected && !f.Aces.Any(a => a.Inherited));
        Assert.True(f.Aces.Select(a => a.Sid).ToHashSet().SetEquals(op.After.Select(a => a.Sid)));
        op = Assert.Single(Planner.Build(new SimProvider(simDir).Scan(), new Dictionary<(string, string), string?>(),
            new Dictionary<string, bool> { [path] = false }).AclOps);
        p.SetFolderAcl(path, false, op.After.ToList());
        f = new SimProvider(simDir).Scan().Folders[path];
        Assert.True(!f.Protected && f.Aces.Any(a => a.Inherited));
    }

    [Fact]
    public void DemoProviderAppliesAclChanges()
    {
        var p = new DemoProvider();
        var path = $@"{OPS}\Service-Staff";
        var op = Assert.Single(Planner.Build(p.Scan(), new Dictionary<(string, string), string?>(), new Dictionary<string, bool> { [path] = true }).AclOps);
        p.SetFolderAcl(path, true, op.After.ToList());
        Assert.True(p.Scan().Folders[path].Protected);
        Assert.True(p.FolderAcl(path).Protected);
    }

    [Fact]
    public void WholeTreeIsScannedUnlessLimited()
    {
        Directory.CreateDirectory(Path.Combine(Share, "HR", "a", "b", "c", "d"));
        Assert.Contains(@"HR\a\b\c\d", new SimProvider(simDir).Scan().Folders.Keys);
        var limited = new SimProvider(simDir, scanDepth: 2);
        Assert.True(limited.Scan().Folders.ContainsKey(@"HR\a") && !limited.Scan().Folders.ContainsKey(@"HR\a\b"));
        Assert.Throws<UnauthorizedAccessException>(() => limited.SetFolderAcl(@"HR\a\b", false, []));
    }

    [Fact]
    public void CreateFolderChecks()
    {
        var p = new SimProvider(simDir);
        p.CreateFolder(@"HR\New");
        Assert.True(Directory.Exists(Path.Combine(Share, "HR", "New")));
        foreach (var bad in new[] { @"HR\New", @"Nope\X", @"HR\CON", "HR\\x." }) // how deep is checked by the planner
        {
            var e = Record.Exception(() => p.CreateFolder(bad));
            Assert.True(e is UnauthorizedAccessException || e?.GetType() == typeof(IOException), $"{bad}: {e?.GetType().Name ?? "no exception"}");
        }
    }

    [Fact]
    public void SymlinkedFolderIsNotScanned()
    {
        var outside = Path.Combine(Tmp, "outside");
        Directory.CreateDirectory(outside);
        if (!Symlink(Path.Combine(Share, "Link"), outside)) return; // creating symlinks needs developer mode or admin rights on Windows
        Assert.DoesNotContain("Link", new SimProvider(simDir).Scan().Folders.Keys);
    }

    [Fact]
    public void AFolderThatBecameALinkIsNotWrittenTo()
    {
        var p = new SimProvider(simDir);
        p.Scan();
        var outside = Path.Combine(Tmp, "outside");
        Directory.Move(Path.Combine(Share, "HR"), outside); // after the scan the folder is replaced by a link
        if (!Symlink(Path.Combine(Share, "HR"), outside)) return; // creating symlinks needs developer mode or admin rights on Windows
        Assert.Throws<UnauthorizedAccessException>(() => p.CreateFolder(@"HR\New"));
        Assert.Throws<UnauthorizedAccessException>(() => p.SetFolderAcl("HR", true, []));
        Assert.False(Directory.Exists(Path.Combine(outside, "New")));
    }

    [Fact]
    public void FolderNamesWithABackslashAreSkipped()
    {
        // On Linux/macOS a folder can be named 'a\..\..\x'; as a path it would point out of the share.
        if (OperatingSystem.IsWindows()) return; // skipif win32: a backslash cannot be part of a folder name on Windows
        Directory.CreateDirectory(Path.Combine(Share, "a\\..\\..\\x"));
        Assert.All(new SimProvider(simDir).Scan().Folders.Keys, p => Assert.DoesNotContain("\\..", p));
    }

    /// <summary>A sim file system where listing one folder is not allowed.</summary>
    sealed class SubdirsDenied(IFilesystem real, string denied) : IFilesystem
    {
        public HashSet<string> Links => real.Links;
        public string ToUnc(string path) => real.ToUnc(path);
        public (bool Protected, List<RawAce> Aces) ReadDacl(string rel) => real.ReadDacl(rel);
        public (string Name, string Domain, int Use) LookupSid(string sid) => real.LookupSid(sid);
        public void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces) => real.WriteDacl(rel, isProtected, aces);
        public bool Exists(string rel) => real.Exists(rel);
        public void Mkdir(string rel) => real.Mkdir(rel);

        public List<string> Subdirs(string rel) =>
            rel == denied ? throw new UnauthorizedAccessException("Access denied") : real.Subdirs(rel);
    }

    [Fact]
    public void UnreadableFolderIsAFindingNotACrash()
    {
        // SimProvider is AdProvider over SimFs + SimDirectory; built by hand to put the patched file system in between.
        var state = new SimState(simDir);
        var fs = new SubdirsDenied(new SimFs(state, Share), "HR");
        var provider = new AdProvider(fs, new SimDirectory(state), state.Data.Obj("config").Str("share") ?? "");
        var s = provider.Scan();
        Assert.StartsWith("Contents not readable", s.Folders["HR"].Error);
        Assert.Contains(Rights.Findings(s), f => f.Path == "HR" && f.Severity == "high");
        Assert.Contains("Programs", s.Folders.Keys); // the rest is scanned
    }

    [Fact]
    public void SidBinaryRoundtrip()
    {
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-21-1-2-3-2014", "S-1-5-32-544" })
            Assert.Equal(sid, AdProvider.SidFromBytes(AdProvider.SidToBytes(sid)));
    }

    [Fact]
    public void LdapEscapeHexEncodesEverythingUnusual()
    {
        Assert.Equal("G-Sales Staff", AdProvider.LdapEscape("G-Sales Staff"));
        Assert.Equal("a\\2ab\\28c\\29\\5cd\\3be\\3cf\\3e", AdProvider.LdapEscape("a*b(c)\\d;e<f>"));
        Assert.Equal("\\c3\\a4", AdProvider.LdapEscape("ä"));
    }
}
