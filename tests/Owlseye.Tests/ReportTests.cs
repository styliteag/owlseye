// Access rights report: content built from the scan, and the Excel workbook.

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

    /// <summary>The demo share plus a user in no group, and without the Authenticated Users entry on Public (which
    /// reaches everybody): that user has no access anywhere.</summary>
    sealed class WithIdleUser : IProvider
    {
        readonly DemoProvider inner = new();

        public WithIdleUser()
        {
            var (prot, aces) = inner.FolderAcl("Public");
            inner.SetFolderAcl("Public", prot, [.. aces.Where(a => a.Sid != "S-1-5-11" && !a.Inherited)]);
        }
        public string Name => inner.Name;
        public string Share => inner.Share;
        public string WhoAmI() => inner.WhoAmI();
        public Snapshot Scan(Progress? progress = null)
        {
            var s = inner.Scan(progress);
            var users = new Dictionary<string, User>(s.Users) { ["CN=Idle"] = new User("CN=Idle", "idle", "Ida Idle", true, "S-1-5-21-1-2-3-1999") };
            return s with { Users = users };
        }
        public (bool Protected, List<Ace> Aces) FolderAcl(string path) => inner.FolderAcl(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public void CreateFolder(string path) => inner.CreateFolder(path);
        public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => inner.SetFolderAcl(path, isProtected, aces);
        public List<Principal> FindGroups(string q) => inner.FindGroups(q);
    }

    [Fact]
    public void UsersWithoutAccessAndAccountsWithoutRightsAreLeftOut()
    {
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") };
        var st = new State(cfg, new WithIdleUser());
        st.Load();
        new Session(st).AddColumn("S-1-5-32-545", "users"); // BUILTINSERS ADDED BY HAND: A COLUMN WITHOUT ANY RIGHT
        var r = ReportBuilder.Build(st);
        Assert.DoesNotContain(r.PerUser, u => u.User.Sam == "idle");
        Assert.Equal((15, 16), (r.UsersWithAccess, r.UsersTotal));
        Assert.All(r.Columns, c => Assert.Contains(r.Matrix, row => row.Cells[r.Columns.ToList().IndexOf(c)].Value is not null));
    }

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
        Assert.Equal("F", payroll.Cells[r.Columns.ToList().FindIndex(p => p.Short == "G-IT")].Text); // full control
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
        Xlsx.Write(path, [new Sheet("A:B/C", ["Name"], [new[] { "Projects & Co <old>" }, new[] { "broken\uD800x" }, new[] { " leading" }])]);
        using var zip = ZipFile.OpenRead(path);
        using var s = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var texts = XDocument.Load(s).Descendants(X + "t").Select(t => t.Value).ToList();
        Assert.Equal(["Name", "Projects & Co <old>", "broken�x", " leading"], texts);
        using var wb = zip.GetEntry("xl/workbook.xml")!.Open();
        Assert.Equal("ABC", (string)XDocument.Load(wb).Descendants(X + "sheet").Single().Attribute("name")!);
        Assert.Equal("AA1", Xlsx.Ref(26, 0));
        Assert.Equal("B3", Xlsx.Ref(1, 2));
    }
}
