// Planner: cells -> ACL operations, automatic R|, inheritance, new folders, effect on users.

using Owlseye.Providers;

namespace Owlseye.Tests;

public class AclPlannerTests : TestBase
{
    const string OPS = "Operations";

    readonly Snapshot snap = new DemoProvider().Scan();

    static string Gsid(string sam) => Demo.Gsid(sam);

    /// <summary>{(sid, path): value} with one entry.</summary>
    static Dictionary<(string Sid, string Path), string?> Ch(string sid, string path, string? value) =>
        new() { [(sid, path)] = value };

    static readonly Dictionary<(string Sid, string Path), string?> NoCells = [];

    /// <summary>{path: inheritance broken?} with one entry.</summary>
    static Dictionary<string, bool> Protect(string path, bool protect) => new() { [path] = protect };

    static HashSet<(string Name, string Path, string? Before, string? After, bool Auto)> Changes(Plan plan) =>
        plan.AclOps.SelectMany(o => o.Changes.Select(c => (c.Name.Split('\\')[^1], o.Path, c.Before, c.After, c.Auto))).ToHashSet();

    static HashSet<(string, string, string?, string?, bool)> Set(params (string, string, string?, string?, bool)[] items) => [.. items];

    [Fact]
    public void RightAddsListEntriesOnParents()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Interns"), $@"{OPS}\Sales-QA", "R"));
        Assert.Equal(
            Set(
                ("G-Interns", $@"{OPS}\Sales-QA", null, "R", false),
                ("G-Interns", OPS, null, "R|", true),
                ("G-Interns", "", null, "R|", true)), // empty group: nobody gets in another way
            Changes(plan));
        Assert.Equal(["", OPS, $@"{OPS}\Sales-QA"], plan.AclOps.Select(o => o.Path)); // parents first
    }

    [Fact]
    public void NoListEntryWhereMembersAlreadyGetIn()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-HR"), @"Programs\CRM", "W"));
        Assert.Equal(Set(("G-HR", @"Programs\CRM", null, "W", false)), Changes(plan)); // G-AllUsers: R on Programs
    }

    [Fact]
    public void NoListEntryBelowAnInheritedRight()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Sales-Staff"), $@"{OPS}\Sales-Staff\2025", "W|"));
        Assert.Equal(Set(("G-Sales-Staff", $@"{OPS}\Sales-Staff\2025", null, "W|", false)), Changes(plan));
    }

    [Fact]
    public void RemovingTheLastRightRemovesTheListEntry()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Sales-QA"), $@"{OPS}\Sales-QA", null));
        Assert.Equal(
            Set(
                ("G-Sales-QA", $@"{OPS}\Sales-QA", "W", null, false),
                ("G-Sales-QA", OPS, "R|", null, true)),
            Changes(plan));
    }

    [Fact]
    public void ListEntryStaysWhileOtherRightsRemainBelow()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Sales-Lead"), $@"{OPS}\Sales-QA", null));
        Assert.Equal(Set(("G-Sales-Lead", $@"{OPS}\Sales-QA", "W", null, false)), Changes(plan));
    }

    [Fact]
    public void ManualListEntryIsKept()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Interns"), OPS, "R|"));
        Assert.Contains(("G-Interns", OPS, (string?)null, (string?)"R|", false), Changes(plan));
        plan = Planner.Build(snap, Ch(Gsid("G-Operations"), OPS, null)); // R| with nothing below it, removed by hand
        Assert.Equal(Set(("G-Operations", OPS, "R|", null, false)), Changes(plan));
    }

    [Fact]
    public void SameValueIsANoOp()
    {
        Assert.True(Planner.Build(snap, Ch(Gsid("G-Management"), OPS, "W")).Empty);
    }

    [Fact]
    public void SameValueOnNonStandardEntryNormalizesIt()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-IT"), @"Programs\Payroll", "W"));
        var op = Assert.Single(plan.AclOps);
        var edv = op.After.Where(a => a.Sid == Gsid("G-IT")).ToList();
        Assert.Equal([(M.Write, 3)], edv.Select(a => (a.Mask, a.Flags)));
    }

    [Fact]
    public void ChangingTheRightReplacesTheEntry()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-HR"), "HR", "R")); // had W
        Assert.Equal(Set(("G-HR", "HR", "W", "R", false)), Changes(plan));
        var op = Assert.Single(plan.AclOps);
        Assert.Equal([(M.Read, 3)], op.After.Where(a => a.Sid == Gsid("G-HR")).Select(a => (a.Mask, a.Flags)));
    }

    [Fact]
    public void OtherEntriesAreKept()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Management"), @"Public\Transfer", "R"));
        var op = Assert.Single(plan.AclOps, o => o.Path == @"Public\Transfer");
        var kept = op.After.Select(a => a.Sid).ToHashSet();
        Assert.Superset(new HashSet<string> { M.System, M.Admins, Gsid("G-AllUsers"), Gsid("G-Transfer") }, kept);
        Assert.Contains(op.After, a => a.Kind == "user"); // mmoore stays: a finding instead of a silent removal
    }

    [Fact]
    public void AdminsAreAddedToProtectedFoldersThatAreWritten()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Management"), @"Programs\ERP", "R"));
        var op = Assert.Single(plan.AclOps, o => o.Path == @"Programs\ERP");
        Assert.Contains(op.After, a => a.Sid == M.Admins && a.Mask == M.Full);
    }

    [Fact]
    public void BreakInheritanceCopiesInheritedEntries()
    {
        var plan = Planner.Build(snap, NoCells, Protect($@"{OPS}\Service-Staff", true));
        var op = Assert.Single(plan.AclOps);
        Assert.Equal((false, true), (op.ProtectedBefore, op.ProtectedAfter));
        Assert.Contains(("G-Management", $@"{OPS}\Service-Staff", (string?)null, (string?)"W", false), Changes(plan));
        Assert.Empty(plan.Impact); // nobody loses anything
    }

    [Fact]
    public void BreakThenRemoveShowsWhoLosesAccess()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-Management"), $@"{OPS}\Service-Staff", null), Protect($@"{OPS}\Service-Staff", true));
        Assert.Equal(
            new HashSet<(string, string, string?)> { ("cclark", $@"{OPS}\Service-Staff", null) },
            plan.Impact.Select(i => (i.User, i.Path, i.After)).ToHashSet());
    }

    /// <summary>R|/W| apply to their own folder only; breaking inheritance must not copy any of them into the subfolder.</summary>
    [Fact]
    public void ListEntriesOfTheParentAreNotCopiedWhenBreaking()
    {
        var op = Assert.Single(Planner.Build(snap, NoCells, Protect($@"{OPS}\Service-Staff", true)).AclOps);
        var sids = op.After.Select(a => a.Sid).ToHashSet();
        Assert.Contains(Gsid("G-Management"), sids); // W from Operations is copied
        Assert.DoesNotContain(Gsid("G-Operations"), sids); // has only R| there
    }

    [Fact]
    public void UnchangedInheritanceFlagIsANoOp()
    {
        Assert.True(Planner.Build(snap, NoCells, Protect(@"Public\Transfer", true)).Empty); // already broken
        Assert.True(Planner.Build(snap, NoCells, Protect($@"{OPS}\Service-Staff", false)).Empty); // already inherits
    }

    [Fact]
    public void RestoreDropsCopiesOfWhatTheParentPasses()
    {
        var plan = Planner.Build(snap, NoCells, Protect($@"{OPS}\Sales-Lead", false));
        var op = Assert.Single(plan.AclOps);
        Assert.Contains(("G-Management", $@"{OPS}\Sales-Lead", (string?)"W", (string?)null, false), Changes(plan));
        Assert.Contains(Gsid("G-Sales-Lead"), op.After.Select(a => a.Sid).ToHashSet());
        Assert.DoesNotContain(M.System, op.After.Select(a => a.Sid).ToHashSet()); // now inherited
    }

    [Fact]
    public void ClearRestoresTheDefault()
    {
        var path = $@"{OPS}\Sales-Lead"; // protected, G-Management W, G-Sales-Lead W, SYSTEM, Administrators
        var plan = Planner.Build(snap, Ch(Gsid("G-HR"), path, "R"), clearIn: [path]); // Clear takes precedence
        var op = Assert.Single(plan.AclOps);
        Assert.True(op.Cleared && (op.ProtectedBefore, op.ProtectedAfter) == (true, false));
        Assert.Empty(op.After);
        Assert.Equal(Set(("G-Management", path, "W", null, false), ("G-Sales-Lead", path, "W", null, false)), Changes(plan));
        // G-Management inherits W from Operations; G-Sales-Lead has only R| there (does not inherit)
        Assert.Equal(
            new HashSet<(string, string?)> { ("aadams", null) },
            plan.Impact.Where(i => i.Path == path).Select(i => (i.User, i.After)).ToHashSet());
    }

    [Fact]
    public void ClearRemovesAutomaticListEntriesAbove()
    {
        var plan = Planner.Build(snap, NoCells, clearIn: [$@"{OPS}\Service-QA"]);
        Assert.Contains(("G-Service-QA", $@"{OPS}\Service-QA", (string?)"W", (string?)null, false), Changes(plan));
        plan = Planner.Build(snap, NoCells, clearIn: [$@"{OPS}\Sales-QA"]);
        Assert.Contains(("G-Sales-QA", OPS, (string?)"R|", (string?)null, true), Changes(plan)); // nichts mehr darunter
    }

    [Fact]
    public void ClearDeepDeviationAndDenyEntries()
    {
        var deep = $@"{OPS}\Sales-Staff\2025\Offsite";
        var f = snap.Folders[deep]; // plus a deny entry
        snap.Folders[deep] = f with { Aces = [.. f.Aces, new Ace(Gsid("G-HR"), "DEMO\\G-HR", "group", 0x10000, Allow: false, Flags: 3)] };
        var op = Assert.Single(Planner.Build(snap, NoCells, clearIn: [deep]).AclOps);
        Assert.True(op.After.Count == 0 && !op.ProtectedAfter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope")]
    public void ClearNotOnRootOrUnknown(string path)
    {
        Assert.Throws<PlanError>(() => Planner.Build(snap, NoCells, clearIn: [path]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope")]
    public void InheritanceNotOnRootOrUnknownFolders(string path)
    {
        Assert.Throws<PlanError>(() => Planner.Build(snap, NoCells, Protect(path, true)));
    }

    [Fact]
    public void DeviationBelowTheMatrixDepthCanBeCleanedUp()
    {
        var deep = $@"{OPS}\Sales-Staff\2025\Offsite"; // level 4, entry for G-Interns
        var plan = Planner.Build(snap, Ch(Gsid("G-Interns"), deep, null), maxLevel: 3);
        Assert.Equal(Set(("G-Interns", deep, "W", null, false)), Changes(plan));
        Assert.NotEmpty(Planner.Build(snap, NoCells, Protect(deep, true), maxLevel: 3).AclOps); // inheritance works there too
    }

    public static TheoryData<string, string, string> InvalidChanges => new()
    {
        { M.System, "HR", "R" },
        { "S-1-5-21-0-0-0-1", "HR", "R" },
        { Demo.Gsid("G-Management"), "Nope", "R" },
        { Demo.Gsid("G-Management"), "HR", "M" },
    };

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public void InvalidChangesAreRefused(string sid, string path, string value)
    {
        Assert.Throws<PlanError>(() => Planner.Build(snap, Ch(sid, path, value)));
    }

    [Fact]
    public void ExtraColumnCanGetARight()
    {
        var p = new Principal("S-1-5-21-1-2-3-9999", "DEMO\\G-New", "group");
        var plan = Planner.Build(snap, Ch(p.Sid, "HR", "R"), extra: new Dictionary<string, Principal> { [p.Sid] = p });
        Assert.Contains(("G-New", "HR", (string?)null, (string?)"R", false), Changes(plan));
    }

    [Fact]
    public void NewFolderWithARight()
    {
        var plan = Planner.Build(
            snap,
            Ch(Gsid("G-HR"), @"Public\New\Sub", "W"),
            newFolders: new Dictionary<string, string> { [@"Public\New"] = "Public", [@"Public\New\Sub"] = @"Public\New" });
        Assert.Equal([@"Public\New", @"Public\New\Sub"], plan.CreateOps);
        var op = Assert.Single(plan.AclOps);
        Assert.True(op.NewFolder && op.Path == @"Public\New\Sub");
        Assert.Superset(new HashSet<string> { @"Public\New", @"Public\New\Sub" }, plan.Impact.Select(i => i.Path).ToHashSet());
    }

    [Theory]
    [InlineData("Public\\CON", "Public")]
    [InlineData("Public\\a.", "Public")]
    [InlineData("X\\Y", "X")]
    [InlineData("HR", "")]
    [InlineData(OPS + @"\Sales-Staff\2025\x", OPS + @"\Sales-Staff\2025")]
    public void BadNewFolders(string path, string parent)
    {
        var folders = new Dictionary<string, string> { [path] = parent };
        Assert.Throws<PlanError>(() => Planner.Build(snap, NoCells, newFolders: folders));
    }

    [Fact]
    public void NewTopLevelFolder()
    {
        var plan = Planner.Build(snap, NoCells, newFolders: new Dictionary<string, string> { ["Marketing"] = "" });
        Assert.Equal(["Marketing"], plan.CreateOps);
        Assert.Empty(plan.AclOps); // inherits from the root, nothing to write
    }

    /// <summary>Invalid characters, trailing dot or space, device names, existing names (also in another spelling).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*")]
    [InlineData("a?")]
    [InlineData("a\"")]
    [InlineData("a<")]
    [InlineData("a>")]
    [InlineData("a|")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("..")]
    [InlineData("Transfer")]
    [InlineData("transfer")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("CON .txt")]
    [InlineData("CONIN$")]
    [InlineData("conout$")]
    [InlineData("COM¹")]
    [InlineData("LPT²")]
    public void BadFolderNames(string name)
    {
        var folders = new Dictionary<string, string> { [$"Public\\{name}"] = "Public" };
        Assert.Throws<PlanError>(() => Planner.Build(snap, NoCells, newFolders: folders));
    }

    [Fact]
    public void NewFolderNamesHaveAtMost200Characters()
    {
        var name = new string('a', M.MaxFolderName);
        var plan = Planner.Build(snap, NoCells, newFolders: new Dictionary<string, string> { [$"Public\\{name}"] = "Public" });
        Assert.Equal([$"Public\\{name}"], plan.CreateOps);
        var e = Assert.Throws<PlanError>(() =>
            Planner.Build(snap, NoCells, newFolders: new Dictionary<string, string> { [$"Public\\{name}b"] = "Public" }));
        Assert.Contains("at most 200 characters", e.Message);
    }

    [Fact]
    public void TheRootGetsTheFullControlAccountsUnlessItInheritsThem()
    {
        // the root inherits from the drive: SYSTEM with full control from above, Administrators not at all
        var root = snap.Folders[""];
        snap.Folders[""] = root with
        {
            Protected = false,
            Aces =
            [
                .. root.Explicit.Where(a => !M.IsHidden(a.Sid, a.Name)),
                new Ace(M.System, @"NT AUTHORITY\SYSTEM", "wellknown", M.Full, Inherited: true, Flags: M.OiCi | M.InheritedAce),
            ],
        };
        var findings = Rights.Findings(snap).Where(f => f.Path == "").Select(f => f.Text).ToList();
        Assert.Contains("Administrators without full control here", findings);
        Assert.DoesNotContain(findings, t => t.StartsWith("SYSTEM"));
        var op = Planner.Build(snap, Ch(Gsid("G-HR"), "", "R|")).AclOps.Single(o => o.Path == "");
        Assert.Contains(op.After, a => a.Sid == M.Admins && a.Mask == M.Full && a.Flags == M.OiCi);
        Assert.DoesNotContain(op.After, a => a.Sid == M.System); // inherited already
    }

    [Fact]
    public void ImpactListsUsers()
    {
        var plan = Planner.Build(snap, Ch(Gsid("G-HR"), @"Programs\ERP", "W"));
        Assert.Equal(
            new HashSet<(string, string, string?, string?)> { ("llewis", @"Programs\ERP", null, "W") },
            plan.Impact.Select(i => (i.User, i.Path, i.Before, i.After)).ToHashSet());
    }
}
