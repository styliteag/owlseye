// ACL model: cells from ACEs, inheritance, user rights, findings (demo share like the Excel list).
// Port of backend/tests/test_acl_model.py.

using Owlseye.Providers;

namespace Owlseye.Tests;

public class AclModelTests : TestBase
{
    const string OPS = "Operations";

    readonly Snapshot snap = new DemoProvider().Scan();

    static Cell? CellOf(Snapshot s, string group, string path) =>
        Rights.Matrix(s).GetValueOrDefault((Demo.Gsid(group), path));

    [Theory]
    [InlineData(M.Read, M.OiCi, "R", true)]
    [InlineData(M.Modify, M.OiCi, "W", true)] // W is Modify by default ("write": "modify")
    [InlineData(M.Read, 0, "R|", true)]
    [InlineData(M.Modify, 0, "W|", true)]
    [InlineData(M.Full, M.OiCi, "F", true)] // full control
    [InlineData(M.WriteNoDelete, M.OiCi, "W", false)] // write without delete: special unless "write": "no-delete"
    [InlineData(0x100021u, 0, "R|", false)] // list/traverse only
    public void Classify(uint mask, int flags, string value, bool standard)
    {
        Assert.Equal((value, standard), Rights.Classify([new Ace("S-1-5-21-9", "X\\G", "group", mask, Flags: flags)]));
    }

    [Fact]
    public void TwoEntriesForOneGroupAreNonStandard()
    {
        Ace[] aces = [new("S", "X\\G", "group", M.Read, Flags: 0), new("S", "X\\G", "group", M.Write, Flags: 0xB)];
        Assert.Equal(("W", false), Rights.Classify(aces));
    }

    [Fact]
    public void ColumnsAreAccountsInTheAcls()
    {
        var names = snap.Principals.Values.Select(p => p.Short).ToHashSet();
        Assert.Superset(new HashSet<string> { "G-Management", "G-AllUsers", "P-Payroll", "mmoore", "Authenticated Users" }, names);
        Assert.False(new HashSet<string> { "SYSTEM", "Administrators" }.Overlaps(names));
    }

    [Fact]
    public void RwInheritUntilInheritanceIsBroken()
    {
        var c = CellOf(snap, "G-Sales-Staff", $@"{OPS}\Sales-Staff\2025")!;
        Assert.Equal(((string?)null, "W", $@"{OPS}\Sales-Staff"), (c.Direct, c.Effective, c.Source));
        Assert.Equal("W", CellOf(snap, "G-Management", $@"{OPS}\Sales-Staff")!.Effective); // from Operations
        Assert.NotNull(CellOf(snap, "G-AllUsers", "Public"));
        Assert.Equal(@"Public\Transfer", CellOf(snap, "G-AllUsers", @"Public\Transfer")!.Source); // protected, own entry
        Assert.Null(CellOf(snap, "G-IT", "HR")); // W on the root does not reach protected folders
    }

    [Fact]
    public void ListEntriesDoNotInherit()
    {
        Assert.Equal("R|", CellOf(snap, "G-Sales-Staff", OPS)!.Direct);
        Assert.Null(CellOf(snap, "G-Sales-Staff", $@"{OPS}\Sales-QA"));
    }

    [Fact]
    public void RootInheritsFromAboveTheShare()
    {
        var root = new Folder("", 0, false, [new Ace("S-1-5-21-7", "X\\G", "group", M.Read, Inherited: true, Flags: M.OiCi | 0x10)]);
        var s = new Snapshot(
            "E:\\S",
            new Dictionary<string, Folder> { [""] = root, ["A"] = new Folder("A", 1) },
            new Dictionary<string, Principal> { ["S-1-5-21-7"] = new Principal("S-1-5-21-7", "X\\G", "group") },
            [],
            []);
        var c = Rights.Matrix(s);
        Assert.Equal(Rights.Above, c[("S-1-5-21-7", "")].Source);
        Assert.Equal("R", c[("S-1-5-21-7", "A")].Effective);
    }

