// The Groups page and the membership matrix, on the demo company: G-Operations contains the sales and service groups,
// G-AllUsers and Authenticated Users reach every user.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class GroupsPageTests : TestBase
{
    Session Client()
    {
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") };
        var st = new State(cfg, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return new Session(st);
    }

    static string G(string sam) => Demo.Gsid(sam);

    [Fact]
    public void ListsTheGroupsWithRights()
    {
        var v = Client().GroupsPage();
        var names = v.Groups.Select(g => g.P.Short).ToList();
        Assert.Contains("G-HR", names);
        Assert.Contains("Authenticated Users", names); // well-known groups with rights count too
        Assert.DoesNotContain("mmoore", names); // a user with an entry of their own is no group
        Assert.Null(v.Selected);
        var hr = v.Groups.Single(g => g.P.Short == "G-HR");
        Assert.Equal((1, 1, false), (hr.Users, hr.Folders, hr.Everyone));
        Assert.True(v.Groups.Single(g => g.P.Short == "G-AllUsers").Everyone);
        Assert.True(v.Groups.Single(g => g.P.Short == "Authenticated Users").Everyone);
        Assert.Equal(["G-HR"], Client().GroupsPage("-hr").Groups.Select(g => g.P.Short));
    }

    [Fact]
    public void AGroupShowsItsRightsMembersAndNesting()
    {
        var v = Client().GroupsPage(sid: G("G-Operations"));
        Assert.Equal("G-Operations", v.Selected!.P.Short);
        Assert.Contains(v.Rights, r => r.Folder.Path == "Operations" && r.Right == "R|" && r.Own);
        Assert.Equal(["G-Sales-Lead", "G-Sales-Staff", "G-Service-Lead", "G-Service-Staff"], v.Nested.Select(p => p.Short));
        var alice = v.Members.Single(m => m.User.Sam == "aadams");
        Assert.Equal("G-Sales-Lead", alice.Via); // member through the nested group
        Assert.All(v.Members, m => Assert.NotEqual("", m.Via)); // G-Operations has groups only, no direct users

        var lead = Client().GroupsPage(sid: G("G-Sales-Lead"));
        Assert.Equal(["G-Operations"], lead.MemberOf.Select(p => p.Short));
        Assert.Equal([("aadams", "")], lead.Members.Select(m => (m.User.Sam, m.Via)));
    }

    [Fact]
    public void MembershipMatrixMarksDirectAndNestedAndLeavesOutEveryoneGroups()
    {
        var mv = Client().MembershipMatrix();
        var cols = mv.Columns.Select(c => c.Short).ToList();
        Assert.DoesNotContain("G-AllUsers", cols);
        Assert.DoesNotContain("Authenticated Users", cols);
        Assert.Contains("G-AllUsers", mv.Everyone.Select(p => p.Short));
        var alice = mv.Rows.Single(r => r.User.Sam == "aadams");
        Assert.Equal('D', alice.Marks[cols.IndexOf("G-Sales-Lead")]);
        Assert.Equal('N', alice.Marks[cols.IndexOf("G-Operations")]);
        Assert.Equal(' ', alice.Marks[cols.IndexOf("G-HR")]);
        Assert.All(mv.Rows, r => Assert.NotEqual("", r.Marks.Trim())); // only users in at least one column
        Assert.Equal(["aadams"], Client().MembershipMatrix("alice").Rows.Select(r => r.User.Sam));
    }

    [Fact]
    public void AllGroupsAddsTheGroupsWithoutRights()
    {
        var s = Client();
        var withRights = s.GroupsPage().Groups.Select(g => g.P.Sid).ToHashSet();
        var all = s.GroupsPage(all: true).Groups.Select(g => g.P.Sid).ToHashSet();
        Assert.Superset(withRights.Where(sid => sid.StartsWith("S-1-5-21-")).ToHashSet(), all);
    }
}
