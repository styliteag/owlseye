// config.json "full_control" (accounts that must have full control), the settings page and where config.json is read
// from. They change process-wide settings of M, so they run in the GlobalRightsSettings collection.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

[Collection(nameof(GlobalRightsSettings))]
public sealed class SettingsTests : TestBase, IDisposable
{
    void IDisposable.Dispose() => M.Configure("modify", []);

    const string DomainAdmins = "S-1-5-21-1-2-3-512"; // the demo domain is S-1-5-21-1-2-3

    (State St, Session S) Client(string? configFile = null)
    {
        var cfg = configFile is not null ? Config.Load(configFile, "demo") : new Config { Provider = "demo" };
        cfg = cfg with { Audit = Path.Combine(Tmp, "log.jsonl") };
        M.Configure(cfg.Write, cfg.Hidden, cfg.FullControl);
        var st = new State(cfg, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    [Fact]
    public void FullControlAccountsAreFoundInTheScan()
    {
        var snap = new DemoProvider().Scan();
        Assert.Equal([M.System, M.Admins], Rights.RequiredFullControl(snap).Select(p => p.Sid));
        M.Configure("modify", [], ["system", "Domain Admins", "DEMO\\G-IT", "g-hr", "S-1-5-21-9-9-9-77", "CORP\\nobody"]);
        var req = Rights.RequiredFullControl(snap);
        Assert.Equal([M.System, DomainAdmins, Demo.Gsid("G-IT"), Demo.Gsid("G-HR"), "S-1-5-21-9-9-9-77"], req.Select(p => p.Sid));
        Assert.Equal("Domain Admins", req[1].Name); // an unknown name (CORP\nobody) is left out
    }

    [Fact]
    public void FindingsAndWritesFollowFullControl()
    {
        var file = Path.Combine(Tmp, "config.json");
        File.WriteAllText(file, """{"full_control": ["SYSTEM", "Domain Admins"]}""");
        var (st, s) = Client(file);
        Assert.DoesNotContain(st.Findings, f => f.Text.StartsWith("Administrators without full control"));
        Assert.Contains(st.Findings, f => f.Path == "HR" && f.Text == "Domain Admins without full control here");
        s.SetCell(Demo.Gsid("G-HR"), "HR", "R"); // HR has broken inheritance: owlseye completes the required entries
        var after = s.Preview().Plan!.AclOps.Single(o => o.Path == "HR").After;
        Assert.Contains(after, a => a.Sid == DomainAdmins && a.Mask == M.Full && a.Flags == M.OiCi);
        // the preview names the added entry, apart from the cell changed by hand
        var preview = s.Preview();
        var op = preview.Plan!.AclOps.Single(o => o.Path == "HR");
        Assert.Equal([DomainAdmins], Planner.AddedFullControl(op, preview.Required!).Select(a => a.Sid));
        Assert.Equal(["SYSTEM", "Domain Admins"], preview.Required!.Select(p => p.Name));
    }

    [Fact]
    public void SavingSettingsWritesConfigLogsAndAppliesThem()
    {
        var file = Path.Combine(Tmp, "config.json");
        File.WriteAllText(file, """{"provider": "demo", "dl_ou": "kept", "scan_depth": 20}""");
        var (st, s) = Client(file);
        var v = s.SettingsPage();
        Assert.Equal((file, file, true), (v.File, v.SaveTo, v.CanSave));
        var r = s.SaveSettings(new SettingsInput(4, "modify", ["DEMO\\G-IT", " "], ["SYSTEM", "Domain Admins"], ""), "T-9");
        Assert.False(r.Error, r.Message);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(("kept", 4L), (json.Str("dl_ou"), json.Long("scan_depth"))); // other keys stay
        Assert.Equal(["DEMO\\G-IT"], json["hidden"]!.AsArray().Select(x => (string)x!));
        Assert.DoesNotContain(s.Matrix().Columns, c => c.Short == "G-IT"); // hidden at once
        Assert.Equal(4, st.Cfg.ScanDepth);
        var entry = st.Audit.Entries().First(e => e.Str("kind") == "settings");
        Assert.Equal("T-9", entry.Str("reason"));
        Assert.Equal(20L, entry.Obj("changes")!["scan_depth"]!.Long("before"));
        Assert.Equal("Nothing changed.", s.SaveSettings(new SettingsInput(4, "modify", ["DEMO\\G-IT"], ["SYSTEM", "Domain Admins"], ""), "").Message);
    }

    [Fact]
    public void SavingWaitsForPendingChangesAndChecksValues()
    {
        var (_, s) = Client();
        Assert.Contains("0 (whole tree) to 100", s.SaveSettings(new SettingsInput(-1, "modify", [], [], ""), "").Message);
        s.SetCell(Demo.Gsid("G-HR"), "HR", "R");
        Assert.False(s.SettingsPage().CanSave);
        Assert.True(s.SaveSettings(new SettingsInput(4, "modify", [], [], ""), "").Error);
    }

    [Fact]
    public void WithoutConfigFileTheSettingsGoToTheAdminsOwnFile()
    {
        var (st, s) = Client();
        Assert.Equal(Launch.PersonalConfig, s.SettingsPage().SaveTo);
        s.SaveSettings(new SettingsInput(5, "no-delete", [], [], ""), "");
        Assert.True(File.Exists(Launch.PersonalConfig));
        Assert.Equal(Launch.PersonalConfig, st.Cfg.File);
        Assert.Equal(M.WriteNoDelete, M.Write);
    }

    /// <summary>A sim share with its desired state and log in the data folder (the default state folder).</summary>
    (State St, Session S) SimClient()
    {
        var sim = SimSeed.Seed(Path.Combine(Tmp, "sim"));
        var cfg = new Config { Provider = "sim", SimDir = sim };
        M.Configure(cfg.Write, cfg.Hidden, cfg.FullControl);
        var st = new State(cfg, new SimProvider(sim));
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    [Fact]
    public void ChangingTheStateFolderMovesDesiredStateAndLogIfItIsEmpty()
    {
        var (st, s) = SimClient();
        var oldDesired = Directory.GetFiles(AppData, "desired-*.json").Single();
        var oldLog = st.Audit.Path;
        Assert.True(File.Exists(oldLog));
        var team = Path.Combine(Tmp, "team");
        var r = s.SaveSettings(new SettingsInput(20, "modify", [], [], team), "to the admin share");
        Assert.False(r.Error, r.Message);
        Assert.Contains("Moved 2 files", r.Message);
        Assert.False(File.Exists(oldDesired) || File.Exists(oldLog)); // moved, not copied
        Assert.True(File.Exists(Path.Combine(team, Path.GetFileName(oldDesired))));
        Assert.Equal(Path.Combine(team, "audit-sim.jsonl"), st.Audit.Path);
        Assert.Contains(st.Audit.Entries(), e => e.Str("kind") == "baseline_init"); // the old log went along
        Assert.Contains(st.Audit.Entries(), e => e.Str("kind") == "settings" && e.Str("reason") == "to the admin share");
        Assert.Empty(st.Drift); // the moved desired state is used
    }

    [Fact]
    public void AStateFolderInUseIsTakenAsItIs()
    {
        var (st, s) = SimClient();
        var oldDesired = Directory.GetFiles(AppData, "desired-*.json").Single();
        var team = Path.Combine(Tmp, "team");
        Directory.CreateDirectory(team);
        File.WriteAllText(Path.Combine(team, "audit-sim.jsonl"), ""); // another admin's log is already there
        var r = s.SaveSettings(new SettingsInput(20, "modify", [], [], team), "");
        Assert.Contains("already held owlseye state", r.Message);
        Assert.True(File.Exists(oldDesired)); // nothing moved
        Assert.Equal(Path.Combine(team, "audit-sim.jsonl"), st.Audit.Path);
    }

    [Fact]
    public void AccountPickerOffersKeywordsAclAccountsAndDirectoryGroups()
    {
        var p = new DemoProvider();
        var (prot, aces) = p.FolderAcl("HR");
        p.SetFolderAcl("HR", prot, [.. aces, new Ace(DomainAdmins, "DEMO\\Domain Admins", "group", M.Full, Flags: M.OiCi)]);
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, p);
        st.Load();
        var s = new Session(st);
        Assert.Equal(["Domain Admins", "DEMO\\Domain Admins"], s.AccountChoices("domain adm").Select(c => c.Value)); // keyword, hidden ACL account
        Assert.Contains(s.AccountChoices("sys"), c => c.Value == "SYSTEM");
        var it = s.AccountChoices("G-I");
        Assert.Contains(it, c => c.Value == "DEMO\\G-IT" && c.Hint.StartsWith("in the ACLs"));
        Assert.Contains(it, c => c.Value == "DEMO\\G-Interns");
        Assert.Contains(s.AccountChoices("users"), c => c.Value == "BUILTIN\\Users" && c.Hint.StartsWith("directory")); // no ACL entry
        Assert.Empty(s.AccountChoices("  "));
    }

    [Fact]
    public void ConfigFileOrderIsCommandLineThenOwnThenNextToTheExe()
    {
        var exeDir = Path.Combine(Tmp, "app");
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, "config.json"), "{}");
        Assert.Equal(Path.Combine(exeDir, "config.json"), Launch.ConfigFile(null, exeDir));
        Directory.CreateDirectory(Path.GetDirectoryName(Launch.PersonalConfig)!);
        File.WriteAllText(Launch.PersonalConfig, "{}");
        Assert.Equal(Launch.PersonalConfig, Launch.ConfigFile(null, exeDir));
        Assert.Equal(@"\\fs01\cfg.json", Launch.ConfigFile(@"\\fs01\cfg.json", exeDir));
    }
}
