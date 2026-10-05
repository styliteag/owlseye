// Integration tests against real NTFS ACLs and the local user database. They need no administrator rights: the
// test share lives in a temp folder the current user owns, and every protected folder keeps an entry for that user.

using System.Diagnostics;
using System.Security.Principal;
using Owlseye.Providers;
using Owlseye.Ui;
using Owlseye.Windows;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Owlseye.Windows.Tests;

public sealed class Win32Tests : IDisposable
{
    const string Users = "S-1-5-32-545";
    static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

    readonly string tmp;
    readonly string share;

    public Win32Tests()
    {
        tmp = Path.Combine(Path.GetTempPath(), "owlseye-win", Guid.NewGuid().ToString("N"));
        share = Path.Combine(tmp, "share");
        Directory.CreateDirectory(share);
        Paths.DataDirOverride = Path.Combine(tmp, "data");
        foreach (var d in new[] { "A", @"A\A1", "B", "C" }) Directory.CreateDirectory(Path.Combine(share, d));
        var fs = Fs();
        // root: protected, like a file server share root; the current user keeps full control so the test can go on
        fs.WriteDacl("", true, [Full(Me), Full(M.System), Full(M.Admins), new RawAce(0, 0, M.Read, Users)]);
        fs.WriteDacl("A", false, [new RawAce(0, M.OiCi, M.Read, Users)]);
        fs.WriteDacl(@"A\A1", false, []);
        fs.WriteDacl("B", true, [Full(Me), Full(M.System), Full(M.Admins), new RawAce(0, M.OiCi, M.Write, Users)]);
        fs.WriteDacl("C", false, []);
    }

