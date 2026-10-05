// The switch "Hidden accounts" above the matrix: SYSTEM, Administrators, Domain Admins and the accounts hidden in the
// settings become columns that can be set. It changes process-wide settings of M, so it runs in GlobalRightsSettings.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

[Collection(nameof(GlobalRightsSettings))]
public sealed class HiddenAccountsTests : TestBase, IDisposable
{
    void IDisposable.Dispose()
    {
        M.ShowHidden = false;
        M.Configure("modify", []);
    }

    const string OPS = "Operations";

    (State St, Session S) Client(params string[] hidden)
    {
        M.Configure("modify", hidden);
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    static List<string> Columns(Session s) => s.Matrix().Columns.Select(p => p.Short).ToList();

    [Fact]
    public void TheSwitchShowsHiddenAccountsAsColumnsWithoutANewScan()
    {
        var (st, s) = Client("DEMO\\G-IT");
        var snap = st.Snap;
        var findings = st.Findings.Select(f => f.Text).ToList();
        Assert.DoesNotContain("SYSTEM", Columns(s));
        Assert.DoesNotContain("G-IT", Columns(s));

        s.SetShowHidden(true);
        Assert.Same(snap, st.Snap); // worked out from the last scan
        Assert.Superset(new HashSet<string> { "SYSTEM", "Administrators", "G-IT" }, Columns(s).ToHashSet());
        Assert.Contains(st.Cells, kv => kv.Key.Sid == M.System && kv.Value.Direct == "F");
        Assert.DoesNotContain(Columns(s), c => c.Contains("CREATOR", StringComparison.OrdinalIgnoreCase)); // never a column
        Assert.Equal(findings, st.Findings.Select(f => f.Text).ToList()); // no findings for them
        Assert.Empty(st.Drift); // and no deviations
        Assert.True(Settings.ShowHidden()); // remembered for this admin

        s.SetShowHidden(false);
        Assert.DoesNotContain("SYSTEM", Columns(s));
        Assert.False(Settings.ShowHidden());
    }

    [Fact]
    public void HiddenAccountsCanBeSetOnlyWhileShown()
    {
        var (_, s) = Client("DEMO\\G-IT");
        var it = Demo.Gsid("G-IT");
        var e = Assert.Throws<UserError>(() => s.SetCell(it, "Public", "R"));
        Assert.Contains("while the matrix shows them", e.Message);

        s.SetShowHidden(true);
        s.SetCell(it, "Public", "R");
        var op = s.Preview().Plan!.AclOps.Single(o => o.Path == "Public");
        Assert.Contains(op.After, a => a.Sid == it && a.Mask == M.Standard["R"].Mask);
        // hiding them again would hide the pending change
        e = Assert.Throws<UserError>(() => s.SetShowHidden(false));
        Assert.Contains("pending changes of hidden accounts", e.Message);
        s.Discard();
        s.SetShowHidden(false);
    }

    [Fact]
    public void AFullControlAccountKeepsFullControlWhereOwlseyeEnsuresIt()
    {
        var (_, s) = Client();
        s.SetShowHidden(true);
        var e = Assert.Throws<UserError>(() => s.SetCell(M.System, "HR", "W")); // HR has broken inheritance
        Assert.Contains("SYSTEM keeps full control on HR", e.Message);
        Assert.Throws<UserError>(() => s.SetCell(M.Admins, "HR", ""));
        // a folder that inherits: an entry of its own is fine, full control still comes from above
        s.SetCell(M.System, $@"{OPS}\Sales-Staff", "R");
        Assert.Null(s.Matrix().PlanError);
    }
}
