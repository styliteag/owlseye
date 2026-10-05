// Port of backend/tests/test_acl_hardening.py.
// Hardening of the ACL write path (security review): stale inheritance, foreign ACE types, SID instead of name.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class AclHardeningTests : TestBase
{
    const string Quotes = @"Sales\Quotes";

    /// <summary>sim_dir fixture</summary>
    readonly string simDir;

    public AclHardeningTests() => simDir = SimSeed.Seed(Path.Combine(Tmp, "sim"));

    string Share => Path.Combine(simDir, SimProvider.ShareDir);

    /// <summary>create_app(Config(provider="sim", audit=tmp_path/"a.jsonl"), SimProvider(sim_dir), "tok") + TestClient.</summary>
    static Session Client(string simDir, string tmp)
    {
        var st = new State(new Config { Provider = "sim", Audit = Path.Combine(tmp, "a.jsonl") }, new SimProvider(simDir));
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return new Session(st);
    }

    /// <summary>A path below `parent` whose components are used exactly as written (\\?\ prefix: no stripping of
    /// trailing dots/spaces).</summary>
    static string RawPath(string parent, params string[] names) => @"\\?\" + Path.GetFullPath(parent) + "\\" + string.Join("\\", names);

    [Fact]
    public void SidBinaryRoundtrip()
    {
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-21-1-2-3-2014", "S-1-5-32-544" })
            Assert.Equal(sid, AdProvider.SidFromBytes(AdProvider.SidToBytes(sid)));
    }

    // L4: undo only for entries of this share, skip broken lines
    [Fact]
    public void UndoRefusesOtherShareAndSurvivesBadLines()
    {
        var c = Client(simDir, Tmp);
        var log = Path.Combine(Tmp, "a.jsonl");
        var entry = new JsonObject
        {
            ["id"] = "x1", ["ts"] = "t", ["share"] = @"\\fs02\Other", ["ops"] = new JsonArray(), ["acl_ops"] = new JsonArray(), ["actor"] = "A",
        };
        File.WriteAllText(log, File.ReadAllText(log) + entry.ToJsonString() + "\n{broken\n" + "{\"ts\": \"no id\"}\n");
        // c.get("/audit").status_code == 200
        var page = c.AuditPage();
        Assert.Contains(page.Entries, e => e.Str("id") == "x1");
        // c.post("/undo/x1").status_code == 400
        var err = Assert.Throws<UserError>(() => c.Undo("x1"));
        Assert.Equal(400, err.Status);
    }

    // L5/L6: path components
    [Theory]
    [InlineData("Sales.")]
    [InlineData("Sales ")]
    [InlineData("Sal\u0001es")]
    public void TrailingDotSpaceControlCharsRefused(string path)
    {
        Assert.Throws<UnauthorizedAccessException>(() => new SimProvider(simDir).SetFolderAcl(path, true, []));
    }

    [Fact]
    public void SymlinkedFolderIsNotScannedOrWritten()
    {
        var outside = Path.Combine(Tmp, "outside");
        Directory.CreateDirectory(outside);
        if (!Symlink(Path.Combine(Share, "Link"), outside)) return; // pytest.skip: symlinks need developer mode or admin rights
        Assert.DoesNotContain("Link", new SimProvider(simDir).Scan().Folders.Keys);
    }

    [Fact]
    public void InheritValueIsValidated()
    {
        // Python: POST /folder/inheritance with path=Sales\Quotes, inherit="x" -> 400.
        // In C# Session.SetInheritance takes `bool inherit`: a value other than inherit/break cannot be passed at all.
        var c = Client(simDir, Tmp);
        var param = typeof(Session).GetMethod(nameof(Session.SetInheritance))!.GetParameters().Single(p => p.Name == "inherit");
        Assert.Equal(typeof(bool), param.ParameterType);
        // The same request otherwise: Sales\Quotes is not in the demo share, so the route answers 400 (in Python this
        // check runs before the one on `inherit`).
        var err = Assert.Throws<UserError>(() => c.SetInheritance(Quotes, false));
        Assert.Equal(400, err.Status);
    }

    // Hostile folder names from users (the scan runs as admin over folders users create)
    [Fact]
    public void UnaddressableNameIsAFindingNotReadOrDescended()
    {
        // Win32 strips trailing dots/spaces: 'End.' would read the ACL of 'End'. Flag it, do not look inside.
        var pub = Path.Combine(Share, "Public");
        MkdirRaw(Path.Combine(pub, "End."));
        Directory.CreateDirectory(RawPath(pub, "End.", "Deeper")); // mkdir_raw(bad / "Deeper")
        var s = new SimProvider(simDir).Scan();
        var f = s.Folders["Public\\End."];
        Assert.True(f.Error != "" && f.OtherAces);
        Assert.DoesNotContain("Public\\End.\\Deeper", s.Folders.Keys);
        Assert.Contains(Rights.Findings(s), x => x.Path == "Public\\End." && x.Severity == "high");
    }

    [Theory]
    [InlineData("Invoices\u202e")]
    [InlineData("Payroll\u200b")]
    [InlineData("Sa\u00a0les")]
    public void InvisibleOrBidiCharactersAreAFinding(string name)
    {
        Directory.CreateDirectory(Path.Combine(Share, "Public", name));
        var s = new SimProvider(simDir).Scan();
        var hits = Rights.Findings(s).Where(x => x.Path == $"Public\\{name}").ToList();
        Assert.NotEmpty(hits);
        Assert.Equal("medium", hits[0].Severity);
        Assert.Contains("invisible", hits[0].Text);
    }

    [Fact]
    public void SymlinkInsideTheShareIsRefusedForWrites()
    {
        // Also a link that points inside the share: the write must land where the admin clicked.
        if (!Symlink(Path.Combine(Share, "Public", "Link"), Path.Combine(Share, "HR"))) return; // pytest.skip
        var fs = new SimFs(new SimState(simDir), Share); // SimProvider(sim_dir).fs
        Assert.Throws<UnauthorizedAccessException>(() => fs.WriteDacl("Public\\Link", true, []));
        Assert.Throws<UnauthorizedAccessException>(() => fs.Mkdir("Public\\Link\\New"));
        Assert.False(Directory.Exists(Path.Combine(Share, "HR", "New")));
    }

    [Fact]
    public void ScanDepthDefaultIsLimited()
    {
        Assert.Equal(20, new Config().ScanDepth);
    }

    [Theory]
    [InlineData("Cafe\u0301")] // NFD: e + combining accent
    [InlineData("D\u0430ta")] // Cyrillic a
    public void NfdAndMixedScriptNamesAreAFinding(string name)
    {
        Directory.CreateDirectory(Path.Combine(Share, "Public", name));
        var s = new SimProvider(simDir).Scan();
        Assert.Contains(Rights.Findings(s), x => x.Path == $"Public\\{name}" && x.Text.Contains("lookalike"));
    }

    [Fact]
    public void DeepTreeDoesNotHitTheRecursionLimit()
    {
        // Python lowers sys.setrecursionlimit to 120 here; C# has no such limit to lower, the walk is iterative.
        var p = Path.Combine(Share, "Public");
        for (var i = 0; i < 60; i++) p = Path.Combine(p, "d");
        Directory.CreateDirectory(p);
        var s = new SimProvider(simDir, scanDepth: 0).Scan();
        Assert.Contains("Public" + string.Concat(Enumerable.Repeat("\\d", 60)), s.Folders.Keys);
    }

    [Fact]
    public void WriteRefusedAboveALinkSeenInTheSubtree()
    {
        // Inheritance propagation walks the subtree by name; a link below could carry it outside the share.
        if (!Symlink(Path.Combine(Share, "Public", "Link"), Tmp)) return; // pytest.skip
        var p = new SimProvider(simDir);
        p.Scan();
        var e = Assert.Throws<UnauthorizedAccessException>(() => p.SetFolderAcl("Public", true, []));
        Assert.Matches("Link", e.Message);
        p.SetFolderAcl("HR", true, []); // unrelated subtree still writable
    }

    [Fact]
    public void FolderAclRefusesUnaddressablePath()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new SimProvider(simDir).FolderAcl("Public\\End."));
    }
}
