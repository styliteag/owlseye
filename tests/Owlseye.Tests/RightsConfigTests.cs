// What W means, which accounts are hidden, full control as F, and the preview warning for rights the matrix does not
// show. Found on a real file server: groups with "Modify" and admin groups with full control all showed as W*.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class RightsConfigTests : TestBase
{
    (State St, Session S) Client(IProvider provider)
    {
        var cfg = new Config { Provider = provider.Name, Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, provider);
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    static string G(string sam) => Demo.Gsid(sam);

    /// <summary>The demo share with extra entries on HR (explicit, so they are columns).</summary>
    static DemoProvider With(params Ace[] extra)
    {
        var p = new DemoProvider();
        var (prot, aces) = p.FolderAcl("HR");
        p.SetFolderAcl("HR", prot, [.. aces, .. extra]);
        return p;
    }

    [Fact]
    public void DomainAndEnterpriseAdminsAreHiddenLikeTheAdministrators()
    {
        const string domainAdmins = "S-1-5-21-1-2-3-512", enterpriseAdmins = "S-1-5-21-1-2-3-519", lookalike = "S-1-5-21-1-2-3-1512";
        var (st, s) = Client(With(
            new Ace(domainAdmins, "DEMO\\Domain Admins", "group", M.Full, Flags: M.OiCi),
            new Ace(enterpriseAdmins, "DEMO\\Enterprise Admins", "group", M.Full, Flags: M.OiCi),
            new Ace(lookalike, "DEMO\\G-1512", "group", M.Modify, Flags: M.OiCi)));
        var columns = s.Matrix().Columns.Select(c => c.Sid).ToList();
        Assert.DoesNotContain(domainAdmins, columns);
        Assert.DoesNotContain(enterpriseAdmins, columns);
        Assert.Contains(lookalike, columns);
        Assert.True(M.IsHidden(domainAdmins) && !M.IsHidden(lookalike) && !M.IsHidden("S-1-5-21-1-2-3-4-512"));
        Assert.DoesNotContain(st.Findings, f => f.Text.Contains("Domain Admins"));
    }

    [Fact]
    public void FullControlIsTheValueFAndCanBeSet()
    {
        var (st, s) = Client(new DemoProvider());
        var panel = s.CellPanel(G("G-IT"), @"Programs\Payroll");
        Assert.Equal(("F", true), (panel.Info.Value, panel.Info.Standard));
        Assert.Equal(("F", "F"), panel.Info.Display());
        Assert.Equal("Entry here: F Full control", panel.Info.Tip);
        Assert.Null(panel.Entry); // a standard entry needs no explanation
        Assert.Contains("F", panel.Options);
        Assert.Contains(st.Findings, f => f.Path == @"Programs\Payroll" && f.Text.StartsWith("Full control for G-IT"));
        s.SetCell(G("G-HR"), "HR", "F");
        var change = s.Preview().Plan!.AclOps.Single(o => o.Path == "HR").After.Single(a => a.Sid == G("G-HR"));
        Assert.Equal((M.Full, M.OiCi), (change.Mask, change.Flags));
    }

    [Fact]
    public void FullControlToWIsAVisibleChangeNotAHiddenLoss()
    {
        var (_, s) = Client(new DemoProvider());
        s.SetCell(G("G-IT"), @"Programs\Payroll", "W");
        var change = s.Preview().Plan!.AclOps.SelectMany(o => o.Changes).Single();
        Assert.Equal(("F", "W"), (change.Before, change.After)); // the preview shows F -> W
        Assert.Empty(s.Preview().HiddenLosses!);
    }

    [Fact]
    public void ReplacingASpecialEntryWarnsAboutWhatTheMatrixDoesNotShow()
    {
        var p = new DemoProvider();
        var (prot, aces) = p.FolderAcl("HR");
        p.SetFolderAcl("HR", prot, [.. aces.Select(a => a.Sid == G("G-HR") ? a with { Mask = M.Modify | M.WriteDac } : a)]);
        var (_, s) = Client(p);
        var panel = s.CellPanel(G("G-HR"), "HR");
        Assert.Equal("W*", panel.Info.Display().Text);
        Assert.Equal("Modify + change permissions (this folder, subfolders and files)", panel.Entry);
        s.SetCell(G("G-HR"), "HR", "W");
        var loss = Assert.Single(s.Preview().HiddenLosses!);
        Assert.Equal(("HR", "DEMO\\G-HR", "change permissions"), (loss.Path, loss.Name, loss.Lost));
        Assert.Equal("Modify (this folder, subfolders and files)", loss.After);
    }

    [Fact]
    public void AVisibleStepIsNoHiddenLoss()
    {
        var (_, s) = Client(new DemoProvider());
        s.SetCell(G("G-HR"), "HR", "R"); // W (Modify) -> R: write and delete go, and the matrix shows it
        Assert.Empty(s.Preview().HiddenLosses!);
        s.SetCell(G("G-HR"), "HR", null);
        Assert.Empty(s.Preview().HiddenLosses!);
    }

    [Fact]
    public void ConfigJsonReadsHiddenAndIgnoresTheOldWriteKey()
    {
        var file = Path.Combine(Tmp, "config.json");
        File.WriteAllText(file, """{"write": "no-delete", "hidden": ["CORP\\backup", "S-1-5-21-9-9-9-1234"]}""");
        Assert.Equal(["CORP\\backup", "S-1-5-21-9-9-9-1234"], Config.Load(file).Hidden); // W- is set per cell now
        Assert.Empty(Config.Load(null).Hidden);
    }
}

/// <summary>Tests that change the process-wide settings of M (hidden and full-control accounts). They run alone and put
/// the defaults back, so no other test sees them.</summary>
[CollectionDefinition(nameof(GlobalRightsSettings), DisableParallelization = true)]
public sealed class GlobalRightsSettings;

[Collection(nameof(GlobalRightsSettings))]
public sealed class RightsSettingsTests : TestBase, IDisposable
{
    void IDisposable.Dispose() => M.Configure([]);

    const string Staff = @"Operations\Sales-Staff"; // inherits; G-Sales-Lead and G-Sales-Staff have W there

    static Ace E(uint mask, int flags) => new("S", "X\\G", "group", mask, Flags: flags);

    [Fact]
    public void WMinusIsWriteWithoutDelete()
    {
        Assert.Equal(("W-", true), Rights.Classify([E(M.WriteNoDelete, M.OiCi)]));
        Assert.Equal(("W", true), Rights.Classify([E(M.Modify, M.OiCi)]));
        Assert.True(M.Rank("R") < M.Rank("W-") && M.Rank("W-") < M.Rank("W"));
        var snap = new DemoProvider().Scan();
        var plan = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?> { [(Demo.Gsid("G-HR"), Staff)] = "W-" });
        var after = plan.AclOps.Single(o => o.Path == Staff).After.Where(a => a.Sid == Demo.Gsid("G-HR")).Select(a => (a.Mask, a.Flags));
        Assert.Equal([(M.WriteNoDelete, M.OiCi)], after);
        Assert.Equal("W-", plan.CellsAfter[(Demo.Gsid("G-HR"), Staff + @"\2025")].Effective); // passes down
        var w = Rights.Findings(plan.SnapAfter, cells: plan.CellsAfter).Where(f => f.Text.StartsWith("W- for G-HR")).ToList();
        Assert.Equal([(Staff, "low")], w.Select(f => (f.Path, f.Severity))); // where it is set, not again below
    }

    [Fact]
    public void AllWMinusCanBeChangedToWListInOneGo()
    {
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new DemoProvider());
        st.Load();
        var s = new Session(st);
        s.SetCell(Demo.Gsid("G-HR"), Staff, "W-");
        s.SetCell(Demo.Gsid("G-IT"), "Public", "W-");
        Assert.False(s.Apply("", s.Preview().Phash).Error);
        Assert.Equal(2, s.FindingsChecks().WMinus);

        Assert.Equal("/preview", s.ConvertAll("W-", "W|").Url);
        Assert.Equal(2, st.PendingCount);
        var op = s.Preview().Plan!.AclOps.Single(o => o.Path == Staff);
        Assert.Equal([(M.Modify, M.ThisFolder)], op.After.Where(a => a.Sid == Demo.Gsid("G-HR")).Select(a => (a.Mask, a.Flags)));
        Assert.False(s.Apply("", s.Preview().Phash).Error);
        Assert.Equal(0, s.FindingsChecks().WMinus);
        Assert.Equal("W|", st.Cells[(Demo.Gsid("G-HR"), Staff)].Direct);
        Assert.Equal("No W- entries to change.", s.ConvertAll("W-", "W|").Message);

        s.Undo(st.Audit.Entries().First(e => e.Arr("acl_ops") is { Count: > 0 }).Str("id")!);
        Assert.False(s.Apply("undo", s.Preview().Phash).Error);
        Assert.Equal(2, s.FindingsChecks().WMinus);
    }

    [Fact]
    public void AFolderKeptFromMovingHasItsWWrittenAsTwoEntries()
    {
        var kept = M.StandardAces("W", kept: true);
        Assert.Equal([(M.WriteNoDelete, M.ThisFolder), (M.Modify, M.OiCi | M.InheritOnly)], kept);
        Assert.Equal(("W", true), Rights.Classify([E(M.Modify, M.OiCi | M.InheritOnly), E(M.WriteNoDelete, M.ThisFolder)], kept: true));
        Assert.Equal(("W|", true), Rights.Classify([E(M.WriteNoDelete, M.ThisFolder)], kept: true));

        var snap = new DemoProvider().Scan();
        Assert.False(Rights.Kept(snap.Folders[Staff]));
        var plan = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?> { [(Demo.Gsid("G-HR"), Staff)] = "W" },
            keptIn: new Dictionary<string, bool> { [Staff] = true });
        var op = plan.AclOps.Single(o => o.Path == Staff);
        Assert.True(!op.KeptBefore && op.KeptAfter);
        foreach (var g in new[] { "G-Sales-Lead", "G-Sales-Staff", "G-HR" }) // existing W converted, the new one written so
            Assert.Equal(kept.Order(), op.After.Where(a => a.Sid == Demo.Gsid(g)).Select(a => (a.Mask, a.Flags)).Order());
        Assert.True(plan.CellsAfter[(Demo.Gsid("G-HR"), Staff)].Standard);
        Assert.Equal("W", plan.CellsAfter[(Demo.Gsid("G-HR"), Staff + @"\2025")].Effective); // works inside as before
        Assert.True(Rights.Kept(plan.SnapAfter.Folders[Staff]));

        var back = Planner.Build(plan.SnapAfter, new Dictionary<(string Sid, string Path), string?>(), keptIn: new Dictionary<string, bool> { [Staff] = false });
        Assert.Equal([(M.Modify, M.OiCi)], back.AclOps.Single().After.Where(a => a.Sid == Demo.Gsid("G-HR")).Select(a => (a.Mask, a.Flags)));
    }

    [Fact]
    public void TheFolderPanelKeepsUsersFromMovingTheFolderWithPreviewAndUndo()
    {
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new DemoProvider());
        st.Load();
        var s = new Session(st);
        Assert.True(s.FolderPanel(Staff).CanKeep && !s.FolderPanel(Staff).Kept);
        Assert.False(s.FolderPanel(@"Operations\Sales-Staff\2025").CanKeep); // no W of its own: nothing to keep

        s.SetKept(Staff, true);
        Assert.Equal(1, st.PendingCount);
        Assert.True(s.FolderPanel(Staff).Kept && s.FolderPanel(Staff).KeptPending);
        Assert.True(s.Matrix().Rows.Single(r => r.Folder.Path == Staff).Kept); // 📌 already while pending
        Assert.True(s.Preview().Plan!.AclOps.Single(o => o.Path == Staff).KeptAfter);
        Assert.False(s.Apply("keep", s.Preview().Phash).Error);
        Assert.Equal((1, 0), (s.FindingsChecks().Kept, s.FindingsChecks().StillMovable));

        var entry = st.Audit.Entries().First(e => e.Arr("acl_ops") is { Count: > 0 });
        Assert.True(entry.Arr("acl_ops")![0]!.AsObject().Bool("kept_after"));
        s.Undo(entry.Str("id")!);
        Assert.False(s.Apply("undo", s.Preview().Phash).Error);
        Assert.False(s.FolderPanel(Staff).Kept);
    }

    [Fact]
    public void APlainWOnAKeptFolderIsMarkedAndAFinding()
    {
        var snap = new DemoProvider().Scan();
        var f = snap.Folders[Staff];
        var lead = Demo.Gsid("G-Sales-Lead");
        snap.Folders[Staff] = f with
        {
            Aces = [.. f.Aces.Where(a => a.Sid != lead), .. M.StandardAces("W", kept: true).Select(s => new Ace(lead, "DEMO\\G-Sales-Lead", "group", s.Mask, Flags: s.Flags))],
        };
        Assert.True(Rights.Kept(snap.Folders[Staff]));
        Assert.Equal([Demo.Gsid("G-Sales-Staff")], Rights.StillMovable(snap.Folders[Staff]).Select(a => a.Sid));
        var cells = Rights.Matrix(snap);
        Assert.True(cells[(Demo.Gsid("G-Sales-Staff"), Staff)].Movable);
        Assert.Contains(Rights.Findings(snap), x => x.Path == Staff && x.Text.StartsWith("G-Sales-Staff can still delete, rename or move this folder"));
        Assert.DoesNotContain(Rights.Findings(snap), x => x.Text.Contains("(shown as W)")); // not a special entry

        var plan = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?>(), keptIn: new Dictionary<string, bool> { [Staff] = true });
        Assert.Empty(Rights.StillMovable(plan.SnapAfter.Folders[Staff])); // keeping it again converts the rest
    }

    [Fact]
    public void AccountsHiddenInConfigAreNoColumns()
    {
        M.Configure(["DEMO\\G-IT", "g-transfer", Demo.Gsid("G-HR")]); // full name, short name in other case, SID
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") };
        var st = new State(cfg, new DemoProvider());
        st.Load();
        var columns = new Session(st).Matrix().Columns.Select(c => c.Short).ToList();
        Assert.DoesNotContain("G-IT", columns);
        Assert.DoesNotContain("G-Transfer", columns);
        Assert.DoesNotContain("G-HR", columns);
        Assert.Contains("G-Management", columns);
        Assert.Throws<PlanError>(() => Planner.Build(st.Snap, new Dictionary<(string, string), string?> { [(Demo.Gsid("G-IT"), "HR")] = "R" }));
    }
}
