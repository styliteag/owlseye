// Port of backend/tests/test_acl_app.py.
// Web app in the ACL model: set cells, preview, apply against the sim, conflicts, undo, columns.
// The routes are Session methods now; HTTP 400/404 is a UserError, a redirect with ?msg= an Outcome, and the
// rendered HTML is checked through the view models the Blazor UI renders.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class AclAppTests : TestBase
{
    const string OPS = "Operations";

    static readonly Regex German =
        new(@"\b(Ändern|Lesen|Vorschau|Verwerfen|Benutzer|Ordner|Abweichung|Protokoll|Gruppe|Rechte)\b");

    static string G(string sam) => Demo.Gsid(sam);

    /// <summary>sim_dir fixture.</summary>
    string SimDir() => SimSeed.Seed(Path.Combine(Tmp, "sim"));

    /// <summary>client(): create_app(cfg, provider, token) = State + Load + Session.</summary>
    (State St, Session S) Client(IProvider provider)
    {
        var cfg = new Config { Provider = provider.Name, Audit = Path.Combine(Tmp, "a.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, provider);
        st.Load();
        return (st, new Session(st));
    }

    /// <summary>The phash the preview page carried in its form.</summary>
    static string PreviewHash(Session s) => s.Preview().Phash;

    static Outcome Apply(Session s, string reason = "") => s.Apply(reason, PreviewHash(s));

    static JsonObject Acl(string simDir, string path) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(simDir, "state.json")))!["acls"]![path]!.AsObject();

    /// <summary>[type, flags, mask, sid] in acl["aces"]</summary>
    static bool HasAce(JsonObject acl, int type, int flags, long mask, string sid) =>
        acl["aces"]!.AsArray().Any(a => (int)a![0]! == type && (int)a[1]! == flags && (long)a[2]! == mask && (string)a[3]! == sid);

    static HashSet<string> AceSids(JsonObject acl) => acl["aces"]!.AsArray().Select(a => (string)a![3]!).ToHashSet();

    /// <summary>Edit state.json by hand, as someone in Explorer would change an ACL.</summary>
    static void EditSim(string simDir, Action<JsonObject> edit)
    {
        var p = Path.Combine(simDir, "state.json");
        var s = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        edit(s);
        File.WriteAllText(p, s.ToJsonString());
    }

    static UserError Refused(Action a, int status = 400)
    {
        var e = Assert.Throws<UserError>(a);
        Assert.Equal(status, e.Status);
        return e;
    }

    static CellInfo CellAt(MatrixView m, string sid, string path)
    {
        var col = m.Columns.ToList().FindIndex(p => p.Sid == sid);
        Assert.True(col >= 0, $"no column {sid}");
        return m.Rows.Single(r => r.Folder.Path == path).Cells[col];
    }

    static IEnumerable<CellInfo> AllCells(MatrixView m) => m.Rows.SelectMany(r => r.Cells);

    static List<string> Rows(MatrixView m) => m.Rows.Select(r => r.Folder.Path).ToList();

    static List<string?> Kinds(State st) => st.Audit.Entries().Select(e => e.Str("kind")).ToList();

    static IEnumerable<JsonObject> AclOps(JsonObject entry) => (entry.Arr("acl_ops") ?? []).OfType<JsonObject>();

    /// <summary>Entries the log page offers "Undo…" for (audit.html: acl_ops in the current format).</summary>
    static List<string> UndoIds(AuditView a) =>
        a.Entries.Where(e => e.Arr("acl_ops") is { Count: > 0 } ops && ops[0].Has("protected_before")).Select(e => e.Str("id")!).ToList();

    /// <summary>Provider with hooks before a write (monkeypatch in the Python tests); a hook may throw.</summary>
    sealed class Hooked(IProvider inner) : IProvider
    {
        public Action<string>? BeforeCreate { get; set; }
        public Action<string>? BeforeSetAcl { get; set; }

        public string Name => inner.Name;
        public string Share => inner.Share;
        public string WhoAmI() => inner.WhoAmI();
        public Snapshot Scan(Progress? progress = null) => inner.Scan(progress);
        public (bool Protected, List<Ace> Aces) FolderAcl(string path) => inner.FolderAcl(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public List<Principal> FindGroups(string q) => inner.FindGroups(q);

        public void CreateFolder(string path)
        {
            BeforeCreate?.Invoke(path);
            inner.CreateFolder(path);
        }

        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces)
        {
            BeforeSetAcl?.Invoke(path);
            inner.SetFolderAcl(path, isProtected, aces);
        }
    }

    [Fact(Skip = "No HTTP in the native app (port-spec X-1): the token cookie and the CSRF token protected the local web "
        + "server against other local processes and web pages. The Blazor UI calls Session in-process; there is no "
        + "endpoint to sign in to or to forge a request against.")]
    public void RequiresTokenAndCsrf()
    {
    }

    [Fact]
    public void MatrixHasGroupsAsColumnsAndListEntries()
    {
        var (_, s) = Client(new DemoProvider());
        var m = s.Matrix();
        var names = m.Columns.Select(p => p.Short).ToList();
        Assert.Contains("G-Sales-Lead", names);
        Assert.Contains("P-Payroll", names);
        Assert.Contains(AllCells(m), c => c.Display().Text == "R|"); // ">R|</button>" in page
        // "SYSTEM" not in page
        Assert.DoesNotContain(m.Columns, p => p.Name.Contains("SYSTEM") || M.Hidden.Contains(p.Sid));
        Assert.DoesNotContain(AllCells(m), c => c.Tip.Contains("SYSTEM"));
    }

    [Fact]
    public void SetCellShowsPendingAndAutomaticEntries()
    {
        var (_, s) = Client(new DemoProvider());
        s.SetCell(G("G-Interns"), $@"{OPS}\Sales-QA", "R"); // status 200: no UserError
        var m = s.Matrix();
        Assert.Equal(1, m.PendingCount); // "1 pending change"
        Assert.Contains(AllCells(m), c => c.Display().Css == "R pend"); // 'c R pend'
        Assert.Contains(AllCells(m), c => c.Display().Css == "RL auto"); // 'c RL auto'
        // "automatic R|" in the preview: the tag of an automatic change
        Assert.Contains(s.Preview().Plan!.AclOps.SelectMany(o => o.Changes), c => c.Auto && c.After == "R|");
    }

    [Fact]
    public void ClickCyclesAndBadValuesAreRefused()
    {
        var (st, s) = Client(new DemoProvider());
        s.SetCell(G("G-HR"), "Public", null); // nothing -> R
        var kv = Assert.Single(st.Pending);
        Assert.Equal((G("G-HR"), "Public"), kv.Key);
        Assert.Equal("R", kv.Value);
        Refused(() => s.SetCell(G("G-HR"), "Public", "M"));
        Refused(() => s.SetCell("S-1-5-18", "Public", "R"));
        Refused(() => s.SetCell(G("G-Management"), "Nope", "R"));
    }

    [Fact]
    public void ExplicitRightSetsPendingAndGoingBackClearsIt()
    {
        var (st, s) = Client(new DemoProvider());
        var (sid, key) = (G("G-HR"), (G("G-HR"), "HR")); // has W on HR
        s.SetCell(sid, "HR", "R");
        var kv = Assert.Single(st.Pending);
        Assert.Equal((key, "R"), (kv.Key, kv.Value));
        s.SetCell(sid, "HR", "W"); // back to actual: no change
        Assert.Empty(st.Pending);
        s.SetCell(sid, "HR", ""); // empty = remove the entry
        kv = Assert.Single(st.Pending);
        Assert.Equal(key, kv.Key);
        Assert.Null(kv.Value);
    }

    [Fact]
    public void CellResponseUpdatesThePanelOutOfBand()
    {
        // Python: the POST /cell response carried the panel too (id="panel" hx-swap-oob="true"). Natively, SetCell
        // raises State.Changed and the UI renders the matrix and the open panel again from the same state.
        var (st, s) = Client(new DemoProvider());
        var changed = 0;
        st.Changed += () => changed++;
        s.SetCell(G("G-HR"), "HR", "R");
        Assert.True(changed > 0);
        var panel = s.CellPanel(G("G-HR"), "HR");
        Assert.Equal("pending", panel.Info.Kind);
        Assert.Equal("Pending: W → R", panel.Info.Tip);
        Assert.Equal("pending", CellAt(s.Matrix(), G("G-HR"), "HR").Kind);
    }

    [Fact]
    public void PanelOfADirectCellOffersEveryRight()
    {
        var (_, s) = Client(new DemoProvider());
        var page = s.CellPanel(G("G-HR"), "HR");
        Assert.Equal("direct", page.Info.Kind); // "Entry here"
        Assert.StartsWith("Entry here", page.Info.Tip);
        Assert.Equal([null, "R|", "R", "W|", "W"], page.Options);
        string[] labels = ["No access", "R| List", "R Read", "W| Modify", "W Modify"];
        foreach (var (label, option) in labels.Zip(page.Options))
            Assert.StartsWith(label, Labels.Label(option)); // title="{label}…" on each choice
    }

    [Fact]
    public void PanelOfAnInheritedCellPointsToBreakInheritance()
    {
        var (_, s) = Client(new DemoProvider());
        var page = s.CellPanel(G("G-Sales-Staff"), $@"{OPS}\Sales-Staff\2025");
        Assert.Equal("inherited", page.Info.Kind); // "Inherited from"
        Assert.Equal($@"{OPS}\Sales-Staff", page.Info.Source);
        Assert.Contains($@"Inherited from {OPS}\Sales-Staff", page.Info.Tip);
        Assert.True(page.CanBreak); // "Break inheritance on 2025": the way to restrict an inherited right
        Assert.Equal("2025", page.F.Name);
    }

    [Fact]
    public void BlockedCellIsMarkedInTheMatrixAndExplainedInThePanel()
    {
        var (_, s) = Client(new DemoProvider());
        var (sid, path) = (G("G-AllUsers"), @"Programs\Payroll"); // inheritance broken, R on Programs
        var hit = CellAt(s.Matrix(), sid, path);
        Assert.Equal("blocked", hit.Display().Css); // class="c blocked"
        Assert.Contains("Blocked: R Read on Programs", hit.Tip);
        var page = s.CellPanel(sid, path);
        Assert.Equal("blocked", page.Info.Kind);
        Assert.True(page.CanRestore); // "Restore inheritance on Payroll"
        Assert.Equal("Payroll", page.F.Name);
    }

    [Fact]
    public void NonStandardEntryIsMarkedWithAStar()
    {
        var p = new DemoProvider();
        var (prot, aces) = p.FolderAcl("HR");
        p.SetFolderAcl("HR", prot, [.. aces.Select(a => a.Sid == G("G-HR") ? a with { Mask = M.WriteNoDelete } : a)]); // write, no delete
        var (_, s) = Client(p);
        var hit = CellAt(s.Matrix(), G("G-HR"), "HR");
        Assert.Equal(("W odd", "W*"), hit.Display()); // class="c W odd" … >W*<
        Assert.Contains("non-standard entry", hit.Tip);
    }

    [Fact]
    public void FullControlIsShownAsF()
    {
        var (_, s) = Client(new DemoProvider());
        var hit = CellAt(s.Matrix(), G("G-IT"), @"Programs\Payroll"); // full control
        Assert.Equal(("W odd", "F"), hit.Display());
        Assert.Equal("Entry here: Full control", hit.Tip);
    }

    [Fact]
    public void DepthSettingShowsDeeperFoldersOnlyWithDeviations()
    {
        var (st, s) = Client(new DemoProvider());
        var shown = Rows(s.Matrix());
        Assert.Contains($@"{OPS}\Sales-Staff\2025", shown); // level 3: normal
        Assert.Contains($@"{OPS}\Sales-Staff\2025\Offsite", shown); // level 4 with its own entry: deviation
        s.SetDepth(2);
        var page = s.Matrix();
        Assert.Equal(2, page.MaxLevel); // 'value="2" selected'
        Assert.Equal(2, st.Cfg.MaxLevel);
        Assert.Contains($@"{OPS}\Sales-Staff\2025", Rows(page)); // only as the path to the deviation
        // 'class="l3 deep way"' and 'class="l4 deep odd"'
        var way = page.Rows.Single(r => r.Folder.Path == $@"{OPS}\Sales-Staff\2025");
        Assert.Equal((3, "way"), (way.Folder.Level, way.Deep));
        var odd = page.Rows.Single(r => r.Folder.Path == $@"{OPS}\Sales-Staff\2025\Offsite");
        Assert.Equal((4, "odd"), (odd.Folder.Level, odd.Deep));
        Assert.Contains(@"Programs\CRM", Rows(page)); // level 2
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(AppData, "settings.json")))!.AsObject();
        Assert.Equal("depth", Assert.Single(settings).Key); // == {"depth": 2}
        Assert.Equal(2, settings.Long("depth"));
        // the remembered depth applies at the next start
        Assert.Equal(2, Client(new DemoProvider()).St.Cfg.MaxLevel);
        Refused(() => s.SetDepth(0));
    }

    [Fact]
    public void FoldersBelowTheDepthWithoutDeviationAreHidden()
    {
        var p = new DemoProvider();
        p.CreateFolder(@"HR\Sitzungen");
        p.CreateFolder(@"HR\Sitzungen\2025");
        var (_, s) = Client(p);
        s.SetDepth(1);
        var shown = Rows(s.Matrix());
        Assert.Contains("HR", shown);
        Assert.DoesNotContain(@"HR\Sitzungen", shown);
    }

    [Fact]
    public void ApplyWritesAclAndUndoReverts()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        s.SetCell(G("G-Interns"), $@"{OPS}\Sales-QA", "W");
        var r = Apply(s, "T-1");
        Assert.Contains("3 changes applied", r.Message);
        Assert.True(HasAce(Acl(sim, $@"{OPS}\Sales-QA"), 0, 3, M.Modify, G("G-Interns")));
        Assert.True(HasAce(Acl(sim, OPS), 0, 0, 0x1200A9, G("G-Interns")));
        var audit = s.AuditPage();
        Assert.Contains(audit.Entries, e => e.Str("reason") == "T-1"); // "T-1" in audit
        // "(auto)" in audit: a change marked automatic
        Assert.Contains(audit.Entries.SelectMany(AclOps).SelectMany(o => (o.Arr("changes") ?? []).OfType<JsonObject>()),
            c => c.Bool("auto") == true);
        Assert.Equal(0, s.Matrix().DriftCount); // "outside owlseye" not in the matrix: own apply is not a deviation
        var eid = UndoIds(audit)[0]; // the first action="/undo/…" on the log page
        var back = s.Undo(eid);
        Assert.Equal("/preview", back.Url);
        // "G-Interns" in the preview
        Assert.Contains(s.Preview().Plan!.AclOps.SelectMany(o => o.Changes), c => c.Name.EndsWith(@"\G-Interns"));
        Apply(s);
        Assert.DoesNotContain(G("G-Interns"), AceSids(Acl(sim, OPS)));
    }

    [Fact]
    public void BreakInheritanceThroughAppApplyAndUndo()
    {
        var sim = SimDir();
        var (st, s) = Client(new SimProvider(sim));
        var path = $@"{OPS}\Service-Staff";
        s.SetInheritance(path, inherit: false);
        // "break inheritance" in the preview
        Assert.Contains(s.Preview().Plan!.AclOps, o => o.Path == path && !o.ProtectedBefore && o.ProtectedAfter);
        Assert.Contains("changes applied", Apply(s, "T-9").Message);
        Assert.True(Acl(sim, path).Bool("protected"));
        var entry = st.Audit.Entries()[0];
        Assert.Equal([path], AclOps(entry).Select(o => o.Str("path")));
        s.Undo(entry.Str("id")!);
        // "restore inheritance" in the preview
        Assert.Contains(s.Preview().Plan!.AclOps, o => o.Path == path && o.ProtectedBefore && !o.ProtectedAfter);
        Apply(s);
        Assert.False(Acl(sim, path).Bool("protected"));
    }

    [Fact]
    public void AclChangedAfterPreviewIsAConflict()
    {
        var sim = SimDir();
        var (st, s) = Client(new SimProvider(sim));
        s.SetCell(G("G-Management"), "HR", "R");
        var phash = PreviewHash(s);
        EditSim(sim, d => d["acls"]!["HR"]!["aces"]!.AsArray().Add(new JsonArray(0, 3, 0x1200A9, G("G-IT")))); // someone in Explorer
        var r = s.Apply("", phash);
        Assert.Contains("changed outside owlseye", r.Message);
        Assert.DoesNotContain(G("G-Management"), AceSids(Acl(sim, "HR")));
        Assert.Equal(["baseline_init"], Kinds(st)); // nothing was applied
    }

    /// <summary>Breaking inheritance copies the inherited entries; if the parent changed since, the copies would be stale.
    /// The child sees the change through its own inherited entries. The restore test below covers the case where it
    /// does not (protected child).</summary>
    [Fact]
    public void ParentAclChangeAfterPreviewOfABreakIsAConflict()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        var path = $@"{OPS}\Service-Staff";
        s.SetInheritance(path, inherit: false);
        var phash = PreviewHash(s);
        EditSim(sim, d => // removed in Explorer
        {
            var aces = d["acls"]![OPS]!["aces"]!.AsArray();
            foreach (var a in aces.Where(a => (string)a![3]! == G("G-Management")).ToList()) aces.Remove(a);
        });
        var r = s.Apply("", phash);
        Assert.Contains("changed outside", r.Message);
        Assert.False(Acl(sim, path).Bool("protected"));
    }

    [Fact]
    public void ParentAclChangeBeforeRestoreIsAConflict()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        var path = $@"{OPS}\Sales-Lead";
        s.SetInheritance(path, inherit: true);
        var phash = PreviewHash(s);
        EditSim(sim, d => d["acls"]![OPS]!["aces"]!.AsArray().Add(new JsonArray(0, 3, 0x1200A9, "S-1-5-11")));
        var r = s.Apply("", phash);
        Assert.Contains("changed outside", r.Message);
        Assert.True(Acl(sim, path).Bool("protected"));
    }

    [Fact]
    public void RootCellCanBeSet()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        s.SetCell(G("G-Management"), "", "R"); // status 200
        s.CellPanel(G("G-Management"), ""); // status 200
        Apply(s);
        Assert.True(HasAce(Acl(sim, ""), 0, 3, 0x1200A9, G("G-Management")));
    }

    [Fact]
    public void ClearFolderThroughApp()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        var path = $@"{OPS}\Sales-Lead";
        var panel = s.FolderPanel(path);
        Assert.True(panel.CanClear && !panel.Clearing); // ">Clear</button>"
        s.ClearFolder(path);
        panel = s.FolderPanel(path);
        Assert.True(panel.Clearing); // "Clear (pending)" and "Undo clear"
        Assert.Contains(s.Preview().Plan!.AclOps, o => o.Path == path && o.Cleared); // "back to the default"
        Apply(s);
        var acl = Acl(sim, path); // == {"protected": False, "aces": []}
        Assert.Equal(2, acl.Count);
        Assert.False(acl.Bool("protected"));
        Assert.Empty(acl["aces"]!.AsArray());
        Assert.Contains(s.AuditPage().Entries.SelectMany(AclOps), o => o.Bool("cleared") == true); // "cleared" in audit
        panel = s.FolderPanel(path);
        Assert.False(panel.CanClear || panel.Clearing); // no Clear button: nothing left to reset
    }

    [Fact]
    public void ClearTogglesAndIsRefusedWherePointless()
    {
        var (st, s) = Client(new DemoProvider());
        var path = $@"{OPS}\Sales-QA";
        s.ClearFolder(path);
        s.ClearFolder(path); // again = take back
        Assert.Empty(st.PendingClear);
        Refused(() => s.ClearFolder(""));
        Refused(() => s.ClearFolder($@"{OPS}\Sales-Staff\2025"));
    }

    [Fact]
    public void InheritanceToggleValidation()
    {
        var (_, s) = Client(new DemoProvider());
        // inherit="x" -> 400: not representable any more, SetInheritance takes a bool
        Refused(() => s.SetInheritance("", inherit: false));
    }

    [Fact]
    public void FolderPanelOffersBreakOrRestoreInheritance()
    {
        var (_, s) = Client(new DemoProvider());
        var serviceStaff = s.FolderPanel($@"{OPS}\Service-Staff");
        Assert.True(serviceStaff.CanToggle && !serviceStaff.Protected); // "Break inheritance"
        var salesLead = s.FolderPanel($@"{OPS}\Sales-Lead");
        Assert.True(salesLead.CanToggle && salesLead.Protected); // "Restore inheritance"
    }

    [Fact]
    public void InheritanceToggleMarksPendingAndTogglingBackClearsIt()
    {
        var (st, s) = Client(new DemoProvider());
        var path = $@"{OPS}\Service-Staff";
        s.SetInheritance(path, inherit: false);
        var kv = Assert.Single(st.PendingFolders);
        Assert.Equal((path, true), (kv.Key, kv.Value));
        Assert.Equal(1, s.Matrix().PendingCount); // "1 pending change"
        s.SetInheritance(path, inherit: true);
        Assert.Empty(st.PendingFolders);
    }

    [Fact]
    public void AddGroupColumnAndGiveItARight()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        const string everyone = "S-1-1-0"; // in no ACL of the demo share: no column yet
        Assert.DoesNotContain(s.Matrix().Columns, p => p.Short == "Everyone"); // ">Everyone<" not in the matrix
        Assert.Contains(s.Groups("Every").Hits, h => h.Name.Contains("Everyone"));
        s.AddColumn(everyone, "Every");
        Assert.Contains(s.Matrix().Columns, p => p.Short == "Everyone");
        s.SetCell(everyone, "HR", "R|");
        Apply(s);
        Assert.True(HasAce(Acl(sim, "HR"), 0, 0, 0x1200A9, everyone));
    }

    /// <summary>G-Interns has only one entry at level 4 (deviation): still a column so it is visible.</summary>
    [Fact]
    public void DeepGroupEntryIsAColumn()
    {
        var (_, s) = Client(new DemoProvider());
        Assert.Contains(s.Matrix().Columns, p => p.Short == "G-Interns");
    }

    [Fact]
    public void GroupSearchOffersBuiltinGroupsAndExplainsAdmins()
    {
        var (_, s) = Client(new SimProvider(SimDir()));
        Assert.Contains(s.Groups("Auth").Hits, h => h.Name.Contains("Authenticated Users"));
        var page = s.Groups("Admin");
        // "SYSTEM and Administrators are not columns" shown, "No group found" not (it shows only without the hint)
        Assert.True(page.HiddenHint);
    }

    [Fact]
    public void CreateFolderWithRight()
    {
        var sim = SimDir();
        var (_, s) = Client(new SimProvider(sim));
        s.NewFolder("HR", "Protokolle");
        s.SetCell(G("G-Management"), @"HR\Protokolle", "R");
        Assert.Equal([@"HR\Protokolle"], s.Preview().Plan!.CreateOps); // "New folders" in the preview
        Apply(s);
        Assert.True(Directory.Exists(Path.Combine(sim, "share", "HR", "Protokolle")));
        Assert.True(HasAce(Acl(sim, @"HR\Protokolle"), 0, 3, 0x1200A9, G("G-Management")));
        Assert.True(HasAce(Acl(sim, "HR"), 0, 0, 0x1200A9, G("G-Management"))); // automatic R|
    }

    [Fact]
    public void CreatingAFolderIsLoggedAndCannotBeUndone()
    {
        var sim = SimDir();
        var (st, s) = Client(new SimProvider(sim));
        s.NewFolder("HR", "Protokolle");
        Assert.Contains("changes applied", Apply(s).Message);
        var entry = st.Audit.Entries()[0];
        Assert.Equal([@"HR\Protokolle"], (entry.Arr("create_ops") ?? []).Select(n => (string)n!));
        var audit = s.AuditPage();
        Assert.Contains(audit.Entries, e => e.Str("id") == entry.Str("id")); // "folder created" (create_ops shown)
        Assert.DoesNotContain(entry.Str("id")!, UndoIds(audit)); // no /undo/<id> button
        s.Undo(entry.Str("id")!); // even by hand, undo removes no folder
        Assert.True(Directory.Exists(Path.Combine(sim, "share", "HR", "Protokolle")));
    }

    [Fact]
    public void CreateConflictsWhenTheFolderAppearsInTheMeantime()
    {
        var sim = SimDir();
        var (st, s) = Client(new SimProvider(sim));
        s.NewFolder("HR", "Messen");
        var phash = PreviewHash(s);
        Directory.CreateDirectory(Path.Combine(sim, "share", "HR", "Messen")); // someone was faster
        var r = s.Apply("", phash);
        Assert.Contains("already exists. Rescanned", r.Message); // caught before writing, not as an error while applying
        Assert.Equal(["baseline_init"], Kinds(st));
    }

    [Fact]
    public void FailedSecondCreateKeepsTheFirstAndIsLogged()
    {
        var sim = SimDir();
        var p = new Hooked(new SimProvider(sim));
        var (st, s) = Client(p);
        foreach (var name in new[] { "AAA", "BBB" })
            s.NewFolder("HR", name);
        p.BeforeCreate = path =>
        {
            if (path.EndsWith("BBB")) throw new IOException("disk full");
        };
        var r = Apply(s);
        Assert.Contains("Error after 1 of 2 operations", r.Message);
        Assert.Contains("disk full", r.Message);
        Assert.True(Directory.Exists(Path.Combine(sim, "share", "HR", "AAA")));
        Assert.False(Path.Exists(Path.Combine(sim, "share", "HR", "BBB")));
        var entry = st.Audit.Entries()[0];
        Assert.Equal("error", entry.Str("status"));
        Assert.Equal([@"HR\AAA"], (entry.Arr("create_ops") ?? []).Select(n => (string)n!));
    }

    [Fact]
    public void FailedAclWriteIsLoggedWithWhatWasDone()
    {
        var sim = SimDir();
        var p = new Hooked(new SimProvider(sim));
        var (st, s) = Client(p);
        var path = $@"{OPS}\Sales-QA"; // the plan writes the root, Operations and Sales-QA, parents first
        s.SetCell(G("G-Interns"), path, "R");
        var calls = new List<string>();
        p.BeforeSetAcl = rel =>
        {
            calls.Add(rel);
            if (calls.Count == 2) throw new IOException("access denied");
        };
        Assert.Contains("Error after 1 of 3 operations", Apply(s).Message);
        var entry = st.Audit.Entries()[0];
        Assert.Equal("error", entry.Str("status"));
        Assert.Equal([""], AclOps(entry).Select(o => o.Str("path")));
        Assert.Contains(G("G-Interns"), AceSids(Acl(sim, ""))); // the first write stayed
    }

    [Fact]
    public void NewFolderInputIsOfferedOnlyAboveTheDepth()
    {
        var (_, s) = Client(new DemoProvider());
        var hr = s.FolderPanel("HR");
        Assert.True(hr.CanAdd && !hr.IsNew); // 'name="name"' (the new subfolder form)
        Assert.False(s.FolderPanel($@"{OPS}\Sales-Staff\2025").CanAdd); // level 3 = depth
    }

    [Fact]
    public void BadFolderNameIsRefused()
    {
        var (st, s) = Client(new DemoProvider());
        Refused(() => s.NewFolder("HR", "CON"));
        Assert.Empty(st.PendingNew);
    }

    [Fact]
    public void UndoRefusesOtherShareAndSurvivesBadLines()
    {
        var (_, s) = Client(new DemoProvider());
        var log = Path.Combine(Tmp, "a.jsonl");
        var entry = new JsonObject { ["id"] = "x1", ["ts"] = "t", ["share"] = @"\\fs02\Other", ["acl_ops"] = new JsonArray(), ["actor"] = "A" };
        var old = File.Exists(log) ? File.ReadAllText(log) : "";
        File.WriteAllText(log, old + entry.ToJsonString() + "\n{kaputt\n" + "{\"ts\": \"no id\"}\n");
        var audit = s.AuditPage(); // status 200
        Assert.Contains(audit.Entries, e => e.Str("id") == "x1");
        Refused(() => s.Undo("x1"));
    }

    [Fact]
    public void PagesRender()
    {
        var (st, s) = Client(new DemoProvider());
        s.Users();
        var anna = s.Users(u: "CN=Alice Adams,OU=Staff,DC=demo,DC=local");
        Assert.NotNull(anna.Selected);
        Assert.NotEmpty(anna.Detail);
        s.FindingsPage();
        s.FolderPage(OPS);
        s.FolderPage("");
        s.FolderPanel("");
        s.CellPanel(G("G-Management"), OPS);
        s.DriftPage();
        s.AuditPage();
        Assert.NotEqual("apply", st.Progress.Now.Task); // /health
    }

    [Fact]
    public void UnknownFoldersAndAccountsAre404()
    {
        var (_, s) = Client(new DemoProvider());
        Refused(() => s.FolderPage("Nope"), 404);
        Refused(() => s.FolderPanel("Nope"), 404);
        Refused(() => s.CellPanel(G("G-Management"), "Nope"), 404);
        Refused(() => s.CellPanel("S-1-5-21-0-0-0-1", "HR"), 404);
    }

    [Fact(Skip = "No HTTP in the native app: matrix.js was a static file served by the web server and linked from the "
        + "matrix page. The keyboard handling (port-spec F-E3) moves into the Blazor matrix component and is tested "
        + "with the UI.")]
    public void KeyboardScriptIsServed()
    {
    }

    /// <summary>Python checked the text of every rendered page. Here: the user-facing strings Session, Labels, the
    /// rights computation and the progress produce in a typical flow (cell tips, panel texts, findings, via, plan
    /// errors, flash messages, refusals, progress phases). The Razor templates get their own check in the UI tests.</summary>
    [Fact]
    public void UiIsEnglish()
    {
        var (st, s) = Client(new DemoProvider());
        var texts = new List<string?>();
        st.Progress.Changed += p => texts.Add(p.Phase);
        var gf = G("G-Management");
        string?[] values = [null, .. M.Cells];
        foreach (var v in values)
            texts.AddRange([Labels.Label(v), Labels.Short(v)]);
        s.SetCell(gf, "HR", "R"); // so the preview has content

        var m = s.Matrix();
        texts.Add(m.PlanError);
        texts.AddRange(m.Rows.Select(r => r.Folder.Name));
        texts.AddRange(AllCells(m).SelectMany(c => new[] { c.Tip, c.Display().Text }));
        var preview = s.Preview();
        texts.Add(preview.Error);
        texts.AddRange(preview.Plan!.AclOps.SelectMany(o => o.Changes).Select(c => c.Name));
        texts.AddRange(preview.Plan.Impact.Select(i => i.Display));
        var drift = s.DriftPage();
        texts.Add(drift.BaselineError);
        texts.AddRange(drift.Items.Select(d => d.Change));
        texts.AddRange(s.FindingsPage().Select(f => f.Text));
        texts.AddRange(s.AuditPage().Entries.Select(e => e.Str("kind")));
        texts.AddRange(s.Users().Users.Select(u => u.Display));
        texts.AddRange(s.Users(u: Demo.Udn("mmoore")).Detail.SelectMany(d => d.Via));
        var folder = s.FolderPage("HR");
        texts.AddRange(folder.Findings.Select(f => f.Text));
        texts.AddRange(folder.Entries.Select(e => e.Value));
        var cell = s.CellPanel(gf, "HR");
        texts.AddRange([cell.Info.Tip, cell.P.Name]);
        var panel = s.FolderPanel("HR");
        texts.AddRange(panel.Grants.Select(g => g.Name));
        texts.AddRange(panel.Findings.Select(f => f.Text));
        texts.AddRange(s.Groups("G-").Hits.Select(h => h.Name));

        // flash messages and refusals
        texts.Add(s.Apply("", "stale").Message);
        texts.Add(s.Rescan().Message);
        texts.Add(Apply(s).Message);
        foreach (var refused in new Action[]
                 {
                     () => s.SetCell(gf, "HR", "M"), () => s.SetCell("S-1-5-18", "HR", "R"), () => s.SetCell(gf, "Nope", "R"),
                     () => s.ClearFolder(""), () => s.SetInheritance("", false), () => s.NewFolder("HR", "CON"),
                     () => s.SetDepth(0), () => s.OpenShare("X:\\"), () => s.AddColumn("S-1-0", "x"),
                     () => s.CellPanel(gf, "Nope"), () => s.Undo("nope"),
                 })
            texts.Add(Assert.Throws<UserError>(refused).Message);

        Assert.NotEmpty(texts);
        foreach (var t in texts.OfType<string>())
            Assert.False(German.IsMatch(t), $"German in: {t}");
    }
}
