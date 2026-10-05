// Desired state (baseline) in the ACL model and reconciliation: accept or discard outside changes.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;
using static Owlseye.Providers.Demo;

namespace Owlseye.Tests;

public sealed class DesiredTests : TestBase
{
    const string SHARE = @"\\fs01\Data";
    const string OPS = "Operations";

    static void AssertDesired(Desired expected, Desired? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Cells, actual.Cells);
        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Protected, actual.Protected);
    }

    // --- File ---------------------------------------------------------------------------------
    [Fact]
    public void FileNameIsCleanShareName()
    {
        Assert.Equal("desired-fs01_data.json", BaselineStore.FileName(SHARE));
        Assert.Equal("desired-fs01_data.json", BaselineStore.FileName(@"\\FS01\Data\\"));
        Assert.Equal("desired-e_share.json", BaselineStore.FileName(@"E:\Share"));
        Assert.Equal("desired-fs01.corp.local_data.json", BaselineStore.FileName(@"\\fs01.corp.local\Data$"));
    }

    [Fact]
    public void StoreRoundtripAndOneFilePerShare()
    {
        var store = new BaselineStore(Tmp);
        Assert.Null(store.Load(SHARE));
        var d = new Desired
        {
            Cells = new() { [("S-1", "A")] = "R", [("S-2", "")] = "R|" },
            Names = new() { ["S-1"] = "X\\G1", ["S-2"] = "X\\G2" },
            Protected = ["A"],
        };
        store.Save(SHARE, d, actor: "X\\admin");
        store.Save(@"\\fs02\Other", new Desired { Cells = new() { [("S-3", "B")] = "W" }, Names = new() { ["S-3"] = "X\\G3" } }, actor: "X\\admin");
        AssertDesired(d, new BaselineStore(Tmp).Load(SHARE));
        Assert.Equal(2, Directory.GetFiles(Tmp, "desired-*.json").Length);
    }

    [Fact]
    public void StoreRefusesFileOfOtherShare()
    {
        new BaselineStore(Tmp).Save(@"\\fs01\a_b", new Desired { Cells = new() { [("S-1-5-21-9", "A")] = "R" } }, actor: "A");
        var e = Assert.Throws<BaselineError>(() => new BaselineStore(Tmp).Load(@"\\fs01\a\b"));
        Assert.Contains("belongs to", e.Message);
    }

    [Fact]
    public void StoreUpdateIsReadModifyWrite()
    {
        BaselineStore a = new(Tmp), b = new(Tmp);
        a.Save(SHARE, new Desired { Cells = new() { [("S-1-5-21-1", "A")] = "R" } }, actor: "A");
        b.Update(SHARE, d => new Desired { Cells = new(d.Cells) { [("S-1-5-21-2", "B")] = "W" }, Names = d.Names, Protected = [.. d.Protected, "P"] }, actor: "B");
        a.Update(SHARE, d => new Desired { Cells = new(d.Cells) { [("S-1-5-21-1", "A")] = "W" }, Names = d.Names, Protected = d.Protected }, actor: "A");
        Assert.Equal(new Dictionary<(string, string), string> { [("S-1-5-21-1", "A")] = "W", [("S-1-5-21-2", "B")] = "W" }, a.Load(SHARE)!.Cells);
        Assert.Equal(new HashSet<string> { "P" }, a.Load(SHARE)!.Protected);
    }

    [Fact]
    public void MemoryStoreWithoutADirectory()
    {
        var store = new BaselineStore(null);
        var d = new Desired { Cells = new() { [("S-1", "A")] = "R" }, Names = new() { ["S-1"] = "X\\G" }, Protected = ["A"] };
        store.Save(SHARE, d, actor: "A");
        AssertDesired(d, store.Load(SHARE));
    }

    [Fact]
    public void LegacyAgdlpFileCountsAsMissing()
    {
        var path = Path.Combine(Tmp, BaselineStore.FileName(SHARE));
        var legacy = new JsonObject
        {
            ["share"] = SHARE, ["groups"] = new JsonObject { ["g"] = new JsonArray("m") }, ["protected"] = new JsonArray(),
        };
        File.WriteAllText(path, legacy.ToJsonString());
        Assert.Null(new BaselineStore(Tmp).Load(SHARE));
    }

    [Fact]
    public void BadValuesAreRefused()
    {
        var path = Path.Combine(Tmp, BaselineStore.FileName(SHARE));
        var bad = new JsonObject
        {
            ["share"] = SHARE, ["cells"] = new JsonArray(new JsonArray("S", "A", "M")), ["names"] = new JsonObject(), ["protected"] = new JsonArray(),
        };
        File.WriteAllText(path, bad.ToJsonString());
        Assert.Throws<BaselineError>(() => new BaselineStore(Tmp).Load(SHARE));
    }

    [Fact]
    public void DefaultLocationIsAppdata()
    {
        Assert.Equal(Paths.DataDir(), new Config { Provider = "sim" }.BaselineDir);
        Assert.Equal(AppData, Paths.DataDir());
        Assert.Null(new Config { Provider = "demo" }.BaselineDir);
    }

    // --- Abgleich ---------------------------------------------------------------------------------

    /// <summary>A seeded sim directory (created lazily: the file tests above do not need it).</summary>
    string SimDir => simDir ??= SimSeed.Seed(Path.Combine(Tmp, "sim"));

    string? simDir;

    static void Edit(string simDir, Action<JsonObject> change)
    {
        var p = Path.Combine(simDir, SimState.FileName);
        var s = (JsonObject)JsonNode.Parse(File.ReadAllText(p))!;
        change(s);
        File.WriteAllText(p, s.ToJsonString());
    }

    static JsonArray Ace(string sid) => new(0, 3, 0x1200A9, sid);

    static JsonArray Aces(JsonObject s, string path) => s["acls"]![path]!["aces"]!.AsArray();

    [Fact]
    public void NoDriftAgainstItself()
    {
        var s = new SimProvider(SimDir).Scan();
        Assert.Empty(Drift.Diff(s, Drift.DesiredOf(s)));
    }

    [Fact]
    public void OutsideChangesAreReported()
    {
        var s0 = new SimProvider(SimDir).Scan();
        var desired = Drift.DesiredOf(s0);

        Edit(SimDir, s =>
        {
            Aces(s, "HR").Add(Ace(Gsid("G-Management"))); // added
            s["acls"]!["Programs"]!["aces"] = new JsonArray(Aces(s, "Programs")
                .Where(a => (string)a![3]! != Gsid("G-AllUsers")).Select(a => a!.DeepClone()).ToArray());
            s["acls"]![OPS + "\\Service-Staff"]!["protected"] = true; // inheritance broken
        });
        var s = new SimProvider(SimDir).Scan();
        var got = Drift.Diff(s, desired).Select(d => (d.Change, d.Path, d.Name.Split('\\')[^1], d.Before, d.After)).ToHashSet();
        Assert.Equal(new HashSet<(string, string, string, string?, string?)>
        {
            ("added", "HR", "G-Management", null, "R"),
            ("removed", "Programs", "G-AllUsers", "R", null),
            ("broken", OPS + "\\Service-Staff", "", null, null),
        }, got);
        var lost = Drift.ImpactOf(s, Drift.Diff(s, desired)).Select(i => (i.User, i.Path)).ToHashSet();
        Assert.Contains(("aadams", "Programs"), lost);
    }

    [Fact]
    public void AcceptMovesDesiredState()
    {
        var s0 = new SimProvider(SimDir).Scan();
        var desired = Drift.DesiredOf(s0);
        Edit(SimDir, s => Aces(s, "HR").Add(Ace(Gsid("G-Management"))));
        var s = new SimProvider(SimDir).Scan();
        var items = Drift.Diff(s, desired);
        Assert.Empty(Drift.Diff(s, Drift.Accept(desired, items)));
    }

    [Fact]
    public void MissingFolderCanBeAcceptedOrRecreated()
    {
        var s0 = new SimProvider(SimDir).Scan();
        var desired = Drift.DesiredOf(s0);
        desired.Cells[(Gsid("G-Management"), @"Weg\Unter")] = "R";
        desired.Protected.Add(@"Weg\Unter");
        var items = Drift.Diff(s0, desired);
        Assert.Equal([("folder_gone", @"Weg\Unter")], items.Select(d => (d.Change, d.Path)).ToList());
        Assert.Empty(Drift.Diff(s0, Drift.Accept(desired, items)));
        var r = Drift.RevertOf(s0, desired, items);
        Assert.Equal(new Dictionary<string, string> { ["Weg"] = "", [@"Weg\Unter"] = "Weg" }, r.NewFolders); // missing parent folder included
        Assert.Equal(new Dictionary<(string, string), string?> { [(Gsid("G-Management"), @"Weg\Unter")] = "R" }, r.Cells);
        Assert.Equal(new Dictionary<string, bool> { [@"Weg\Unter"] = true }, r.Folders);
    }

    /// <summary>State on the sim with the log in the temp folder, first scan done, and a Session on it.</summary>
    static Session Client(string simDir, string tmp)
    {
        var st = new State(new Config { Provider = "sim", Audit = Path.Combine(tmp, "a.jsonl") }, new SimProvider(simDir));
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return new Session(st);
    }

    static List<string?> Kinds(Session c) => c.St.Audit.Entries().Select(e => e.Str("kind")).ToList();

    [Fact]
    public void FirstStartTakesCurrentState()
    {
        var c = Client(SimDir, Tmp);
        Assert.True(File.Exists(Path.Combine(AppData, BaselineStore.FileName(SHARE))));
        Assert.Equal(["baseline_init"], Kinds(c));
    }

    [Fact]
    public void DiscardGoesThroughPreviewAndRestores()
    {
        var c = Client(SimDir, Tmp);
        Edit(SimDir, s => Aces(s, "HR").Add(Ace(Gsid("G-Management"))));
        c.Rescan();
        var page = c.DriftPage();
        Assert.Contains(page.Items, d => d.Change == "added"); // shown as "entry added"
        var keys = DriftKeys(page);
        var o = c.DriftRevert(keys); // leads to the preview
        Assert.Equal("/preview", o.Url);
        var p = c.Preview();
        // assert "Preview" in p and "G-Management" in p
        Assert.Null(p.Error);
        Assert.Contains(p.Plan!.AclOps.SelectMany(x => x.Changes), x => x.Name.Split('\\')[^1] == "G-Management");
        c.Apply("", Phash(p));
        Assert.DoesNotContain(Gsid("G-Management"), AclState(SimDir, "HR")["aces"]!.AsArray().Select(a => (string)a![3]!));
        Assert.Empty(c.St.Drift);
    }

    [Fact]
    public void DiscardRecreatesDeletedFolderWithDesiredRights()
    {
        var path = OPS + "\\Sales-Lead";
        var c = Client(SimDir, Tmp);
        Directory.Delete(Path.Combine(SimDir, SimProvider.ShareDir, OPS, "Sales-Lead"), recursive: true);
        Edit(SimDir, s => s["acls"]!.AsObject().Remove(path)); // deleted as in Explorer
        c.Rescan();
        var page = c.DriftPage();
        // the page shows a folder_gone row as "folder missing" and "recreates it"
        Assert.Equal("", page.BaselineError);
        Assert.Contains(page.Items, d => d.Change == "folder_gone");
        var keys = DriftKeys(page);
        Assert.Equal("/preview", c.DriftRevert(keys).Url);
        var p = c.Preview();
        Assert.NotEmpty(p.Plan!.CreateOps); // assert "New folders" in p
        c.Apply("", Phash(p));
        Assert.True(Directory.Exists(Path.Combine(SimDir, SimProvider.ShareDir, OPS, "Sales-Lead")));
        var entry = AclState(SimDir, path);
        Assert.True(entry.Bool("protected"));
        var sids = entry["aces"]!.AsArray().Select(a => (string)a![3]!).ToHashSet();
        Assert.Subset(sids, new HashSet<string> { Gsid("G-Management"), Gsid("G-Sales-Lead"), "S-1-5-18", "S-1-5-32-544" });
        Assert.Empty(c.St.Drift);
    }

    [Fact]
    public void AcceptThroughAppIsLogged()
    {
        var c = Client(SimDir, Tmp);
        Edit(SimDir, s => Aces(s, "HR").Add(Ace(Gsid("G-Management"))));
        c.Rescan();
        var keys = DriftKeys(c.DriftPage());
        var r = c.DriftAccept(keys, "ok so");
        Assert.Contains("kept as the new desired state", r.Message);
        Assert.Empty(c.St.Drift);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE))))!;
        Assert.Contains(saved["cells"]!.AsArray(), x => JsonNode.DeepEquals(x, new JsonArray(Gsid("G-Management"), "HR", "R")));
    }

    [Fact]
    public void PageShowsFileAndDeleteResetsToCurrent()
    {
        var c = Client(SimDir, Tmp);
        var page = c.DriftPage();
        // the page names the file and when it was last written
        Assert.Equal(Path.Combine(AppData, BaselineStore.FileName(SHARE)), page.Info.Path);
        Assert.True(page.Info.Exists && !page.Info.Legacy && page.Info.Updated != ""); // renders "last written {updated} by {by}"
        Edit(SimDir, s => Aces(s, "HR").Add(Ace(Gsid("G-Management"))));
        c.Rescan();
        Assert.NotEmpty(c.St.Drift);
        var r = c.DriftReset("new");
        Assert.Contains("Desired state deleted", r.Message);
        Assert.Empty(c.St.Drift); // actual is now desired
        var kinds = Kinds(c);
        Assert.Equal(["baseline_init", "baseline_reset"], kinds.Take(2).ToList());
        // the log shows a baseline_reset entry as "Desired state deleted"
        Assert.Contains(c.AuditPage().Entries, e => e.Str("kind") == "baseline_reset");
    }

    [Fact]
    public void DeleteRepairsCorruptFile()
    {
        Directory.CreateDirectory(AppData);
        File.WriteAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE)), "{broken");
        var c = Client(SimDir, Tmp);
        Assert.NotEqual("", c.DriftPage().BaselineError); // the drift page says the file cannot be read
        c.DriftReset("");
        Assert.Equal("", c.St.BaselineError);
        Assert.NotEmpty(JsonNode.Parse(File.ReadAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE))))!["cells"]!.AsArray());
    }

    [Fact]
    public void CorruptBaselineIsNotOverwritten()
    {
        Directory.CreateDirectory(AppData);
        File.WriteAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE)), "{broken");
        var c = Client(SimDir, Tmp);
        Assert.NotEqual("", c.DriftPage().BaselineError); // the drift page says the file cannot be read
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE))));
    }

    // --- Several windows, restarts, inheritance ------------------------------------------------
    static JsonObject AclState(string simDir, string path) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(simDir, SimState.FileName)))!["acls"]![path]!.AsObject();

    /// <summary>An outside change: someone adds G-Management with R in Explorer.</summary>
    static void AddEntry(string simDir, string path) => Edit(simDir, s => Aces(s, path).Add(Ace(Gsid("G-Management"))));

    /// <summary>The checkboxes (name="k") on the drift page: only rendered when the desired state is readable.</summary>
    static List<string> DriftKeys(DriftView page) => page.BaselineError != "" ? [] : page.Items.Select(d => d.Key).ToList();

    static List<string> DriftKeys(Session c) => DriftKeys(c.DriftPage());

    /// <summary>The hidden phash field: only on a preview without error and with changes.</summary>
    static string Phash(PreviewView p)
    {
        Assert.Null(p.Error);
        Assert.False(p.Plan!.Empty);
        return p.Phash;
    }

    static void ApplyPending(Session c) => c.Apply("", Phash(c.Preview()));

    [Fact]
    public void OutsideChangeIsFoundOnStartAndFlaggedOnTheMatrix()
    {
        Client(SimDir, Tmp); // the first start records the desired state
        AddEntry(SimDir, "HR");
        var c = Client(SimDir, Tmp); // restart
        Assert.Single(DriftKeys(c));
        Assert.True(c.Matrix().DriftCount > 0); // the matrix points to the outside changes
    }

    [Fact]
    public void AcceptSelectedKeepsTheOtherChangesInTheList()
    {
        var c = Client(SimDir, Tmp);
        AddEntry(SimDir, "HR");
        AddEntry(SimDir, "Public");
        c.Rescan();
        var keys = DriftKeys(c);
        Assert.Equal(2, keys.Count);
        var r = c.DriftAccept([keys[0]], "T-7");
        Assert.Contains("1 outside change(s) kept", r.Message);
        Assert.Equal(keys[1..], DriftKeys(c));
        var entry = c.St.Audit.Entries()[0];
        Assert.Equal(("drift_accept", "T-7", 1), (entry.Str("kind"), entry.Str("reason"), entry.Arr("accepted")!.Count));
    }

    [Fact]
    public void OwnApplyIsNotDriftForAnotherAdmin()
    {
        Session a = Client(SimDir, Tmp), b = Client(SimDir, Tmp); // two admins, same desired-state file
        a.SetCell(Gsid("G-Management"), "HR", "R");
        ApplyPending(a);
        Assert.Empty(a.St.Drift);
        b.Rescan();
        Assert.Empty(b.St.Drift);
    }

    [Fact]
    public void OwnApplyKeepsUnrelatedDrift()
    {
        var c = Client(SimDir, Tmp);
        AddEntry(SimDir, "Public");
        c.Rescan();
        c.SetCell(Gsid("G-Management"), "HR", "R");
        ApplyPending(c);
        Assert.Equal([("added", "Public")], c.St.Drift.Select(d => (d.Change, d.Path)).ToList());
    }

    [Fact]
    public void FirstDesiredStateIsNotOverwrittenByASecondStart()
    {
        Client(SimDir, Tmp); // admin A records the desired state
        AddEntry(SimDir, "HR"); // then an outside change
        // Admin B starts at the same time as A: A's file is not there when B first reads it and appears while B waits
        // for the desired-state lock (A still writing); B's read under the lock then sees it.
        var file = Path.Combine(AppData, BaselineStore.FileName(SHARE));
        var aside = file + ".aside";
        File.Move(file, aside);
        var st = new State(new Config { Provider = "sim", Audit = Path.Combine(Tmp, "a.jsonl") }, new SimProvider(SimDir));
        var waited = false;
        using (var lf = FileLock.OpenShared(Path.ChangeExtension(file, ".lock")))
        {
            var held = FileLock.Acquire(lf);
            var t = new Thread(st.Load) { IsBackground = true };
            t.Start();
            var until = Environment.TickCount64 + 8_000;
            while (t.IsAlive && Environment.TickCount64 < until) // B read no desired state and now retries the lock
            {
                if (st.Progress.Now.Phase == "Evaluating the rights" && t.ThreadState.HasFlag(ThreadState.WaitSleepJoin))
                {
                    waited = true;
                    break;
                }
                Thread.Sleep(10);
            }
            File.Move(aside, file);
            held.Dispose();
            t.Join();
        }
        Assert.True(waited, "admin B did not wait for the desired-state lock (stale read not reproduced)");
        Assert.True(st.Ready, st.LoadError);
        var b = new Session(st);
        Assert.Single(DriftKeys(b)); // the change stays visible, the desired state was kept
    }

    [Fact]
    public void OutsideInheritanceChangesAreReportedAndCanBeAcceptedOrReverted()
    {
        string broken = $@"{OPS}\Service-Staff", restored = $@"{OPS}\Sales-Lead";
        var desired = Drift.DesiredOf(new SimProvider(SimDir).Scan());

        Edit(SimDir, s =>
        {
            s["acls"]![broken]!["protected"] = true; // inheritance removed in Explorer
            s["acls"]![restored]!["protected"] = false; // inheritance turned back on
        });
        var s = new SimProvider(SimDir).Scan();
        var items = Drift.Diff(s, desired);
        Assert.Equal(new HashSet<(string, string)> { ("broken", broken), ("restored", restored) }, items.Select(d => (d.Change, d.Path)).ToHashSet());
        Assert.Equal(new Dictionary<string, bool> { [broken] = false, [restored] = true }, Drift.RevertOf(s, desired, items).Folders);
        Assert.Empty(Drift.Diff(s, Drift.Accept(desired, items)));
    }

    [Fact]
    public void DiscardRestoresInheritanceThroughPreview()
    {
        var c = Client(SimDir, Tmp);
        var path = $@"{OPS}\Service-Staff";
        Edit(SimDir, s => s["acls"]![path]!["protected"] = true);
        c.Rescan();
        var page = c.DriftPage();
        Assert.Contains(page.Items, d => d.Change == "broken"); // shown as "inheritance broken"
        Assert.Equal("/preview", c.DriftRevert(DriftKeys(page)).Url);
        var p = c.Preview();
        // assert "restore inheritance" in p (an op that is not a clear and turns inheritance back on)
        Assert.Contains(p.Plan!.AclOps, o => !o.Cleared && o.ProtectedBefore != o.ProtectedAfter && !o.ProtectedAfter);
        ApplyPending(c);
        Assert.False(AclState(SimDir, path).Bool("protected"));
        Assert.Empty(c.St.Drift);
    }

    [Fact]
    public void AcceptedInheritanceChangeIsSaved()
    {
        var c = Client(SimDir, Tmp);
        var path = $@"{OPS}\Service-Staff";
        Edit(SimDir, s => s["acls"]![path]!["protected"] = true);
        c.Rescan();
        c.DriftAccept(DriftKeys(c), "");
        Assert.Empty(c.St.Drift);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(AppData, BaselineStore.FileName(SHARE))))!;
        Assert.Contains(path, saved["protected"]!.AsArray().Select(x => (string)x!));
        // the log shows the kept "broken" item as "inheritance broken"
        Assert.Contains(c.AuditPage().Entries, e => e.Str("kind") == "drift_accept"
            && (e.Arr("accepted") ?? []).Any(d => d.Str("change") == "broken"));
    }

    [Fact]
    public void OwnInheritanceChangeIsNotDrift()
    {
        var c = Client(SimDir, Tmp);
        var path = $@"{OPS}\Service-Staff";
        c.SetInheritance(path, inherit: false); // inherit=0
        ApplyPending(c);
        Assert.True(AclState(SimDir, path).Bool("protected"));
        Assert.Empty(c.St.Drift);
        Assert.Empty(Client(SimDir, Tmp).St.Drift); // second window, same desired state
    }
}
