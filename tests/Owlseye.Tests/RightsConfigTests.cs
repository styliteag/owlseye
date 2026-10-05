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
    public void FullControlIsShownAsFAndThePanelSaysWhatIsThere()
    {
        var (_, s) = Client(new DemoProvider());
        var panel = s.CellPanel(G("G-IT"), @"Programs\Payroll");
        Assert.True(panel.Info.Full);
        Assert.Equal("F", panel.Info.Display().Text);
        Assert.Equal("Full control (this folder, subfolders and files)", panel.Entry);
        Assert.Null(s.CellPanel(G("G-HR"), "HR").Entry); // a standard entry needs no explanation
    }

    [Fact]
    public void ReplacingFullControlWithWWarnsAboutWhatTheMatrixDoesNotShow()
    {
        var (_, s) = Client(new DemoProvider());
        s.SetCell(G("G-IT"), @"Programs\Payroll", "W");
        var loss = Assert.Single(s.Preview().HiddenLosses!);
        Assert.Equal((@"Programs\Payroll", "DEMO\\G-IT"), (loss.Path, loss.Name));
        Assert.Equal("delete subfolders and files, change permissions, take ownership", loss.Lost); // Modify keeps delete itself
        Assert.Equal("Full control (this folder, subfolders and files)", loss.Before);
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
    public void ConfigJsonReadsWriteAndHidden()
    {
        var file = Path.Combine(Tmp, "config.json");
        File.WriteAllText(file, """{"write": "no-delete", "hidden": ["CORP\\backup", "S-1-5-21-9-9-9-1234"]}""");
        var cfg = Config.Load(file);
        Assert.Equal("no-delete", cfg.Write);
        Assert.Equal(["CORP\\backup", "S-1-5-21-9-9-9-1234"], cfg.Hidden);
        var plain = Config.Load(null);
        Assert.Equal("modify", plain.Write);
        Assert.Empty(plain.Hidden);
    }
}

/// <summary>Tests that change the process-wide settings of M (what W means, hidden accounts). They run alone and put
/// the defaults back, so no other test sees them.</summary>
[CollectionDefinition(nameof(GlobalRightsSettings), DisableParallelization = true)]
public sealed class GlobalRightsSettings;

[Collection(nameof(GlobalRightsSettings))]
public sealed class RightsSettingsTests : TestBase, IDisposable
{
    void IDisposable.Dispose() => M.Configure("modify", []);

    [Fact]
    public void WriteWithoutDeleteMakesThatTheStandardEntry()
    {
        M.Configure("no-delete", []);
        Assert.Equal(M.WriteNoDelete, M.Write);
        Assert.Equal(("W", true), Rights.Classify([new Ace("S-1-5-21-9", "X\\G", "group", M.WriteNoDelete, Flags: M.OiCi)]));
        Assert.Equal(("W", false), Rights.Classify([new Ace("S-1-5-21-9", "X\\G", "group", M.Modify, Flags: M.OiCi)]));
        M.Configure("modify", []);
        Assert.Equal(M.Modify, M.Standard["W"].Mask);
    }

    [Fact]
    public void UnknownWriteSettingIsRefused()
    {
        var e = Assert.Throws<ArgumentException>(() => M.Configure("delete", []));
        Assert.Contains("\"modify\" or \"no-delete\"", e.Message);
    }

    [Fact]
    public void AccountsHiddenInConfigAreNoColumns()
    {
        M.Configure("modify", ["DEMO\\G-IT", "g-transfer", Demo.Gsid("G-HR")]); // full name, short name in other case, SID
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
