// Moved folders: a folder moved within the volume keeps the inherited entries of its old place. owlseye finds them,
// shows what Windows applies there, and re-applies inheritance; any write above a moved folder ends the leftovers too.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class MovedFoldersTests : TestBase
{
    const string Moved = @"Operations\Service-Staff"; // inherits G-Management W from Operations
    const string Finding = "Inherited entries are not what the parent folder passes down (moved here?): re-apply inheritance in the folder panel";

    static string G(string sam) => Demo.Gsid(sam);

    /// <summary>The demo share as if Service-Staff had been moved from HR into Operations: its inherited entries are HR's.</summary>
    static Snapshot MovedFromHr(Snapshot snap)
    {
        var f = snap.Folders[Moved];
        var fromHr = snap.Folders["HR"].Aces.Where(a => (a.Flags & M.ContainerInherit) != 0)
            .Select(a => a with { Inherited = true, Flags = (a.Flags & ~M.InheritOnly) | M.InheritedAce });
        snap.Folders[Moved] = f with { Aces = [.. f.Explicit, .. fromHr] };
        return snap;
    }

    /// <summary>The demo provider with the moved folder until owlseye writes something.</summary>
    sealed class MovedDemo : IProvider
    {
        readonly DemoProvider inner = new();
        bool moved = true;
        public string Name => inner.Name;
        public string Share => inner.Share;
        public string WhoAmI() => inner.WhoAmI();
        public Snapshot Scan(Progress? progress = null) => moved ? MovedFromHr(inner.Scan(progress)) : inner.Scan(progress);
        // the conflict check before a write reads the ACL as it is on the disk: the moved folder as scanned
        public (bool Protected, List<Ace> Aces) FolderAcl(string path) =>
            moved && path == Moved ? (false, [.. MovedFromHr(inner.Scan()).Folders[path].Aces]) : inner.FolderAcl(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public void CreateFolder(string path) => inner.CreateFolder(path);
        public List<Principal> FindGroups(string q) => inner.FindGroups(q);

        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces)
        {
            moved = false; // Windows works the inherited entries out anew
            inner.SetFolderAcl(path, isProtected, aces);
        }
    }

    [Fact]
    public void DemoAndSimHaveNoMovedFolders()
    {
        Assert.Empty(Rights.Stale(new DemoProvider().Scan()));
        Assert.Empty(Rights.Stale(new SimProvider(SimSeed.Seed(Path.Combine(Tmp, "sim"))).Scan()));
    }

    [Fact]
    public void AMovedFolderIsFoundAndShowsWhatItBroughtAlong()
    {
        var snap = MovedFromHr(new DemoProvider().Scan());
        Assert.Equal([Moved], Rights.Stale(snap));
        Assert.Contains(Rights.Findings(snap), f => f.Path == Moved && f.Text == Finding);
        var cells = Rights.Matrix(snap);
        Assert.Equal(("W", Rights.LeftOver(Moved)), (cells[(G("G-HR"), Moved)].Effective, cells[(G("G-HR"), Moved)].Source));
        Assert.False(cells.ContainsKey((G("G-Management"), Moved))); // what Operations passes down does not apply there
        Assert.Equal("W", cells[(G("G-Service-Staff"), Moved)].Direct); // its own entries do
    }

    [Fact]
    public void ReapplyingInheritanceTakesTheParentsEntriesAgain()
    {
        var snap = MovedFromHr(new DemoProvider().Scan());
        var plan = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?>(), reinheritIn: [Moved]);
        var op = Assert.Single(plan.AclOps);
        Assert.True(op.Reinherited && op.Path == Moved && op.Changes.Count == 0);
        Assert.Empty(Rights.Stale(plan.SnapAfter));
        Assert.Equal("W", plan.CellsAfter[(G("G-Management"), Moved)].Effective);
        Assert.False(plan.CellsAfter.ContainsKey((G("G-HR"), Moved)));
        Assert.Contains(plan.Impact, i => i.User == "llewis" && i.Path == Moved && i.Before == "W" && i.After is null); // G-HR
    }

    [Fact]
    public void WritingAFolderAboveEndsTheLeftovers()
    {
        var snap = MovedFromHr(new DemoProvider().Scan());
        var above = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?> { [(G("G-Interns"), "Operations")] = "R" });
        Assert.Empty(Rights.Stale(above.SnapAfter)); // Windows works inheritance out anew below Operations
        var elsewhere = Planner.Build(snap, new Dictionary<(string Sid, string Path), string?> { [(G("G-Interns"), "HR")] = "R" });
        Assert.Equal([Moved], Rights.Stale(elsewhere.SnapAfter));
    }

    [Fact]
    public void TheFolderPanelOffersToReapplyInheritance()
    {
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new MovedDemo());
        st.Load();
        var s = new Session(st);
        Assert.True(s.FolderPanel(Moved).Stale);
        Assert.Throws<UserError>(() => s.ReinheritFolder("HR")); // nothing to re-apply there

        s.ReinheritFolder(Moved);
        Assert.True(s.FolderPanel(Moved).Reinheriting);
        Assert.Equal(1, st.PendingCount);
        s.Apply("moved", s.Preview().Phash);
        Assert.False(s.FolderPanel(Moved).Stale);
        Assert.DoesNotContain(st.Findings, f => f.Text == Finding);
        var entry = st.Audit.Entries().First(e => e.Arr("acl_ops") is { Count: > 0 });
        Assert.True(entry.Arr("acl_ops")![0]!.AsObject().Bool("reinherited"));
    }
}
