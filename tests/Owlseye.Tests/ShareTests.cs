// Port of backend/tests/test_share.py.
// Choosing the share in the UI and opening the last one at the next start.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class ShareTests : TestBase
{
    const string Other = @"\\fs02\Other";
    const string First = @"\\fs01\Data";

    /// <summary>two fixture: two sim directories, the second one with share \\fs02\Other.</summary>
    (string A, string B) Two()
    {
        var a = SimSeed.Seed(Path.Combine(Tmp, "a"));
        var b = SimSeed.Seed(Path.Combine(Tmp, "b"));
        var p = Path.Combine(b, "state.json");
        var s = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        s["config"]!["share"] = Other;
        File.WriteAllText(p, s.ToJsonString());
        return (a, b);
    }

    /// <summary>open_share=SimProvider: the class as factory.</summary>
    static IProvider OpenSim(string path) => new SimProvider(path);

    (State St, Session S) Client(IProvider provider, Func<string, IProvider>? openShare = null)
    {
        var cfg = new Config { Provider = provider.Name, Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, provider) { OpenShare = openShare };
        st.Load();
        return (st, new Session(st));
    }

    [Fact]
    public void HeaderLinksToTheSharePage()
    {
        var (a, _) = Two();
        var (_, s) = Client(new SimProvider(a), OpenSim);
        // 'href="/share"' in the matrix header: the Razor layout has its own check
        var page = s.SharePage();
        Assert.Equal(First, page.Share);
        Assert.True(page.CanSwitch); // the form with name="path" and the Browse… button (id="browse") is shown
    }

    [Fact]
    public void OpenAnotherShareAndRememberIt()
    {
        var (a, b) = Two();
        var (st, s) = Client(new SimProvider(a), OpenSim);
        s.SetCell(Demo.Gsid("G-Management"), "HR", "R");
        var page = s.SharePage();
        Assert.True(page.CanSwitch);
        Assert.Equal(1, page.PendingCount); // "1 pending change": warned before dropping
        var r = s.OpenShare($"  \"{b}\"  "); // quotes from Explorer
        Assert.Equal("/matrix", r.Url);
        Assert.Equal($"Opened {Other}.", r.Message);
        Assert.False(r.Error);
        Assert.Equal(Other, st.Snap.Share);
        Assert.Equal(0, st.PendingCount);
        Assert.Equal(Other, Settings.LastShare("sim"));
        // back to the first one through the recent list
        s.OpenShare(a);
        Assert.Equal([First, Other], Settings.RecentShares("sim"));
        Assert.Contains(Other, s.SharePage().Recent);
    }

    [Fact]
    public void DesiredStateAndLogFollowTheShare()
    {
        var (a, b) = Two();
        var (st, s) = Client(new SimProvider(a), OpenSim);
        s.OpenShare(b);
        var files = Directory.GetFiles(Path.Combine(Tmp, "base"), "desired-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(["desired-fs01_data.json", "desired-fs02_other.json"], files);
        Assert.Empty(st.Drift);
    }

    [Fact]
    public void BadPathKeepsTheCurrentShare()
    {
        var (a, _) = Two();
        var (st, s) = Client(new SimProvider(a), OpenSim);
        var r = s.OpenShare(Path.Combine(Tmp, "nope"));
        Assert.Contains("Cannot open", r.Message);
        Assert.True(r.Error);
        Assert.Equal(First, st.Snap.Share);
        r = s.OpenShare("  ");
        Assert.Contains("Enter a folder", r.Message);
    }

    [Fact]
    public void FixedShareInDemo()
    {
        var (_, s) = Client(new DemoProvider());
        var page = s.SharePage();
        Assert.False(page.CanSwitch); // "In demo mode the share is fixed."
        Assert.Equal("demo", page.Provider);
        var e = Assert.Throws<UserError>(() => s.OpenShare("X:\\"));
        Assert.Equal(400, e.Status);
    }

    /// <summary>A demo share under another name, standing in for LocalProvider (Win32) in the start tests.</summary>
    sealed class AtPath(string share) : IProvider
    {
        readonly DemoProvider demo = new();
        public string Name => "local";
        public string Share => share;
        public string WhoAmI() => demo.WhoAmI();
        public Snapshot Scan(Progress? progress = null) => demo.Scan(progress) with { Share = share };
        public (bool Protected, List<Ace> Aces) FolderAcl(string path) => demo.FolderAcl(path);
        public bool FolderExists(string path) => demo.FolderExists(path);
        public void CreateFolder(string path) => demo.CreateFolder(path);
        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => demo.SetFolderAcl(path, isProtected, aces);
        public List<Principal> FindGroups(string q) => demo.FindGroups(q);
    }

    static State RunMain(string? path) => Launch.Build("local", path, null, (_, share) => new AtPath(share), scanInBackground: false);

    [Fact]
    public void StartOpensTheShareOpenedLast()
    {
        var st = RunMain(@"E:\Eins");
        Assert.Equal(@"E:\Eins", st.Provider.Share);
        Assert.NotNull(st.OpenShare);
        Assert.True(st.Ready, st.LoadError);
        Assert.Equal(@"E:\Eins", Settings.LastShare("local"));
        Settings.RememberShare("local", @"D:\Zwei"); // chosen in the UI
        Assert.Equal(@"D:\Zwei", RunMain(null).Provider.Share);
        Assert.Equal(@"F:\Drei", RunMain(@"F:\Drei").Provider.Share); // explicit wins
        // the app starts the first scan in the background: the window opens at once and shows its progress
        var bg = Launch.Build("local", @"E:\Eins", null, (_, share) => new AtPath(share));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!bg.Ready && bg.LoadError == "" && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(bg.Ready, bg.LoadError);
    }

    /// <summary>A double-click on the exe (no arguments, no config.json) is not the demo: the app passes the provider for
    /// the machine (windows on a domain member, local elsewhere). The demo only with --demo or "provider": "demo".</summary>
    [Fact]
    public void StartWithoutModeUsesTheMachinesProviderNotTheDemo()
    {
        State Start(string? mode, string? config) =>
            Launch.Build(mode, null, config, (_, share) => new AtPath(share), scanInBackground: false, defaultProvider: "local");
        Assert.Equal("local", Start(null, null).Provider.Name);
        Assert.Equal("demo", Start("demo", null).Provider.Name);
        var noProvider = Path.Combine(Tmp, "plain.json");
        File.WriteAllText(noProvider, """{"max_level": 2}""");
        Assert.Equal("local", Start(null, noProvider).Provider.Name); // a config.json without provider is no demo either
        var demo = Path.Combine(Tmp, "demo.json");
        File.WriteAllText(demo, """{"provider": "demo"}""");
        Assert.Equal("demo", Start(null, demo).Provider.Name);
        Assert.Equal("local", Start("local", demo).Provider.Name); // the command line wins
    }

    [Fact]
    public void ConfigJsonNextToTheExeIsReadWithoutConfigFlag()
    {
        var exeDir = Path.Combine(Tmp, "app");
        Directory.CreateDirectory(exeDir);
        Assert.Null(Launch.ConfigFile(null, exeDir));
        File.WriteAllText(Path.Combine(exeDir, "config.json"), "{}");
        Assert.Equal(Path.Combine(exeDir, "config.json"), Launch.ConfigFile(null, exeDir));
        Assert.Equal(@"\\fs01\Owlseye$\config.json", Launch.ConfigFile(@"\\fs01\Owlseye$\config.json", exeDir)); // --config wins
    }

    [Fact]
    public void StartWithoutAnyShareSaysWhatToDo()
    {
        // Python exits with "No share yet: start once with --local PATH"; the window says it and offers to open one
        var st = RunMain(null);
        Assert.False(st.Ready);
        Assert.Contains("--local PATH", st.LoadError);
        Assert.NotNull(st.OpenShare);
    }
}
