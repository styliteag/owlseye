// Windows file system adapter: runs in the context of the logged-in admin.
//
// ACLs via Win32 (GetNamedSecurityInfo / SetSecurityInfo on a handle), DACLs as binary security descriptors through
// System.Security.AccessControl (RawSecurityDescriptor keeps ACE types it does not know as opaque ACEs instead of
// failing). Writes go through a chain of handles so no junction can be slid in between check and write.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Owlseye.Providers;

namespace Owlseye.Windows;

public class Win32Fs : IFilesystem
{
    public string Share { get; protected set; } = "";

    public HashSet<string> Links { get; } = [];

    internal static IOException Fail(string func, int? error = null)
    {
        var code = error ?? Marshal.GetLastPInvokeError();
        return new IOException($"{func}: {new Win32Exception(code).Message}", code);
    }

    /// <summary>Drive letter -> UNC. UNC stays UNC. A mapped network drive becomes its share; a folder on this
    /// computer's own disk (the file server's E:\Shares\Data) its administrative share, \\HOST\E$\Shares\Data.</summary>
    public virtual string ToUnc(string path)
    {
        var p = path.TrimEnd('\\', '/');
        if (p == "") p = path;
        if (p.StartsWith(@"\\"))
        {
            Share = p;
            return p;
        }
        if (Regex.IsMatch(p, "^[A-Za-z]:"))
        {
            if (UniversalName(p + "\\") is { } unc)
            {
                Share = unc.TrimEnd('\\');
                return Share;
            }
            if (new DriveInfo(p[..1]).DriveType == DriveType.Fixed)
            {
                var admin = AdminShare(p, Environment.MachineName);
                if (!Directory.Exists(admin))
                    throw new IOException(
                        $"{p} is a folder on this computer's own disk, not a share. owlseye opens it as {admin} (the "
                        + "administrative share), which it cannot reach: the administrative share may be switched off. "
                        + "Share the folder and open it by its UNC path.");
                Share = admin;
                return Share;
            }
            throw new IOException(
                $"{p} is not a network drive in this session. Is owlseye running elevated? "
                + "Then it cannot see normally mapped drives; start it without 'Run as administrator' "
                + "or use the UNC path.");
        }
        throw new ArgumentException($"Neither UNC nor a drive letter: {path}");
    }

    /// <summary>A folder on a computer's own disk as its administrative share: E:\Shares\Data -> \\HOST\E$\Shares\Data.</summary>
    public static string AdminShare(string localPath, string host)
    {
        var full = Path.GetFullPath(localPath.Length == 2 ? localPath + "\\" : localPath).TrimEnd('\\');
        return $@"\\{host}\{char.ToUpperInvariant(full[0])}${full[2..]}";
    }

    static unsafe string? UniversalName(string local)
    {
        uint size = 1024;
        while (true)
        {
            var buf = new byte[size];
            fixed (byte* b = buf)
            {
                var rc = Native.WNetGetUniversalName(local, Native.UniversalNameInfoLevel, b, ref size);
                if (rc == Native.ErrorMoreData) continue;
                if (rc != 0) return null;
                var p = *(nint*)b; // UNIVERSAL_NAME_INFO { LPWSTR lpUniversalName }
                return Marshal.PtrToStringUni(p);
            }
        }
    }

    protected string Unc(string rel) => rel == "" ? Share : Share.EndsWith('\\') ? Share + rel : Share + "\\" + rel;

