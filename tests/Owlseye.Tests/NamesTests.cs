// Names with umlauts, spaces, special characters and emoji: folders, groups, users, share, audit log.
// Port of backend/tests/test_names.py. The FastAPI TestClient calls become calls on Owlseye.Ui.Session; a route that
// answered 200 is a Session call that did not throw, a 400 is a UserError with Status 400.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public class NamesTests : TestBase
{
    static readonly string[] FolderNames =
    [
        "Geschäftsführung",
        "Übergabe Ordner",
        "Projekte & Co",
        "Notizen; alt",
        "O'Brien",
        "100% fertig",
        "a#b+c=d",
        "Ordner 📁",
        "Zwei  Leerzeichen",
        "日本語",
    ];

    const string GroupSid = "S-1-5-21-1-2-3-3001", UserSid = "S-1-5-21-1-2-3-3002";
    const string GroupName = "G-Geschäftsführung Süd";
    const string GroupDn = $"CN={GroupName},OU=Groups,{Demo.Base}";
    const string UserDn = $"CN=Müller\\, Jürgen,OU=Staff,{Demo.Base}"; // comma in the CN, escaped in the DN

    public static TheoryData<string> Folders => [.. FolderNames];

    /// <summary>sim_dir fixture: seeded sim with the special folders, the group and the user.</summary>
    string SimDir()
    {
        var d = SimSeed.Seed(Path.Combine(Tmp, "sim"));
        foreach (var name in FolderNames)
            Directory.CreateDirectory(Path.Combine(d, "share", "Public", name));
        var p = Path.Combine(d, "state.json");
        var s = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8))!.AsObject();
        s["objects"]![GroupDn] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "group"),
            ["sAMAccountName"] = GroupName,
            ["groupType"] = -2147483640L,
            ["member"] = new JsonArray(UserDn),
            ["objectSid"] = GroupSid,
        };
        s["objects"]![UserDn] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "person", "organizationalPerson", "user"),
            ["sAMAccountName"] = "jmüller",
            ["displayName"] = "Jürgen Müller",
            ["userAccountControl"] = 0x200,
            ["objectSid"] = UserSid,
        };
        s["sids"]![GroupSid] = new JsonArray(GroupName, "DEMO", 2);
        s["sids"]![UserSid] = new JsonArray("jmüller", "DEMO", 1);
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // ensure_ascii=False
        File.WriteAllText(p, s.ToJsonString(opts), new UTF8Encoding(false));
        return d;
    }

    /// <summary>create_app(cfg, SimProvider(sim_dir), token): the first scan is done when it returns.</summary>
    Session Client(string simDir)
    {
        var cfg = new Config { Provider = "sim", Audit = Path.Combine(Tmp, "a.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, new SimProvider(simDir));
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return new Session(st);
    }

    /// <summary>GET /preview for the phash, then POST /apply.</summary>
    static Outcome Apply(Session c, string reason = "") => c.Apply(reason, c.Preview().Phash);

    /// <summary>acls[path]["aces"] of the sim's state.json as (type, flags, mask, sid).</summary>
    static List<(long Type, long Flags, long Mask, string Sid)> AcesOf(string simDir, string path)
    {
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(simDir, "state.json"), Encoding.UTF8))!;
        return s["acls"]![path]!["aces"]!.AsArray()
            .Select(a => ((long)a![0]!, (long)a[1]!, (long)a[2]!, (string)a[3]!)).ToList();
    }

    [Fact]
    public void FoldersWithSpecialNamesAreScanned()
    {
        var s = new SimProvider(SimDir()).Scan();
        foreach (var name in FolderNames)
            Assert.True(s.Folders.ContainsKey($"Public\\{name}"), name);
    }

    [Theory]
    [MemberData(nameof(Folders))]
    public void RightOnSpecialFolderThroughTheApp(string name)
    {
        var simDir = SimDir();
        var c = Client(simDir);
        var path = $"Public\\{name}";
        Assert.NotNull(c.FolderPanel(path)); // GET /panel/folder: 200
        Assert.NotNull(c.CellPanel(Demo.Gsid("G-HR"), path)); // GET /panel/cell: 200
        c.SetCell(Demo.Gsid("G-HR"), path, "W"); // POST /cell: 200 (a UserError would be the 400)
        Assert.Contains(c.Matrix().Rows, r => r.Folder.Name == "Geschäftsführung"); // UTF-8 arrives unchanged
        var r = Apply(c);
        Assert.Contains("changes applied", r.Message);
        Assert.Contains((0L, 3L, (long)M.Modify, Demo.Gsid("G-HR")), AcesOf(simDir, path));
        Assert.NotNull(c.FolderPage(path)); // GET /folder: 200
    }

    [Fact]
    public void HtmlSpecialCharactersAreEscaped()
    {
        var m = Client(SimDir()).Matrix();
        // UI: Razor escapes output (the Python test checked ">Projekte &amp; Co<" in the rendered page). Here: the name
        // reaches the view model unchanged, so the escaping is left to the renderer.
        Assert.Contains(m.Rows, r => r.Folder.Name == "Projekte & Co");
        Assert.DoesNotContain(m.Rows, r => r.Folder.Name == "Projekte &amp; Co");
    }

    [Fact]
    public void NewFoldersWithUmlautsAndSpaces()
    {
        var simDir = SimDir();
        var c = Client(simDir);
        foreach (var name in new[] { "Neue Übersicht", "Ärger; 2025", "Straße 12" })
            Assert.Equal("Public\\" + name, c.NewFolder("Public", name)); // POST /folder/new: 200
        foreach (var bad in new[] { "Ende.", "a/b", "x:y", "CON", "Tab\there" })
        {
            var e = Assert.Throws<UserError>(() => c.NewFolder("Public", bad));
            Assert.True(e.Status == 400, bad);
        }
        c.NewFolder("Public", "  Ende  "); // outer spaces: trimmed
        Apply(c);
        foreach (var name in new[] { "Neue Übersicht", "Ärger; 2025", "Straße 12", "Ende" })
            Assert.True(Directory.Exists(Path.Combine(simDir, "share", "Public", name)), name);
    }

    [Fact]
    public void GroupWithUmlautsAndSpaces()
    {
        var simDir = SimDir();
        var c = Client(simDir);
        var page = c.Groups("G-Gesch"); // GET /panel/groups: the template shows h.name
        Assert.Contains(page.Hits, h => h.Name.Contains(GroupName, StringComparison.Ordinal));
        c.AddColumn(GroupSid, "G-Gesch");
        Assert.Contains(c.Matrix().Columns, p => p.Short == GroupName); // f">{GROUP}<": the column header shows p.short
        c.SetCell(GroupSid, "HR", "R");
        var r = Apply(c, reason: "Für die Geschäftsführung");
        Assert.Contains("changes applied", r.Message);
        Assert.Contains(c.AuditPage().Entries, e => e.Str("reason") == "Für die Geschäftsführung");
        var s = new SimProvider(simDir).Scan();
        var p = s.Principals[GroupSid];
        Assert.Equal(($"DEMO\\{GroupName}", GroupDn), (p.Name, p.Dn));
        Assert.Equal([UserDn], Rights.MembersOf(s, GroupSid)!); // DN with escaped comma resolved
        Assert.Equal("R", Rights.UserRights(s)[UserDn]["HR"]);
        var users = c.Users(u: UserDn); // GET /users?u=...
        Assert.Equal("Jürgen Müller", users.Selected?.Display); // users.html: header of the selected user
        Assert.Contains(users.Detail, d => d.Via.Any(v => v.Contains(GroupName, StringComparison.Ordinal))); // "via" column
    }

    [Fact]
    public void LdapEscapeRoundtripForUmlautsAndCommas()
    {
        foreach (var value in new[] { GroupName, UserDn, "Ärger (alt) * \\ ; < > ," })
        {
            var node = LdapFilter.Parse($"(sAMAccountName={AdProvider.LdapEscape(value)})");
            Assert.True(LdapFilter.Matches(node, new Dictionary<string, object?> { ["sAMAccountName"] = value }), value);
            Assert.False(LdapFilter.Matches(node, new Dictionary<string, object?> { ["sAMAccountName"] = value + "x" }));
        }
    }

    [Fact]
    public void DesiredStateFileForShareWithUmlauts()
    {
        var share = @"\\fs01\Geschäft Data";
        Assert.Equal("desired-fs01_geschäft_data.json", BaselineStore.FileName(share));
        var store = new BaselineStore(Tmp);
        var d = new Desired
        {
            Cells = new() { [(GroupSid, "Übergabe Ordner")] = "W" },
            Names = new() { [GroupSid] = $"DEMO\\{GroupName}" },
            Protected = ["Übergabe Ordner"],
        };
        store.Save(share, d, actor: "DEMO\\Jürgen");
        AssertSameDesired(d, new BaselineStore(Tmp).Load(share));
        Assert.Contains("Übergabe Ordner", File.ReadAllText(Path.Combine(Tmp, BaselineStore.FileName(share)), Encoding.UTF8)); // readable, not \u00dc
    }

    /// <summary>Folders with a trailing dot/space (creatable via \\?\) are not written by owlseye; the preview shows this.</summary>
    [Fact]
    public void UnwritableNameIsRefusedInThePreviewNotAtApply()
    {
        var s = new SimProvider(SimDir()).Scan();
        s.Folders["Public\\Ende."] = new Folder("Public\\Ende.", 2);
        var e = Assert.Throws<PlanError>(() =>
            Planner.Build(s, new Dictionary<(string, string), string?> { [(Demo.Gsid("G-Management"), "Public\\Ende.")] = "R" }));
        Assert.Contains("does not write", e.Message);
    }

    [Fact]
    public void PlannerOnSpecialNames()
    {
        var s = new SimProvider(SimDir()).Scan();
        var path = "Public\\Notizen; alt";
        var plan = Planner.Build(s, new Dictionary<(string, string), string?> { [(Demo.Gsid("G-Management"), path)] = "R" });
        Assert.Equal(path, plan.AclOps.Select(o => o.Path).ToList()[^1]);
    }

    /// <summary>Python compared the Desired dataclasses with ==.</summary>
    static void AssertSameDesired(Desired expected, Desired? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Cells, actual.Cells);
        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Protected, actual.Protected);
    }
}