    [Fact]
    public void RootRightReachesUnprotectedFoldersAndCountsForUsers()
    {
        var (sid, dn, anna) = ("S-1-5-21-5", "CN=G,OU=G,DC=x", "CN=anna,OU=U,DC=x");
        var folders = new Dictionary<string, Folder>
        {
            [""] = new Folder("", 0, true, [new Ace(sid, "X\\G", "group", M.Read, Flags: M.OiCi)]),
            ["X"] = new Folder("X", 1),
            ["P"] = new Folder("P", 1, Protected: true),
        };
        var s = new Snapshot(
            "E:\\S",
            folders,
            new Dictionary<string, Principal> { [sid] = new Principal(sid, "X\\G", "group", dn) },
            new Dictionary<string, Group> { [dn] = new Group(dn, "G", [anna], sid) },
            new Dictionary<string, User> { [anna] = new User(anna, "anna", "Anna", Sid: "S-1-5-21-6") });
        var c = Rights.Matrix(s);
        Assert.Equal("R", c[(sid, "")].Direct);
        Assert.Equal(("R", ""), (c[(sid, "X")].Effective, c[(sid, "X")].Source));
        Assert.False(c.ContainsKey((sid, "P"))); // inheritance broken
        Assert.Equal(new Dictionary<string, string> { [""] = "R", ["X"] = "R" }, Rights.UserRights(s)[anna]);
    }

    [Fact]
    public void UserRightsThroughNestedGroups()
    {
        var ur = Rights.UserRights(snap);
        var anna = ur[Demo.Udn("aadams")]; // G-Sales-Lead, above it G-Operations, and G-AllUsers
        Assert.Equal("W", anna[$@"{OPS}\Sales-Lead"]);
        Assert.Equal("R|", anna[OPS]);
        Assert.Equal("R", anna["Programs"]);
        Assert.False(anna.ContainsKey($@"{OPS}\Service-Lead"));
    }

    [Fact]
    public void MembersOfGroupColumn()
    {
        var members = Rights.MembersOf(snap, Demo.Gsid("G-Operations"))!;
        Assert.True(members.Contains(Demo.Udn("aadams")) && members.Contains(Demo.Udn("hhill")));
        Assert.DoesNotContain(Demo.Udn("cclark"), members);
        Assert.Null(Rights.MembersOf(snap, "S-1-5-21-1-2-3-4711"));
    }

    [Fact]
    public void ViaExplainsTheGroup()
    {
        var v = Rights.Via(snap, Demo.Udn("cclark"), @"Programs\Payroll");
        Assert.Contains(v, x => x.StartsWith("P-Payroll (W", StringComparison.Ordinal));
    }

    [Fact]
    public void FindingsOfTheDemo()
    {
        var texts = Rights.Findings(snap).Select(f => (f.Severity, f.Path, f.Text)).ToHashSet();
        Assert.Contains(("high", "Programs", "Unresolved SID S-1-5-21-1-2-3-4711"), texts);
        Assert.Contains(("high", "Public", "Broad permission for Authenticated Users"), texts);
        Assert.Contains(("high", @"Public\Transfer", "Direct user entry for DEMO\\mmoore"), texts);
        Assert.Contains(("medium", @"Programs\ERP", "Administrators without full control here"), texts);
        Assert.Contains(("low", @"Programs\Payroll", "Full control for G-IT: its members can change permissions and take ownership"), texts);
        Assert.Contains(("medium", OPS, $@"G-Service-QA cannot open this folder to reach {OPS}\Service-QA (R| missing)"), texts);
        Assert.Contains(texts, t => t.Text.StartsWith("Explicit entry below level 3", StringComparison.Ordinal));
    }

    /// <summary>G-HR has no R| on the root, but all members get in via G-AllUsers.</summary>
    [Fact]
    public void GroupCoveredByAllUsersNeedsNoListEntry()
    {
        var texts = Rights.Findings(snap).Select(f => f.Text).ToList();
        Assert.DoesNotContain(texts, t => t.StartsWith("G-HR cannot open", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockedWhereInheritanceIsBroken()
    {
        var c = Rights.Matrix(snap);
        var b = Rights.BlockedCells(snap, c);
        Assert.False(b.ContainsKey((Demo.Gsid("G-Management"), $@"{OPS}\Sales-Lead"))); // its own entry there
        Assert.False(b.ContainsKey((Demo.Gsid("G-Sales-Lead"), $@"{OPS}\Service-QA"))); // only R| above, which does not inherit
        Assert.Equal(("R", "Programs"), b.GetValueOrDefault((Demo.Gsid("G-AllUsers"), @"Programs\Payroll")));
    }

    [Fact]
    public void UserAceColumnHasSidOfTheUser()
    {
        var p = snap.Principals[Demo.Usid("mmoore")];
        Assert.True(p.Kind == "user" && p.Dn == Demo.Udn("mmoore"));
    }
}
