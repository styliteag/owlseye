// Owner entries: Creator Owner and Owner Rights are no columns but shown and set in the folder panel; Creator Owner
// with full control is a finding. The folder panel also lists the entries of the hidden accounts.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class OwnerEntriesTests : TestBase
{
    const string Finding = "Creator Owner has full control: whoever creates something here gets personal full control of it, "
        + "outside the groups and the matrix, and keeps it after leaving them";

    (State St, Session S) Client()
    {
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    static OwnerEntryView Entry(Session s, string path, string sid) => s.FolderPanel(path).OwnerEntries.Single(e => e.Sid == sid);

    static string LastApply(Session s) => s.AuditPage().Entries.First(e => e.Arr("acl_ops") is { Count: > 0 }).Str("id")!;

    [Fact]
    public void CreatorOwnerIsSetInTheFolderPanelWithPreviewLogAndUndo()
    {
        var (st, s) = Client();
        Assert.Null(Entry(s, "Public", M.CreatorOwner).Value);
        Assert.Throws<UserError>(() => s.SetOwnerEntry(M.CreatorOwner, "Public", "R"));

        s.SetOwnerEntry(M.CreatorOwner, "Public", "F");
        Assert.True(Entry(s, "Public", M.CreatorOwner).Pending);
        var plan = s.Preview().Plan!;
        var op = plan.AclOps.Single(o => o.Path == "Public");
        Assert.Contains(op.After, a => a.Sid == M.CreatorOwner && a.Mask == M.Full && a.Flags == M.CreatorOwnerFlags);
        Assert.Equal(((string?)null, (string?)"F"), op.Changes.Where(c => c.Sid == M.CreatorOwner).Select(c => (c.Before, c.After)).Single());
        Assert.Empty(plan.Impact); // owners of files are not read: Windows gives them the right, owlseye cannot list them

        s.Apply("", s.Preview().Phash);
        Assert.Equal("F", Entry(s, "Public", M.CreatorOwner).Value);
        Assert.Contains(st.Findings, f => f.Path == "Public" && f.Text == Finding);
        Assert.Empty(st.Drift); // no deviation either

        s.Undo(LastApply(s));
        Assert.Equal(1, st.PendingCount);
        s.Apply("", s.Preview().Phash);
        Assert.Null(Entry(s, "Public", M.CreatorOwner).Value);
        Assert.DoesNotContain(st.Findings, f => f.Text == Finding);
    }

    [Fact]
    public void OwnerRightsStopOwnersFromChangingPermissions()
    {
        var (st, s) = Client();
        Assert.Throws<UserError>(() => s.SetOwnerEntry(M.OwnerRights, "Public", "F")); // that would allow it again

        s.SetOwnerEntry(M.OwnerRights, "Public", "V");
        var op = s.Preview().Plan!.AclOps.Single(o => o.Path == "Public");
        Assert.Contains(op.After, a => a.Sid == M.OwnerRights && a.Mask == (M.ReadControl | M.Synchronize) && a.Flags == M.OiCi);
        var change = op.Changes.Single(c => c.Sid == M.OwnerRights);
        Assert.Equal("no personal rights", Labels.Value(change.Sid, change.After));

        s.Apply("", s.Preview().Phash);
        Assert.Equal("V", Entry(s, "Public", M.OwnerRights).Value);
        Assert.Empty(st.Drift);

        s.SetOwnerEntry(M.OwnerRights, "Public", "W");
        s.Apply("", s.Preview().Phash);
        Assert.Equal("W", Entry(s, "Public", M.OwnerRights).Value);
        s.SetOwnerEntry(M.OwnerRights, "Public", null);
        s.Apply("", s.Preview().Phash);
        Assert.Null(Entry(s, "Public", M.OwnerRights).Value);
    }

    [Fact]
    public void OwnerRightsThatAllowChangingPermissionsAreSpecial()
    {
        var f = new Folder("X", 1, Aces: [new Ace(M.OwnerRights, "OWNER RIGHTS", "wellknown", M.Modify | M.WriteDac, Flags: M.OiCi)]);
        Assert.Equal("*", Rights.OwnerEntryOf(f, M.OwnerRights));
        Assert.Equal("special rights", Labels.OwnerEntryText(M.OwnerRights, "*"));
    }

    [Fact]
    public void CreatorOwnerWithFullControlFromTheDriveIsAFindingOnTheRoot()
    {
        var snap = new DemoProvider().Scan();
        var root = snap.Folders[""];
        const uint genericAll = 0x10000000; // as Windows writes it
        snap.Folders[""] = root with
        {
            Protected = false,
            Aces = [.. root.Aces, new Ace(M.CreatorOwner, @"\CREATOR OWNER", "wellknown", genericAll, Inherited: true, Flags: M.CreatorOwnerFlags | M.InheritedAce)],
        };
        Assert.Equal("F", Rights.CreatorOwnerOf(snap.Folders[""], inherited: true));
        Assert.Contains(Rights.Findings(snap), f => f.Path == "" && f.Text == Finding);
    }

    [Fact]
    public void TheFolderPanelListsTheHiddenAccounts()
    {
        var (_, s) = Client();
        var hidden = s.FolderPanel("HR").Hidden;
        Assert.Contains(hidden, h => h.Name == "SYSTEM" && h.Right == "F" && !h.Inherited);
        Assert.DoesNotContain(hidden, h => h.Name.Contains("OWNER", StringComparison.OrdinalIgnoreCase)); // own lines
    }
}