    public void Dispose()
    {
        Paths.DataDirOverride = null;
        try
        {
            foreach (var j in Directory.EnumerateDirectories(share, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
                Directory.Delete(j);
            Directory.Delete(tmp, true);
        }
        catch (Exception)
        {
        }
    }

    static RawAce Full(string sid) => new(0, M.OiCi, M.Full, sid);

    LocalFs Fs()
    {
        var fs = new LocalFs();
        fs.ToUnc(share);
        return fs;
    }

    (State St, Session S) Client()
    {
        var cfg = new Config { Provider = "local", Share = share };
        var st = new State(cfg, new LocalProvider(share, 20)) { OpenShare = p => new LocalProvider(p, 20) };
        st.Load();
        Assert.True(st.Ready, st.LoadError);
        return (st, new Session(st));
    }

    static Outcome Apply(Session s, string reason = "test") => s.Apply(reason, s.Preview().Phash);

    [Fact]
    public void DaclRoundtripAndInheritedEntries()
    {
        var fs = Fs();
        var (prot, aces) = fs.ReadDacl("");
        Assert.True(prot);
        Assert.Contains(aces, a => a.Sid == Me && a.Mask == M.Full && a.Flags == M.OiCi);
        Assert.Contains(aces, a => a.Sid == Users && a.Mask == M.Read && a.Flags == 0);
        var (protA, acesA) = fs.ReadDacl("A");
        Assert.False(protA);
        Assert.Contains(acesA, a => a.Sid == Users && a.Flags == M.OiCi); // own entry
        Assert.Contains(acesA, a => a.Sid == Me && (a.Flags & M.InheritedAce) != 0); // inherited from the root
        Assert.DoesNotContain(acesA, a => a.Sid == Users && a.Mask == M.Read && (a.Flags & M.InheritedAce) != 0 && (a.Flags & M.OiCi) == 0);
    }

    [Fact]
    public void ScanReadsFoldersAccountsAndFindings()
    {
        var p = new LocalProvider(share, 20);
        var snap = p.Scan();
        Assert.Equal(new[] { "", "A", @"A\A1", "B", "C" }, snap.Folders.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("user", snap.Principals[Me].Kind);
        Assert.Equal("wellknown", snap.Principals[Users].Kind);
        var cells = Rights.Matrix(snap);
        Assert.Equal("R", cells[(Users, "A")].Direct);
        Assert.Equal("R", cells[(Users, @"A\A1")].Effective);
        Assert.Equal("W", cells[(Users, "B")].Direct);
        Assert.Contains(Rights.Findings(snap), f => f.Text.StartsWith("Direct user entry for"));
    }

    [Fact]
    public void ApplyWritesTheAclAndLeavesNoDrift()
    {
        var (st, s) = Client();
        s.SetCell(Users, "C", "W");
        var o = Apply(s);
        Assert.False(o.Error, o.Message);
        Assert.Contains("changes applied", o.Message);
        var (prot, aces) = Fs().ReadDacl("C");
        Assert.False(prot);
        Assert.Contains(aces, a => a.Sid == Users && a.Mask == M.Write && a.Flags == M.OiCi);
        Assert.Empty(st.Drift);
        Assert.Equal("W", st.Cells[(Users, "C")].Direct);
    }

    [Fact]
    public void BreakInheritanceAndUndo()
    {
        var (st, s) = Client();
        s.SetInheritance("A", inherit: false);
        var o = Apply(s);
        Assert.False(o.Error, o.Message);
        var (prot, aces) = Fs().ReadDacl("A");
        Assert.True(prot);
        Assert.Contains(aces, a => a.Sid == Me && a.Mask == M.Full && (a.Flags & M.InheritedAce) == 0); // copied, nobody loses access
        Assert.Empty(st.Drift);
        var id = st.Audit.Entries().First(e => e.Str("kind") == "change").Str("id")!;
        Assert.Equal("/preview", s.Undo(id).Url);
        o = Apply(s, "undo");
        Assert.False(o.Error, o.Message);
        Assert.False(Fs().ReadDacl("A").Protected);
        Assert.Empty(st.Drift);
    }

    [Fact]
    public void NewFolderIsCreatedWithItsRight()
    {
        var (st, s) = Client();
        s.NewFolder("C", "New folder ä");
        s.SetCell(Users, @"C\New folder ä", "W");
        var o = Apply(s);
        Assert.False(o.Error, o.Message);
        Assert.True(Directory.Exists(Path.Combine(share, "C", "New folder ä")));
        Assert.Contains(Fs().ReadDacl(@"C\New folder ä").Aces, a => a.Sid == Users && a.Mask == M.Write && (a.Flags & M.InheritedAce) == 0);
        Assert.Equal("W", st.Cells[(Users, @"C\New folder ä")].Direct);
    }

    static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void JunctionBelowAFolderBlocksWritesAbove()
    {
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(share, "C", "link"), outside);
        var (st, s) = Client();
        Assert.DoesNotContain(@"C\link", st.Snap.Folders.Keys); // not scanned
        s.SetCell(Users, "C", "R");
        var o = Apply(s);
        Assert.True(o.Error);
        Assert.Contains("junction or link below it", o.Message);
        Assert.DoesNotContain(Fs().ReadDacl("C").Aces, a => a.Sid == Users && (a.Flags & M.InheritedAce) == 0);
    }

    [Fact]
    public void WritingThroughAJunctionIsRefused()
    {
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(share, "C", "link"), outside);
        var fs = Fs();
        var e = Assert.Throws<UnauthorizedAccessException>(() => fs.WriteDacl(@"C\link", false, []));
        Assert.Contains("junction or link", e.Message);
    }

    /// <summary>The open question from the ADR, answered for local NTFS: SetSecurityInfo's propagation updates the junction
    /// object itself but does not follow it into the target. (Over SMB still to be verified in the lab, so AdProvider keeps
    /// refusing writes above a link.)</summary>
    [Fact]
    public void PropagationDoesNotFollowAJunctionOnLocalNtfs()
    {
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "X1"));
        Junction(Path.Combine(share, "C", "link"), outside);
        var fs = Fs();
        fs.WriteDacl("C", false, [new RawAce(0, M.OiCi, M.Write, Users)]); // Win32Fs directly, past AdProvider's refusal
        var target = new LocalFs();
        target.ToUnc(outside);
        Assert.DoesNotContain(target.ReadDacl("").Aces, a => a.Sid == Users && a.Mask == M.Write);
        Assert.DoesNotContain(target.ReadDacl("X1").Aces, a => a.Sid == Users && a.Mask == M.Write);
        Assert.Contains(fs.ReadDacl(@"A").Aces, a => a.Sid == Users); // unrelated sibling untouched, still readable
    }

    [Fact]
    public void ParallelScanGivesTheSameSnapshotAsTheSequentialOne()
    {
        var fs = Fs();
        var rnd = new Random(7);
        for (var a = 0; a < 6; a++)
            for (var b = 0; b < 5; b++)
                for (var c = 0; c < 4; c++)
                {
                    var rel = $@"B\Bereich{a}\Team {b}\Projekt-{c}";
                    Directory.CreateDirectory(Path.Combine(share, rel));
                    if (rnd.Next(3) == 0) fs.WriteDacl(rel, false, [new RawAce(0, rnd.Next(2) == 0 ? 0 : M.OiCi, rnd.Next(2) == 0 ? M.Read : M.Write, Users)]);
                }
        fs.WriteDacl(@"B\Bereich2", true, [Full(Me), new RawAce(0, M.OiCi, M.Read, Users)]);
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(share, "B", "Bereich3", "link"), outside);
        Directory.CreateDirectory(Path.Combine(share, "B", "zu"));
        // ACL readable and writable (plus SYNCHRONIZE and read attributes, which CreateFile always asks for), contents not listable
        fs.WriteDacl(@"B\zu", true, [new RawAce(0, 0, 0x170080, Me)]);
        var seq = new AdProvider(new LocalFs(), new LocalDirectory(), share, 20, parallelism: 1).Scan();
        var par = new AdProvider(new LocalFs(), new LocalDirectory(), share, 20, parallelism: 6).Scan();
        Assert.True(seq.Folders.Count > 120);
        Assert.Equal(seq.Folders.Keys, par.Folders.Keys); // same order too
        foreach (var (k, f) in seq.Folders)
        {
            var g = par.Folders[k];
            Assert.Equal((f.Level, f.Protected, f.OtherAces, f.Error), (g.Level, g.Protected, g.OtherAces, g.Error));
            Assert.Equal(f.Aces, g.Aces);
        }
        Assert.StartsWith("Contents not readable", seq.Folders[@"B\zu"].Error);
        Assert.DoesNotContain(@"B\Bereich3\link", par.Folders.Keys);
        Assert.Equal(seq.Principals.Keys.Order(), par.Principals.Keys.Order());
        Assert.Equal(Rights.Findings(seq), Rights.Findings(par));
        fs.WriteDacl(@"B\zu", true, [Full(Me)]); // so the test folder can be deleted
    }

    static void Swap(string a, string b)
    {
        var tmp = a + ".swap";
        Directory.Move(a, tmp);
        Directory.Move(b, a);
        Directory.Move(tmp, b);
    }

    /// <summary>Security review, finding 4: a user who may rename folders swaps the folder owlseye is about to write with
    /// another one between the conflict check and the write. The write checks on the pinned handle and refuses.</summary>
    [Fact]
    public void FolderSwappedInByARenameIsNotWritten()
    {
        Directory.CreateDirectory(Path.Combine(share, "C", "Scratch"));
        Directory.CreateDirectory(Path.Combine(share, "C", "Secret"));
        var fs = Fs();
        fs.WriteDacl(@"C\Secret", true, [Full(Me), Full(M.Admins)]);
        var scratch = new AdProvider(new LocalFs(), new LocalDirectory(), share, 20).Scan().Folders[@"C\Scratch"];
        Swap(Path.Combine(share, "C", "Scratch"), Path.Combine(share, "C", "Secret"));
        var p = new LocalProvider(share, 20);
        var e = Assert.Throws<IOException>(() => p.SetFolderAcl(@"C\Scratch", false, [new Ace(Users, "Users", "wellknown", M.Write, Flags: M.OiCi)], scratch));
        Assert.Contains("no longer the folder that was scanned", e.Message);
        Assert.DoesNotContain(fs.ReadDacl(@"C\Scratch").Aces, a => a.Sid == Users && a.Mask == M.Write); // the former Secret is untouched
    }

    /// <summary>Even two folders with identical ACLs are told apart by their identity.</summary>
    [Fact]
    public void SwapOfFoldersWithTheSameAclIsNoticedToo()
    {
        Directory.CreateDirectory(Path.Combine(share, "C", "One"));
        Directory.CreateDirectory(Path.Combine(share, "C", "Two"));
        var snap = new AdProvider(new LocalFs(), new LocalDirectory(), share, 20).Scan();
        Assert.NotEqual("", snap.Folders[@"C\One"].Id);
        Assert.NotEqual(snap.Folders[@"C\One"].Id, snap.Folders[@"C\Two"].Id);
        Swap(Path.Combine(share, "C", "One"), Path.Combine(share, "C", "Two"));
        var p = new LocalProvider(share, 20);
        Assert.Throws<IOException>(() => p.SetFolderAcl(@"C\One", false, [new Ace(Users, "Users", "wellknown", M.Write, Flags: M.OiCi)], snap.Folders[@"C\One"]));
        // without a swap the same write goes through
        p.SetFolderAcl(@"C\Two", false, [new Ace(Users, "Users", "wellknown", M.Write, Flags: M.OiCi)], new AdProvider(new LocalFs(), new LocalDirectory(), share, 20).Scan().Folders[@"C\Two"]);
    }

    /// <summary>An ACL changed between the conflict check and the write (here: right before the write) is not overwritten.</summary>
    [Fact]
    public void AclChangedRightBeforeTheWriteIsNotOverwritten()
    {
        var snapC = new AdProvider(new LocalFs(), new LocalDirectory(), share, 20).Scan().Folders["C"];
        Fs().WriteDacl("C", false, [new RawAce(0, M.OiCi, M.Read, M.Admins)]); // someone in Explorer
        var p = new LocalProvider(share, 20);
        var e = Assert.Throws<IOException>(() => p.SetFolderAcl("C", false, [new Ace(Users, "Users", "wellknown", M.Write, Flags: M.OiCi)], snapC));
        Assert.Contains("changed outside owlseye", e.Message);
    }

    /// <summary>Security review, finding 11: the scan reads each folder through a handle that does not follow a junction.</summary>
    [Fact]
    public void ScanReadsTheFolderItselfNotAJunctionTarget()
    {
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(share, "C", "link"), outside);
        var e = Assert.Throws<UnauthorizedAccessException>(() => Fs().ReadFolder(@"C\link"));
        Assert.Contains("junction or link", e.Message);
    }

    // --- security review, finding 1: hardening of an elevated instance ---

    [Fact]
    public void WebView2OverridesAreRemovedFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--remote-debugging-port=9999");
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", @"C:\evil");
        var removed = Hardening.ClearWebView2Overrides();
        Assert.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", removed);
        Assert.Contains("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", removed);
        Assert.Null(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"));
        Assert.Null(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"));
    }

    static unsafe string? MandatoryLabel(string dir)
    {
        nint sacl = 0, sd = 0;
        var rc = GetNamedSecurityInfoW(dir, 1, 0x10, null, null, null, &sacl, &sd);
        Assert.Equal(0, rc);
        try
        {
            var len = GetSecurityDescriptorLength(sd);
            var bytes = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(sd, bytes, 0, (int)len);
            var rsd = new System.Security.AccessControl.RawSecurityDescriptor(bytes, 0);
            foreach (System.Security.AccessControl.GenericAce ace in rsd.SystemAcl ?? new System.Security.AccessControl.RawAcl(2, 0))
                if (ace is System.Security.AccessControl.CustomAce c && (int)c.AceType == 0x11) // SYSTEM_MANDATORY_LABEL_ACE
                    return new SecurityIdentifier(c.GetOpaque()!, 4).Value; // mask, then the label SID
            return null;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern unsafe int GetNamedSecurityInfoW(string name, int type, uint info, nint* owner, nint* group, nint* dacl, nint* sacl, nint* sd);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern uint GetSecurityDescriptorLength(nint sd);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern nint LocalFree(nint mem);

    /// <summary>The elevated profile is created fresh and carries no integrity label: WebView2 runs its browser process
    /// de-elevated, and a high label (as in the first fix of finding 1) locked it out of its data directory.</summary>
    [Fact]
    public void FreshFolderReplacesWhatWasThereAndHasNoIntegrityLabel()
    {
        var dir = Path.Combine(tmp, "profile");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "planted.txt"), "x");
        Hardening.FreshFolder(dir);
        Assert.False(File.Exists(Path.Combine(dir, "planted.txt")));
        Assert.True(Directory.Exists(dir));
        Assert.Null(MandatoryLabel(dir));
        File.WriteAllText(Path.Combine(dir, "written-at-medium.txt"), "x"); // this test runs unelevated
    }

    // --- security review, finding 2: an elevated owlseye loads libraries from its own folder ---

    [Fact]
    public void ProgramFolderIsTrustedOnlyIfAdministratorsAloneCanChangeIt()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        System.Security.AccessControl.DirectorySecurity Like(params (SecurityIdentifier Sid, System.Security.AccessControl.FileSystemRights Rights)[] rules)
        {
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetOwner(admins);
            foreach (var (sid, rights) in rules)
                sec.AddAccessRule(new(sid, rights, System.Security.AccessControl.AccessControlType.Allow));
            return sec;
        }
        var full = System.Security.AccessControl.FileSystemRights.FullControl;
        var rx = System.Security.AccessControl.FileSystemRights.ReadAndExecute;
        // as C:\Program Files: administrators and SYSTEM change it, users read and run
        Assert.Null(Hardening.UntrustedWriter(Like((admins, full), (new SecurityIdentifier(M.System), full), (users, rx))));
        // users may add files (as in C:\ itself) or change everything
        Assert.NotNull(Hardening.UntrustedWriter(Like((admins, full), (users, System.Security.AccessControl.FileSystemRights.CreateFiles))));
        Assert.NotNull(Hardening.UntrustedWriter(Like((admins, full), (users, System.Security.AccessControl.FileSystemRights.Modify))));
        // an owner who is not an administrator can always change the ACL
        var owned = Like((admins, full));
        owned.SetOwner(new SecurityIdentifier(Me));
        Assert.StartsWith("the owner ", Hardening.UntrustedWriter(owned));
        // a folder the user made (here: the test folder) is not trusted, Program Files is
        Assert.NotNull(Hardening.WritableByOthers(tmp));
        Assert.Null(Hardening.WritableByOthers(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    [Fact]
    public void MachineDomainIsNullOnAWorkgroupOrANameOnAMember()
    {
        var domain = Machine.Domain(); // the CI runners and dev machines here are workgroup machines; a member gives a name
        Assert.True(domain is null || domain.Length > 0);
        if (domain is not null) Assert.DoesNotContain('\\', domain);
    }

    [Fact]
    public void MkdirRefusesAnExistingFolder()
    {
        var fs = Fs();
        fs.Mkdir(@"C\X");
        Assert.True(Directory.Exists(Path.Combine(share, "C", "X")));
        Assert.Throws<IOException>(() => fs.Mkdir(@"C\X"));
    }

    [Fact]
    public void LookupSidKnowsWellKnownAndRefusesUnknown()
    {
        var fs = Fs();
        var (name, _, use) = fs.LookupSid(M.System);
        Assert.Equal("SYSTEM", name);
        Assert.Equal(5, use);
        Assert.Equal(1, fs.LookupSid(Me).Use);
        Assert.Throws<KeyNotFoundException>(() => fs.LookupSid("S-1-5-21-1-2-3-4711"));
    }

    [Fact]
    public void LocalDirectoryListsThisUser()
    {
        var d = new LocalDirectory();
        var rows = d.Query(d.RootDn(), "(objectClass=user)", ["distinguishedName", "sAMAccountName", "objectSid"]);
        bool IsMe(Row r) => string.Equals(r.S("sAMAccountName"), Environment.UserName, StringComparison.OrdinalIgnoreCase);
        // Built-in accounts (Administrator, Guest, …) are not listed on purpose. The GitHub runners run the tests as
        // runneradmin, which is the renamed built-in Administrator (RID 500).
        if (Me.EndsWith("-500") || Me.EndsWith("-501") || Me.EndsWith("-503") || Me.EndsWith("-504"))
        {
            Assert.DoesNotContain(rows, IsMe);
            return;
        }
        var mine = rows.Single(IsMe);
        Assert.Equal(Me, AdProvider.SidFromBytes(mine.Bytes("objectSid")!));
        Assert.EndsWith($"OU=Users,DC={Environment.MachineName}", mine.S("distinguishedName"));
    }

    [Fact]
    public void DriveLetterThatIsNotANetworkDriveIsExplained()
    {
        var e = Assert.Throws<IOException>(() => new Win32Fs().ToUnc(@"C:\"));
        Assert.Contains("not a network drive", e.Message);
    }

    [Fact]
    public void FoldersBeyond260CharactersAreReadAndWritten()
    {
        var parts = Enumerable.Range(1, 8).Select(i => $"Ebene{i}-" + new string('x', 34)).ToList();
        var rel = "C\\" + string.Join("\\", parts);
        Directory.CreateDirectory(Path.Combine(share, rel)); // .NET handles long paths itself
        Assert.True(Path.Combine(share, rel).Length > 300);
        var (st, s) = Client();
        Assert.Contains(rel, st.Snap.Folders.Keys);
        s.SetCell(Users, rel, "W");
        var o = Apply(s);
        Assert.False(o.Error, o.Message);
        Assert.Contains(Fs().ReadDacl(rel).Aces, a => a.Sid == Users && a.Mask == M.Write && (a.Flags & M.InheritedAce) == 0);
        Assert.Equal("W", st.Cells[(Users, rel)].Direct);
    }

    [Fact]
    public void OutsideChangeIsAConflict()
    {
        var (st, s) = Client();
        s.SetCell(Users, "C", "W");
        var phash = s.Preview().Phash;
        Fs().WriteDacl("C", false, [new RawAce(0, M.OiCi, M.Read, M.Admins)]); // someone changes it in Explorer
        var o = s.Apply("x", phash);
        Assert.True(o.Error);
        Assert.Contains("was changed outside owlseye", o.Message);
    }
}
