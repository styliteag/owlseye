// Fixes from the code review of the port; several of these bugs were in the Python version as well.

using System.Text.Json.Nodes;
using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class ReviewFixTests : TestBase
{
    (State St, Session S) Client(IProvider provider)
    {
        var cfg = new Config { Provider = provider.Name, Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        var st = new State(cfg, provider);
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    [Fact]
    public void NameWithALoneSurrogateIsSuspiciousAndDoesNotBreakTheScan()
    {
        var name = "Data\uD800x";
        Assert.True(M.SuspiciousName(name));
        var (snap, _) = Demo.Build();
        var folders = new Dictionary<string, Folder>(snap.Folders) { [name] = new Folder(name, 1) };
        var findings = Rights.Findings(snap with { Folders = folders });
        Assert.Contains(findings, f => f.Path == name && f.Text.Contains("lookalike"));
    }

    [Fact]
    public void ComputerAccountInAnAclHasNoKnownMembersAndItsPanelOpens()
    {
        var dir = SimSeed.Seed(Path.Combine(Tmp, "sim"));
        var p = Path.Combine(dir, "state.json");
        var s = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        const string sid = "S-1-5-21-1-2-3-3000";
        s["objects"]!["CN=FS01,OU=Server,DC=demo,DC=local"] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "person", "organizationalPerson", "user", "computer"),
            ["sAMAccountName"] = "FS01$", ["objectSid"] = sid,
        };
        s["sids"]![sid] = new JsonArray("FS01$", "DEMO", 1); // SidTypeUser, as Windows reports computer accounts
        s["acls"]!["HR"]!["aces"]!.AsArray().Add(new JsonArray(0, 3, (long)M.Read, sid));
        File.WriteAllText(p, s.ToJsonString());
        var (st, session) = Client(new SimProvider(dir));
        Assert.Equal("user", st.Snap.Principals[sid].Kind);
        Assert.Null(Rights.MembersOf(st.Snap, sid));
        var panel = session.CellPanel(sid, "HR");
        Assert.Null(panel.Members);
        Assert.Equal("?", session.Matrix().UsersIn[sid]);
    }

    [Fact]
    public void CyclicGroupNestingKeepsAllGroups()
    {
        var groups = new Dictionary<string, Group>
        {
            ["A"] = new("A", "A", ["B", "u1"], "S-A"),
            ["B"] = new("B", "B", ["A", "u2"], "S-B"),
        };
        var users = new Dictionary<string, User> { ["u1"] = new("u1", "u1", "U1"), ["u2"] = new("u2", "u2", "U2") };
        var snap = new Snapshot("x", [], [], groups, users);
        var tg = Rights.Transitive(snap);
        Assert.Equal(["A", "B"], tg["u1"].Order(StringComparer.Ordinal));
        Assert.Equal(["A", "B"], tg["u2"].Order(StringComparer.Ordinal));
        Assert.Equal(["B"], tg["A"]);
        Assert.Equal(["A"], tg["B"]);
    }

    [Fact]
    public void FolderRenamedOnlyInCaseIsNoDrift()
    {
        var snap = new DemoProvider().Scan();
        var desired = Drift.DesiredOf(snap);
        var folders = snap.Folders.ToDictionary(kv => kv.Key == "HR" ? "hr" : kv.Key, kv => kv.Key == "HR" ? kv.Value with { Path = "hr" } : kv.Value);
        var renamed = snap with { Folders = folders };
        var items = Drift.Diff(renamed, desired);
        Assert.Empty(items);
        Assert.Empty(Drift.ImpactOf(renamed, items));
        // a real change on the renamed folder is reported under its current spelling and can be restored or kept
        var hr = renamed.Folders["hr"];
        folders["hr"] = hr with { Aces = hr.Aces.Where(a => a.Sid != Demo.Gsid("G-HR")).ToList() };
        items = Drift.Diff(renamed with { Folders = folders }, desired);
        var d = Assert.Single(items);
        Assert.Equal(("removed", "hr"), (d.Change, d.Path));
        var kept = Drift.Accept(desired, items);
        Assert.DoesNotContain(kept.Cells.Keys, k => k.Sid == Demo.Gsid("G-HR") && M.Lower(k.Path) == "hr");
    }

    [Fact]
    public void UnreachableBaselineFolderKeepsTheScanAndSaysWhy()
    {
        var provider = new DemoProvider();
        var cfg = new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl"), Baseline = Path.Combine(Tmp, "base") };
        Directory.CreateDirectory(Path.Combine(Tmp, "base", Path.ChangeExtension(BaselineStore.FileName(Demo.Share), ".lock")));
        var st = new State(cfg, provider);
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        Assert.NotEqual("", st.BaselineError);
        Assert.Empty(st.Drift);
    }

    [Fact]
    public void LoweringTheDepthDropsWhatWasPendingOnDeeperNewFolders()
    {
        var (st, s) = Client(new DemoProvider());
        var path = s.NewFolder(@"Operations\Sales-Staff", "Neu");
        s.SetCell(Demo.Gsid("G-HR"), path, "W");
        s.SetDepth(2);
        Assert.DoesNotContain(path, st.PendingNew.Keys);
        Assert.DoesNotContain(st.Pending.Keys, k => k.Path == path);
        st.Plan(); // still plannable
    }

    // --- security review, finding 3: names and SIDs from files are not trusted ---

    /// <summary>A desired state edited on the admin share gives the account of Alice Adams (in no ACL) the name "G-Management":
    /// the drift page and the restore preview show who it really is.</summary>
    [Fact]
    public void RestoreShowsTheResolvedNameNotTheOneInTheFile()
    {
        var (st, s) = Client(new DemoProvider());
        var anna = Demo.Usid("aadams");
        st.Baseline.Update(st.Snap.Share, d =>
        {
            d.Cells[(anna, "Public")] = "W";
            d.Names[anna] = "DEMO\\G-Management";
            return d;
        }, "tamper");
        st.Rescan();
        Assert.Contains(st.Drift, d => d.Sid == anna && d.Name == "DEMO\\G-Management"); // what the file says
        var item = Assert.Single(s.DriftPage().Items, d => d.Sid == anna);
        Assert.Equal("DEMO\\aadams", item.Name); // what is shown
        s.DriftRevert([item.Key]);
        var change = s.Preview().Plan!.AclOps.SelectMany(o => o.Changes).Single(c => c.Sid == anna && c.Path == "Public");
        Assert.Equal("DEMO\\aadams", change.Name);
    }

    /// <summary>A desired state with a SID nobody can resolve, named like a real group: restoring it is refused.</summary>
    [Fact]
    public void RestoreOfAnUnresolvableSidIsRefused()
    {
        var (st, s) = Client(new DemoProvider());
        const string ghost = "S-1-5-21-9-9-9-666";
        st.Baseline.Update(st.Snap.Share, d =>
        {
            d.Cells[(ghost, "Public")] = "W";
            d.Names[ghost] = "DEMO\\G-Finance";
            return d;
        }, "tamper");
        st.Rescan();
        var item = Assert.Single(s.DriftPage().Items, d => d.Sid == ghost);
        Assert.StartsWith($"{ghost} (unresolved; the file calls it DEMO\\G-Finance)", item.Name);
        Assert.Equal("/preview", s.DriftRevert([item.Key]).Url);
        var preview = s.Preview();
        Assert.Null(preview.Plan);
        Assert.Contains("cannot be resolved", preview.Error);
    }

    /// <summary>A log entry forged to "undo" a right for an unresolvable SID cannot grant it.</summary>
    [Fact]
    public void UndoOfAForgedEntryForAnUnknownSidIsRefused()
    {
        var (st, s) = Client(new DemoProvider());
        var entry = st.Audit.Append(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "change", ["actor"] = "DEMO\\admin", ["share"] = st.Snap.Share, ["status"] = "ok",
            ["acl_ops"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = "Public", ["protected_before"] = true, ["protected_after"] = true,
                ["before"] = new System.Text.Json.Nodes.JsonArray(), ["after"] = new System.Text.Json.Nodes.JsonArray(),
                ["changes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["sid"] = "S-1-5-21-9-9-9-666", ["name"] = "DEMO\\G-Management", ["path"] = "Public", ["before"] = "W", ["after"] = null,
                }),
            }),
        });
        Assert.Equal("/preview", s.Undo(entry.Str("id")!).Url);
        Assert.Contains("cannot be resolved", s.Preview().Error);
        // and a log entry whose account is not a SID at all is not read
        var bad = st.Audit.Append(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "change", ["share"] = st.Snap.Share,
            ["acl_ops"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = "Public", ["protected_before"] = true, ["protected_after"] = true,
                ["before"] = new System.Text.Json.Nodes.JsonArray(), ["after"] = new System.Text.Json.Nodes.JsonArray(),
                ["changes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["sid"] = "Everyone", ["name"] = "x", ["path"] = "Public", ["before"] = "W", ["after"] = null,
                }),
            }),
        });
        Assert.Equal(400, Assert.Throws<UserError>(() => s.Undo(bad.Str("id")!)).Status);
    }

    /// <summary>Security review, finding 10: odd values in a hand-edited log line do not break the log.</summary>
    [Fact]
    public void UnfinishedApplyWithOddPlannedValuesStillFolds()
    {
        var log = new AuditLog(Path.Combine(Tmp, "a.jsonl"));
        log.Append(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "change_start", ["share"] = "x",
            ["planned_create"] = new System.Text.Json.Nodes.JsonArray(42, "Neu"),
            ["planned_acl"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject(), "HR", 7),
        });
        var e = Assert.Single(log.Entries());
        Assert.Equal(["Neu", "HR"], e.Arr("missing")!.Select(m => (string)m!).ToArray());
    }

    [Fact]
    public void BaselineWithAnAccountThatIsNoSidIsRefused()
    {
        var dir = Path.Combine(Tmp, "b");
        var store = new BaselineStore(dir);
        File.WriteAllText(Path.Combine(dir, BaselineStore.FileName(@"\\fs\x")),
            """{"share": "\\\\fs\\x", "cells": [["Everyone", "A", "W"]], "names": {}, "protected": []}""");
        Assert.Throws<BaselineError>(() => store.Load(@"\\fs\x"));
    }

    [Fact]
    public void CancellingANewFolderAlsoDropsItsPendingInheritance()
    {
        var (st, s) = Client(new DemoProvider());
        st.PendingNew[@"HR\Weg"] = "HR"; // as "Restore" puts a missing folder with broken inheritance
        st.PendingFolders[@"HR\Weg"] = true;
        st.Plan();
        s.CancelNewFolder(@"HR\Weg");
        Assert.Empty(st.PendingFolders);
        Assert.Equal(0, st.PendingCount);
        st.Plan();
    }
}
