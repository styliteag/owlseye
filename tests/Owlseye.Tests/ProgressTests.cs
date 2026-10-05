// Progress: the first scan runs behind a progress page; apply shows its progress and logs every write at once.
// The UI shows the progress page while !State.Ready (or State.LoadError) and renders State.Progress.Now; the window
// counts as busy while Progress.Now.Task == "apply".

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class ProgressTests : TestBase
{
    const string OPS = "Operations";
    const string SalesQa = $@"{OPS}\Sales-QA"; // R for G-Interns writes the root, Operations and Sales-QA, parents first
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>The scan reports one folder, then waits for the test; fails while Fail is set.</summary>
    sealed class SlowDemo : IProvider
    {
        readonly DemoProvider inner = new();
        public ManualResetEventSlim Reading { get; } = new();
        public ManualResetEventSlim Go { get; } = new();
        public volatile string Fail = "";

        public string Name => inner.Name;
        public string Share => inner.Share;
        public string WhoAmI() => inner.WhoAmI();

        public Snapshot Scan(Progress? progress = null)
        {
            progress!.Set(path: "Projects", done: 7);
            Reading.Set();
            Assert.True(Go.Wait(Wait));
            if (Fail != "") throw new IOException(Fail);
            return inner.Scan(progress);
        }

        public (bool Protected, List<Ace> Aces) FolderAcl(string path) => inner.FolderAcl(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public void CreateFolder(string path) => inner.CreateFolder(path);
        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => inner.SetFolderAcl(path, isProtected, aces);
        public List<Principal> FindGroups(string q) => inner.FindGroups(q);
    }

    /// <summary>Another provider with its own share and a hook before each ACL write; the hook may block.</summary>
    sealed class Hooked(IProvider inner, string? share = null) : IProvider
    {
        public Action<string>? BeforeSetAcl { get; set; }

        public string Name => inner.Name;
        public string Share => share ?? inner.Share;
        public string WhoAmI() => inner.WhoAmI();
        public Snapshot Scan(Progress? progress = null) => inner.Scan(progress);
        public (bool Protected, List<Ace> Aces) FolderAcl(string path) => inner.FolderAcl(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public void CreateFolder(string path) => inner.CreateFolder(path);
        public List<Principal> FindGroups(string q) => inner.FindGroups(q);

        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces)
        {
            BeforeSetAcl?.Invoke(path);
            inner.SetFolderAcl(path, isProtected, aces);
        }
    }

    string SimDir() => SimSeed.Seed(Path.Combine(Tmp, "sim"));

    string LogPath => Path.Combine(Tmp, "a.jsonl");

    /// <summary>State and Session; optionally a share opener and the first scan in the background.</summary>
    (State St, Session S) Client(IProvider provider, Func<string, IProvider>? openShare = null, bool scanInBackground = false)
    {
        var cfg = new Config { Provider = provider.Name, Audit = LogPath, Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, provider) { OpenShare = openShare };
        if (scanInBackground) st.LoadInBackground();
        else st.Load();
        return (st, new Session(st));
    }

    static void Settled(State st)
    {
        for (var i = 0; i < 250; i++)
        {
            if (st.Ready || st.LoadError != "") return;
            Thread.Sleep(20);
        }
        Assert.Fail("the first scan did not finish");
    }

    static Outcome Apply(Session s, string reason = "") => s.Apply(reason, s.Preview().Phash);

    /// <summary>The provider and whether an apply is running (then the window asks before it closes).</summary>
    static (string Provider, bool Busy) Health(State st) => (st.Provider.Name, st.Progress.Now.Task == "apply");

    static List<string> Columns(Session s) => s.Matrix().Columns.Select(p => p.Short).ToList();

    [Fact]
    public void PagesShowTheProgressUntilTheFirstScanIsDone()
    {
        var p = new SlowDemo();
        var (st, s) = Client(p, scanInBackground: true);
        Assert.True(p.Reading.Wait(Wait));
        Assert.Equal(("demo", false), Health(st)); // answers while it scans
        // the scan holds the state lock (pages wait for it), the progress does not need it
        var free = Monitor.TryEnter(st.Lock);
        if (free) Monitor.Exit(st.Lock);
        Assert.False(free);
        // the pages show the progress instead of their content
        Assert.False(st.Ready);
        Assert.Equal("", st.LoadError);
        Assert.True(st.Loading);
        // "Reading folders", "7 folders read", "Projects"
        Assert.Equal(new Status("scan", "Reading folders", "Projects", 7, 0), st.Progress.Now);
        // not ready yet: the progress page reloads once ready or an error is set
        p.Go.Set();
        Settled(st);
        Assert.True(st.Ready); // the progress page gives way to the page
        Assert.Equal("", st.LoadError);
        Assert.Contains("G-Sales-Lead", Columns(s));
    }

    [Fact]
    public void AFailedFirstScanCanBeTriedAgain()
    {
        var p = new SlowDemo { Fail = "The network path was not found" };
        p.Go.Set();
        var (st, s) = Client(p, scanInBackground: true);
        Settled(st);
        // the page shows the error and a "Try again" button
        Assert.False(st.Ready);
        Assert.Equal("The network path was not found", st.LoadError);
        Assert.Null(st.OpenShare); // demo: the share is fixed, no other folder offered
        // the progress page shows the error (Settled returned on LoadError)
        p.Fail = "";
        st.LoadInBackground(); // "Try again"
        Settled(st);
        Assert.True(st.Ready);
        Assert.Contains("G-Sales-Lead", Columns(s));
    }

    [Fact]
    public void AfterAFailedFirstScanAnotherFolderCanBeOpened()
    {
        var p = new SlowDemo { Fail = "Access is denied" };
        p.Go.Set();
        var other = new Hooked(new DemoProvider(), @"\\fs02\Data");
        var (st, s) = Client(p, openShare: _ => other, scanInBackground: true);
        Settled(st);
        // the error page offers another folder
        Assert.Equal("Access is denied", st.LoadError);
        Assert.NotNull(st.OpenShare);
        s.OpenShare(@"\\fs02\Data");
        Settled(st);
        Assert.True(st.Ready);
        Assert.Same(other, st.Provider);
        Assert.Equal(@"\\fs02\Data", Settings.LastShare("demo")); // opened again at the next start
    }

    [Fact]
    public async Task ProgressAndHealthShowARunningApply()
    {
        var p = new Hooked(new DemoProvider());
        var (st, s) = Client(p);
        using (st.Progress.Task("apply", "Writing ACLs", 5))
        {
            st.Progress.Set(done: 2, path: @"HR\Minutes");
            Assert.True(Health(st).Busy); // the window asks before it closes
            // "Writing ACLs", "2 of 5 done", "HR\Minutes", "Keep owlseye open" (shown for task "apply")
            Assert.Equal(new Status("apply", "Writing ACLs", @"HR\Minutes", 2, 5), st.Progress.Now);
        }
        Assert.False(Health(st).Busy);
        Assert.Equal(new Status(), st.Progress.Now);

        // The same during a real apply, held at its second ACL write (Operations, after the root)
        s.SetCell(Demo.Gsid("G-Interns"), SalesQa, "R");
        var phash = s.Preview().Phash;
        using var writing = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var calls = 0;
        p.BeforeSetAcl = _ =>
        {
            if (Interlocked.Increment(ref calls) != 2) return;
            writing.Set();
            go.Wait(Wait);
        };
        var run = Task.Run(() => s.Apply("", phash));
        try
        {
            Assert.True(writing.Wait(Wait));
            Assert.True(Health(st).Busy);
            Assert.Equal(new Status("apply", "Writing ACLs", OPS, 1, 3), st.Progress.Now);
        }
        finally
        {
            go.Set();
        }
        var outcome = await run;
        Assert.Contains("3 changes applied", outcome.Message);
        Assert.False(Health(st).Busy);
    }

    [Fact]
    public void EachWriteIsLoggedBeforeTheNextAndDesiredInBatches()
    {
        var p = new Hooked(new SimProvider(SimDir()));
        var (st, s) = Client(p);
        var seen = new List<(string Task, int Steps, bool Desired)>();
        s.SetCell(Demo.Gsid("G-Interns"), SalesQa, "R");
        // "data-busy" in the preview (the apply form shows the overlay): see FormsThatScanShowTheOverlay
        p.BeforeSetAcl = _ =>
        {
            var kinds = File.ReadAllLines(LogPath).Select(x => JsonNode.Parse(x).Str("kind")).ToList();
            var desired = st.Baseline.Load(st.Snap.Share)!;
            seen.Add((st.Progress.Now.Task, kinds.Count(k => k == "change_step"), desired.Cells.ContainsKey((Demo.Gsid("G-Interns"), ""))));
        };
        Assert.Contains("3 changes applied", Apply(s, "T-4").Message);
        // the log has every write before the next; the desired state (a large file on a large share) is saved after
        // up to 25 folders or 2 seconds, and at the end
        Assert.Equal([("apply", 0, false), ("apply", 1, false), ("apply", 2, false)], seen);
        Assert.True(st.Baseline.Load(st.Snap.Share)!.Cells.ContainsKey((Demo.Gsid("G-Interns"), "")));
        // start and steps folded away
        Assert.Equal(new HashSet<string?> { "baseline_init", "change" }, st.Audit.Entries().Select(e => e.Str("kind")).ToHashSet());
        Assert.DoesNotContain(s.AuditPage().Entries, e => e.Str("kind") == "change_start"); // "Not finished" not in the log
    }

    [Fact]
    public void AnApplyThatNeverFinishedShowsWhatWasWrittenAndCanBeUndone()
    {
        var (st, s) = Client(new SimProvider(SimDir()));
        s.SetCell(Demo.Gsid("G-Interns"), SalesQa, "R");
        Apply(s, "T-5");
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(["change_start", "change_step", "change_step", "change_step", "change"],
            lines[^5..].Select(x => JsonNode.Parse(x).Str("kind")));
        File.WriteAllText(LogPath, string.Join("\n", lines[..^2]) + "\n"); // as if stopped while writing Sales-QA
        var entry = st.Audit.Entries()[0];
        Assert.Equal("change_start", entry.Str("kind"));
        Assert.Equal("T-5", entry.Str("reason"));
        Assert.Equal(["", OPS], (entry.Arr("acl_ops") ?? []).Select(o => o.Str("path")));
        Assert.Equal([SalesQa], (entry.Arr("missing") ?? []).Select(n => (string?)n));
        // "Not finished: 2 of 3 operations written" and SalesQa on the log page
        var shown = s.AuditPage().Entries.Single(e => e.Str("id") == entry.Str("id"));
        Assert.Equal(2, shown.Arr("acl_ops")!.Count + shown.Arr("create_ops")!.Count);
        Assert.Equal(3, shown.Long("total"));
        Assert.Contains(SalesQa, shown.Arr("missing")!.Select(n => (string?)n));
        s.Undo(entry.Str("id")!);
        Assert.Equal(new HashSet<string> { "", OPS }, st.Pending.Keys.Select(k => k.Path).ToHashSet()); // only what was written
    }
}