    /// <summary>The path for Win32 and file system calls: \\?\ prefixed, so folders deeper than 260 characters work
    /// (common on file servers). Such paths are not normalized, which is fine: names Win32 would rewrite (trailing dots
    /// or spaces) are refused before they get here (M.BadComponent).</summary>
    protected string Long(string rel)
    {
        var p = Unc(rel);
        if (p.StartsWith(@"\\?\")) return p;
        return p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    }

    public unsafe (bool Protected, List<RawAce> Aces) ReadDacl(string rel)
    {
        nint dacl = 0, sd = 0;
        var rc = Native.GetNamedSecurityInfo(Long(rel), Native.SeFileObject, Native.DaclSecurityInformation, null, null, &dacl, null, &sd);
        if (rc != 0) throw Fail("GetNamedSecurityInfo", rc);
        return Parse(sd);
    }

    /// <summary>Takes ownership of `sd` (LocalFree).</summary>
    static (bool Protected, List<RawAce> Aces) Parse(nint sd)
    {
        try
        {
            var len = Native.GetSecurityDescriptorLength(sd);
            var bytes = new byte[len];
            Marshal.Copy(sd, bytes, 0, (int)len);
            var rsd = new RawSecurityDescriptor(bytes, 0);
            var prot = rsd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected);
            if (rsd.DiscretionaryAcl is null) // NULL DACL = Everyone full control; show it as what it is instead of "no entries"
                return (prot, [new RawAce(0, 0x3, 0x1F01FF, "S-1-1-0")]);
            var aces = new List<RawAce>();
            foreach (GenericAce ace in rsd.DiscretionaryAcl)
            {
                var type = (int)ace.AceType;
                if (ace is CommonAce { IsCallback: false } c && c.AceQualifier is AceQualifier.AccessAllowed or AceQualifier.AccessDenied)
                    aces.Add(new RawAce(c.AceQualifier == AceQualifier.AccessAllowed ? 0 : 1, (int)c.AceFlags, unchecked((uint)c.AccessMask),
                        c.SecurityIdentifier.Value));
                else if (ace is KnownAce k)
                    aces.Add(new RawAce(type is 0 or 1 ? 100 + type : type, (int)k.AceFlags, unchecked((uint)k.AccessMask), k.SecurityIdentifier.Value));
                else
                    aces.Add(new RawAce(type is 0 or 1 ? 100 + type : type, (int)ace.AceFlags, 0, ""));
            }
            return (prot, aces);
        }
        finally
        {
            Native.LocalFree(sd);
        }
    }

    static unsafe (bool Protected, List<RawAce> Aces) DaclOf(SafeFileHandle h)
    {
        nint dacl = 0, sd = 0;
        var rc = Native.GetSecurityInfo(h, Native.SeFileObject, Native.DaclSecurityInformation, null, null, &dacl, null, &sd);
        if (rc != 0) throw Fail("GetSecurityInfo", rc);
        return Parse(sd);
    }

    static string IdOf(Native.ByHandleFileInformation info) =>
        $"{info.VolumeSerialNumber:x8}-{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";

    /// <summary>For the scan: the DACL read through a handle on the folder itself, with its identity. A folder that was
    /// replaced by a junction after its parent was listed is refused instead of read through. Where the admin may read
    /// the ACL but not open a handle (CreateFile always asks for SYNCHRONIZE and read attributes), by path without identity.</summary>
    public (bool Protected, List<RawAce> Aces, string Id) ReadFolder(string rel)
    {
        var flags = Native.FileFlagBackupSemantics | (rel != "" ? Native.FileFlagOpenReparsePoint : 0);
        using var h = Native.CreateFile(Long(rel), Native.ReadControl, Native.FileShareRead | Native.FileShareWrite | Native.FileShareDelete,
            0, Native.OpenExisting, flags, 0);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != Native.ErrorAccessDenied) throw Fail("CreateFile", err);
            var (p, a) = ReadDacl(rel);
            return (p, a, "");
        }
        if (!Native.GetFileInformationByHandle(h, out var info)) throw Fail("GetFileInformationByHandle");
        if (rel != "" && (info.FileAttributes & Native.FileAttributeReparsePoint) != 0)
            throw new UnauthorizedAccessException($"{rel} is a junction or link, not a folder; owlseye does not read through it");
        var (prot, aces) = DaclOf(h);
        return (prot, aces, IdOf(info));
    }

    static unsafe string FinalPath(SafeFileHandle h)
    {
        var buf = stackalloc char[1024];
        var n = Native.GetFinalPathNameByHandle(h, buf, 1024, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
        if (n == 0) throw Fail("GetFinalPathNameByHandle");
        if (n >= 1024)
        {
            var big = new char[n + 1];
            fixed (char* b = big) n = Native.GetFinalPathNameByHandle(h, b, (uint)big.Length, 0);
            return new string(big, 0, (int)n).TrimEnd('\\').ToLowerInvariant();
        }
        return new string(buf, 0, (int)n).TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>Open one folder by handle: not a reparse point itself, cannot be renamed or deleted while open.</summary>
    SafeFileHandle OpenOne(string rel, uint access = 0)
    {
        var h = Native.CreateFile(Long(rel), Native.ReadControl | access, Native.FileShareRead | Native.FileShareWrite, 0,
            Native.OpenExisting, Native.FileFlagBackupSemantics | Native.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid) throw Fail("CreateFile");
        try
        {
            if (!Native.GetFileInformationByHandle(h, out var info)) throw Fail("GetFileInformationByHandle");
            if ((info.FileAttributes & Native.FileAttributeReparsePoint) != 0 || (info.FileAttributes & Native.FileAttributeDirectory) == 0)
                throw new UnauthorizedAccessException($"{rel} is a junction or link, not a folder; owlseye does not write there");
        }
        catch
        {
            h.Dispose();
            throw;
        }
        return h;
    }

    string? rootFinal;

    /// <summary>Final path of the share root itself (DFS, mapped or substituted drives resolve to something else).</summary>
    string RootFinal()
    {
        if (rootFinal is not null) return rootFinal;
        using var h = Native.CreateFile(Long(""), Native.ReadControl, Native.FileShareRead | Native.FileShareWrite | Native.FileShareDelete, 0,
            Native.OpenExisting, Native.FileFlagBackupSemantics, 0);
        if (h.IsInvalid) throw Fail("CreateFile");
        return rootFinal = FinalPath(h);
    }

    /// <summary>Handles for every folder from the share root down to `rel`; runs `use` with the leaf.
    ///
    /// Users own the folders they create (CREATOR OWNER), so they can swap one for a junction at any time.
    /// A check by path followed by a write by path is a race; everything runs on handles instead.
    /// Each component is opened with FILE_FLAG_OPEN_REPARSE_POINT and checked, and stays open (no
    /// FILE_SHARE_DELETE) until the caller is done, so no junction can be slid into the chain in between.
    /// As a second layer the leaf's final path is compared with the share root's. Over SMB the redirector may
    /// answer that from the client-side name, so the chain of handles is the check that counts.</summary>
    protected void WithChain(string rel, uint access, Action<SafeFileHandle> use)
    {
        var parts = rel != "" ? rel.Split('\\') : [];
        var handles = new List<SafeFileHandle>();
        try
        {
            for (var i = 0; i <= parts.Length; i++)
                handles.Add(OpenOne(string.Join("\\", parts[..i]), i == parts.Length ? access : 0));
            var leaf = handles[^1];
            var expected = RootFinal() + (rel != "" ? "\\" + rel.ToLowerInvariant() : "");
            if (FinalPath(leaf) != expected)
                throw new UnauthorizedAccessException($"{rel} goes through a junction or link; owlseye does not write there");
            use(leaf);
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
        }
    }

    public void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces) => WriteDacl(rel, isProtected, aces, null);

    /// <summary>Writes through the handle chain; with `expected`, first checks on the same pinned leaf handle that it is
    /// still the scanned folder (identity, explicit entries, protection), so nothing can be swapped in between.</summary>
    public unsafe void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces, ExpectedAcl? expected)
    {
        var acl = new RawAcl(GenericAcl.AclRevisionDS, aces.Count);
        var i = 0;
        foreach (var (atype, aflags, mask, sid) in aces)
            acl.InsertAce(i++, new CommonAce((AceFlags)(byte)aflags, atype == 0 ? AceQualifier.AccessAllowed : AceQualifier.AccessDenied,
                unchecked((int)mask), new SecurityIdentifier(sid), false, null));
        var bin = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bin, 0);
        var mode = isProtected ? Native.ProtectedDaclSecurityInformation : Native.UnprotectedDaclSecurityInformation;
        WithChain(rel, Native.WriteDac, h =>
        {
            if (expected is not null)
            {
                if (!Native.GetFileInformationByHandle(h, out var info)) throw Fail("GetFileInformationByHandle");
                var (prot, current) = DaclOf(h);
                expected.Check(rel, prot, current, IdOf(info));
            }
            // Sets the DACL and propagates inheritance into the subtree (can take a while on large trees).
            // Propagation and junctions inside the subtree: on local NTFS the propagation updates the junction object
            // but does not follow it (Win32Tests.PropagationDoesNotFollowAJunctionOnLocalNtfs). Over SMB this still
            // has to be verified in the lab; until then AdProvider refuses to write above any link the scan saw (Links).
            fixed (byte* p = bin)
            {
                var rc = Native.SetSecurityInfo(h, Native.SeFileObject, Native.DaclSecurityInformation | mode, 0, 0, p, 0);
                if (rc != 0) throw Fail("SetSecurityInfo", rc);
            }
        });
    }

    public bool Exists(string rel) => Directory.Exists(Long(rel));

    /// <summary>Create under a verified parent chain, then prove the new folder is where it should be (undo if not).</summary>
    public void Mkdir(string rel)
    {
        var parent = rel.Contains('\\') ? rel[..rel.LastIndexOf('\\')] : "";
        WithChain(parent, 0, _ => // held open: nothing above can be renamed during mkdir
        {
            if (!Native.CreateDirectory(Long(rel), 0))
            {
                var err = Marshal.GetLastPInvokeError();
                throw err == Native.ErrorAlreadyExists ? new IOException($"{rel} already exists") : Fail("CreateDirectory", err);
            }
            try
            {
                WithChain(rel, 0, _ => { });
            }
            catch (Exception)
            {
                Native.RemoveDirectory(Long(rel));
                throw;
            }
        });
    }

    public unsafe (string Name, string Domain, int Use) LookupSid(string sid)
    {
        SecurityIdentifier si;
        try
        {
            si = new SecurityIdentifier(sid);
        }
        catch (ArgumentException)
        {
            throw new KeyNotFoundException(sid);
        }
        var bin = new byte[si.BinaryLength];
        si.GetBinaryForm(bin, 0);
        uint nameLen = 256, domLen = 256;
        var name = new char[nameLen];
        var dom = new char[domLen];
        fixed (byte* s = bin)
        {
            while (true)
            {
                bool ok;
                int use;
                fixed (char* n = name, d = dom)
                    ok = Native.LookupAccountSid(0, s, n, ref nameLen, d, ref domLen, out use);
                if (ok) return (new string(name, 0, (int)nameLen), new string(dom, 0, (int)domLen), use);
                var err = Marshal.GetLastPInvokeError();
                if (err == Native.ErrorInsufficientBuffer)
                {
                    name = new char[nameLen];
                    dom = new char[domLen];
                    continue;
                }
                throw new KeyNotFoundException(sid);
            }
        }
    }

    static readonly EnumerationOptions Opts = new()
    {
        AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
    };

    /// <summary>Subfolders without symlinks and junctions; those go into Links (writes above them are refused).</summary>
    public List<string> Subdirs(string rel)
    {
        var names = new List<string>();
        foreach (var d in new DirectoryInfo(Long(rel) + "\\").EnumerateDirectories("*", Opts))
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                lock (Links) Links.Add(rel != "" ? $"{rel}\\{d.Name}" : d.Name); // the scan lists several folders at a time
            }
            else names.Add(d.Name);
        }
        return names;
    }
}
