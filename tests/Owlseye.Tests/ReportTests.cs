// Access rights report (beyond the Python version): content built from the scan, and the Excel workbook.

using System.IO.Compression;
using System.Xml.Linq;
using Owlseye.Providers;
using Owlseye.Reports;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class ReportTests : TestBase
{
    (State St, Session S) Client()
    {
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, new DemoProvider());
        st.Load();
        return (st, new Session(st));
    }

    static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void ReportShowsTheScanAsTheMatrixDoes()
    {
        var (st, _) = Client();
        var r = ReportBuilder.Build(st);
        Assert.Equal(st.Folders().Select(f => f.Path), r.Matrix.Select(m => m.Folder.Path));
        Assert.Equal(st.Columns().Select(p => p.Sid), r.Columns.Select(p => p.Sid));
        var hr = r.Matrix.Single(m => m.Folder.Path == "HR");
        var col = r.Columns.ToList().FindIndex(p => p.Short == "G-HR");
        Assert.Equal("W", hr.Cells[col].Text);
        var hortMa = r.Matrix.Single(m => m.Folder.Path == @"Operations\Service-Staff");
        Assert.Equal("(W)", hortMa.Cells[r.Columns.ToList().FindIndex(p => p.Short == "G-Management")].Text); // inherited
        var payroll = r.Matrix.Single(m => m.Folder.Path == @"Programs\Payroll");
        Assert.Equal("W*", payroll.Cells[r.Columns.ToList().FindIndex(p => p.Short == "G-IT")].Text); // full control: special
        Assert.Equal(st.Findings.Count, r.Findings.Count);
        Assert.Equal(15, r.Groups.Single(g => g.Account.Short == "G-AllUsers").Members!.Count);
        Assert.Null(r.Groups.Single(g => g.Account.Short == "S-1-5-21-1-2-3-4711").Members); // unresolved SID
    }

    [Fact]
    public void AccessListsEveryUserFolderPairWithItsGroups()
    {
        var (st, _) = Client();
        var r = ReportBuilder.Build(st);
        var row = r.Access.Single(a => a.User == "llewis" && a.Folder == "HR");
        Assert.Equal("W", row.Right);
        Assert.Contains("G-HR (W, here)", row.Via);
        Assert.DoesNotContain(r.Access, a => a.User == "llewis" && a.Folder.StartsWith("Operations"));
        var lars = r.PerUser.Single(u => u.User.Sam == "llewis");
        Assert.Contains(("HR", "W"), lars.Folders);
        Assert.Equal(15, r.UsersWithAccess); // everyone can at least list the root
    }

    [Fact]
    public void ChangesComeFromTheLogWithTheReason()
    {
        var (st, s) = Client();
        s.SetCell(Demo.Gsid("G-HR"), @"Operations\Sales-Staff", "W");
        var o = s.Apply("Ticket 4711", s.Preview().Phash);
        Assert.False(o.Error, o.Message);
        s.SetCell(Demo.Gsid("G-Management"), "HR", "R"); // pending: noted, not part of the report
        var r = ReportBuilder.Build(st);
        var ch = r.Changes.Single(c => c.Folder == @"Operations\Sales-Staff");
        Assert.Equal(("change", "G-HR", "–", "W", "Ticket 4711"), (ch.What, ch.Account, ch.Before, ch.After, ch.Reason));
        Assert.Contains(r.Changes, c => c.What == "change (automatic R|)" && c.Folder == "Operations");
        Assert.Contains(r.Changes, c => c.What.StartsWith("current state taken"));
        Assert.Equal(1, r.PendingCount);
        Assert.DoesNotContain(r.Access, a => a.User == "cclark" && a.Folder == "HR"); // pending R for G-Management is not in it
    }

    [Fact]
    public void WorkbookHasAllSheetsAndOpensAsValidXml()
    {
        var (st, _) = Client();
        var r = ReportBuilder.Build(st);
        var path = Path.Combine(Tmp, "report.xlsx");
        ReportBuilder.WriteXlsx(r, path);
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            using var stream = e.Open();
            XDocument.Load(stream); // every part is well-formed XML
        }
        XDocument Part(string name)
        {
            using var stream = zip.GetEntry(name)!.Open();
            return XDocument.Load(stream);
        }
        var names = Part("xl/workbook.xml").Descendants(X + "sheet").Select(e => (string)e.Attribute("name")!).ToList();
        Assert.Equal(["Summary", "Matrix", "Access", "Groups", "Findings", "Changes (90 days)"], names);
        string Text(XDocument d) => string.Join("|", d.Descendants(X + "t").Select(t => t.Value));
        var matrix = Text(Part("xl/worksheets/sheet2.xml"));
        Assert.Contains("Operations\\Sales-Staff", matrix);
        Assert.Contains("G-AllUsers", matrix);
        var accessRows = Part("xl/worksheets/sheet3.xml").Descendants(X + "row").Count();
        Assert.Equal(r.Access.Count + 1, accessRows);
        Assert.Contains("Unresolved SID", Text(Part("xl/worksheets/sheet5.xml")));
    }

    [Fact]
    public void CellsWithCharactersXmlCannotHoldStayValid()
    {
        var path = Path.Combine(Tmp, "odd.xlsx");
        Xlsx.Write(path, [new Sheet("A:B/C", ["Name"], [new[] { "Projekte & Co <alt>" }, new[] { "kaputt\uD800x" }, new[] { " leading" }])]);
        using var zip = ZipFile.OpenRead(path);
        using var s = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var texts = XDocument.Load(s).Descendants(X + "t").Select(t => t.Value).ToList();
        Assert.Equal(["Name", "Projekte & Co <alt>", "kaputt�x", " leading"], texts);
        using var wb = zip.GetEntry("xl/workbook.xml")!.Open();
        Assert.Equal("ABC", (string)XDocument.Load(wb).Descendants(X + "sheet").Single().Attribute("name")!);
        Assert.Equal("AA1", Xlsx.Ref(26, 0));
        Assert.Equal("B3", Xlsx.Ref(1, 2));
    }
}
