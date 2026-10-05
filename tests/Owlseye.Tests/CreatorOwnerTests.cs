// Creator Owner: shown and set in the folder panel (subfolders and files only), a finding when it has full control;
// the folder panel also lists the entries of the hidden accounts.

using Owlseye.Providers;
using Owlseye.Ui;

namespace Owlseye.Tests;

public sealed class CreatorOwnerTests : TestBase
{
    const string Finding = "Creator Owner has full control: users can change permissions on what they create here";

    (State St, Session S) Client()
    {
        var st = new State(new Config { Provider = "demo", Audit = Path.Combine(Tmp, "log.jsonl") }, new DemoProvider());
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    [Fact]
    public void CreatorOwnerIsSetInTheFolderPanelWithPreviewLogAndUndo()
    {
        var (st, s) = Client();
        Assert.Null(s.FolderPanel("Public").CreatorOwner);
        Assert.Throws<UserError>(() => s.SetCreatorOwner("Public", "R"));

        s.SetCreatorOwner("Public", "F");
        Assert.True(s.FolderPanel("Public").CreatorOwnerPending);
        var plan = s.Preview().Plan!;
        var op = plan.AclOps.Single(o => o.Path == "Public");
        Assert.Contains(op.After, a => a.Sid == M.CreatorOwner && a.Mask == M.Full && a.Flags == M.CreatorOwnerFlags);
        Assert.Equal(((string?)null, (string?)"F"), op.Changes.Where(c => c.Sid == M.CreatorOwner).Select(c => (c.Before, c.After)).Single());
        Assert.Empty(plan.Impact); // nobody gets anything on what exists

        s.Apply("", s.Preview().Phash);
        Assert.Equal("F", s.FolderPanel("Public").CreatorOwner);
        Assert.Contains(st.Findings, f => f.Path == "Public" && f.Text == Finding);
        Assert.Empty(st.Drift); // no deviation either

        var id = s.AuditPage().Entries.First(e => e.Arr("acl_ops") is { Count: > 0 }).Str("id")!;
        s.Undo(id);
        Assert.Equal(1, st.PendingCount);
        s.Apply("", s.Preview().Phash);
        Assert.Null(s.FolderPanel("Public").CreatorOwner);
        Assert.DoesNotContain(st.Findings, f => f.Text == Finding);
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
        Assert.Contains(s.FolderPanel("HR").Hidden, h => h.Name == "SYSTEM" && h.Right == "F" && !h.Inherited);
    }
}
