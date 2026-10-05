// Desired/actual on a large share (45,000 folders on a real file server): a desired state written by an older version,
// accounts that are hidden now, and keeping tens of thousands of differences at once.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Owlseye.Providers;

namespace Owlseye.Tests;

public sealed class DriftScaleTests : TestBase
{
    const string DomainAdmins = "S-1-5-21-1-2-3-512";

    /// <summary>The demo share, G-IT with full control on Payroll (as in the demo), and a desired state as owlseye
    /// 0.9.12 wrote it: full control saved as W, Domain Admins as an ordinary column, no "format".</summary>
    (State St, string File) WithOldDesiredState()
    {
        var dir = Path.Combine(Tmp, "base");
        var store = new BaselineStore(dir);
        var snap = new DemoProvider().Scan();
        var desired = Drift.DesiredOf(snap);
        desired.Cells[(Demo.Gsid("G-IT"), @"Programs\Payroll")] = "W";
        desired.Cells[(DomainAdmins, "HR")] = "W";
        desired.Names[DomainAdmins] = "DEMO\\Domain Admins";
        store.Save(snap.Share, desired, "test");
        var file = store.PathOf(snap.Share)!;
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("format");
        File.WriteAllText(file, json.ToJsonString());
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = dir };
        var st = new State(cfg, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, file);
    }

    [Fact]
    public void AnOldDesiredStateShowsNoFalseDifferences()
    {
        var (st, _) = WithOldDesiredState();
        Assert.True(st.Baseline.Load(st.Snap.Share)!.Legacy);
        Assert.Empty(st.Drift); // W in the old file = F now; Domain Admins are hidden
    }

    [Fact]
    public void SavingAnOldDesiredStateWritesTheNewFormat()
    {
        var (st, file) = WithOldDesiredState();
        var s = new Owlseye.Ui.Session(st);
        s.SetCell(Demo.Gsid("G-HR"), "HR", "R"); // any apply saves the desired state
        Assert.Contains("applied", s.Apply("", State.PlanHash(st.Plan())).Message);
        var saved = st.Baseline.Load(st.Snap.Share)!;
        Assert.False(saved.Legacy);
        Assert.Equal(BaselineStore.Format, JsonNode.Parse(File.ReadAllText(file))!["format"]!.GetValue<int>());
        Assert.Equal("F", saved.Cells[(Demo.Gsid("G-IT"), @"Programs\Payroll")]); // upgraded, not reported later
        Assert.Empty(st.Drift);
    }

    [Fact]
    public void ANewDesiredStateTellsWAndFApart()
    {
        var dir = Path.Combine(Tmp, "base");
        var store = new BaselineStore(dir);
        var snap = new DemoProvider().Scan();
        var desired = Drift.DesiredOf(snap);
        desired.Cells[(Demo.Gsid("G-IT"), @"Programs\Payroll")] = "W"; // someone gave G-IT full control instead of W
        store.Save(snap.Share, desired, "test");
        var d = Assert.Single(Drift.Diff(snap, store.Load(snap.Share)!));
        Assert.Equal(("changed", "W", "F"), (d.Change, d.Before, d.After));
    }

    [Fact]
    public void KeepingTensOfThousandsOfDifferencesTakesNoTime()
    {
        var desired = new Desired();
        var items = new List<DriftItem>();
        for (var i = 0; i < 40000; i++)
        {
            var path = $@"A{i / 1000}\B{i % 1000}";
            desired.Cells[($"S-1-5-21-1-2-3-{4000 + i % 50}", path)] = "W";
            desired.Cells[("S-1-5-21-1-2-3-4999", path)] = "R";
            items.Add(new DriftItem("changed", $"S-1-5-21-1-2-3-{4000 + i % 50}", "X\\G", path, "W", "F"));
        }
        items.Add(new DriftItem("folder_gone", Path: @"A1\B1"));
        var sw = Stopwatch.StartNew();
        var kept = Drift.Accept(desired, items);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms"); // was quadratic: minutes
        Assert.Equal("F", kept.Cells[("S-1-5-21-1-2-3-4000", @"A0\B0")]);
        Assert.DoesNotContain(kept.Cells.Keys, k => k.Path == @"A1\B1");
        Assert.Equal(80000 - 2, kept.Cells.Count);
    }

    [Fact]
    public void KeepingUnderAnotherSpellingReplacesTheOldOne()
    {
        var desired = new Desired();
        desired.Cells[("S-1-5-21-1-2-3-4000", "Data")] = "R";
        var kept = Drift.Accept(desired, [new DriftItem("changed", "S-1-5-21-1-2-3-4000", "X\\G", "data", "R", "W")]);
        Assert.Equal([(("S-1-5-21-1-2-3-4000", "data"), "W")], kept.Cells.Select(kv => (kv.Key, kv.Value)));
    }
}
