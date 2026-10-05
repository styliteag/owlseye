// AD provider: logic over the ports IDirectory and IFilesystem.
//
// Platform-neutral. On Windows with Win32/ADSI adapters, locally with the SAM database,
// in the sim with the emulation. So the same logic runs everywhere.
//
// Reading starts from the filesystem: all folders (up to scanDepth), their ACLs, from those the accounts (columns).
// Only then does owlseye query the directory for these accounts and their (nested) members.

using System.Collections.Concurrent;
using System.Text;

namespace Owlseye.Providers;

public class AdProvider : IProvider
{
    public const int AccessAllowed = 0, AccessDenied = 1;
    const int Batch = 50;

    static readonly Dictionary<int, string> SidKind = new() { [1] = "user", [2] = "group", [4] = "group", [5] = "wellknown", [9] = "computer" };

    static readonly HashSet<string> WellKnown = ["S-1-5-18", "S-1-5-32-544", "S-1-3-0", "S-1-3-4", "S-1-1-0", "S-1-5-11", "S-1-5-32-545"];

    /// <summary>Selectable via "+ Group" although not in the directory (owlseye sets SYSTEM/Administrators).</summary>
    public static readonly IReadOnlyList<(string Sid, string[] Aliases)> BuiltinChoices =
    [
        ("S-1-5-32-545", ["Users", "Benutzer"]),
        ("S-1-5-32-547", ["Power Users", "Hauptbenutzer"]),
        ("S-1-5-32-555", ["Remote Desktop Users", "Remotedesktopbenutzer"]),
        ("S-1-5-11", ["Authenticated Users", "Authentifizierte Benutzer"]),
        ("S-1-1-0", ["Everyone", "Jeder"]),
    ];

    const string Safe = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 =,.-_";

    /// <summary>RFC 4515 for filter values: everything except harmless ASCII characters as \xx (UTF-8).
    /// Also ; &lt; &gt; , because ADODB assembled the command as &lt;base&gt;;filter;attrs;scope.</summary>
    public static string LdapEscape(string v)
    {
        var sb = new StringBuilder();
        Span<byte> buf = stackalloc byte[4];
        foreach (var r in v.EnumerateRunes())
        {
            if (r.IsAscii && Safe.Contains((char)r.Value)) sb.Append((char)r.Value);
            else
            {
                var n = r.EncodeToUtf8(buf);
                for (var i = 0; i < n; i++) sb.Append('\\').Append(buf[i].ToString("x2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>Binary SID (objectSid) -> S-1-…</summary>
    public static string SidFromBytes(byte[] b)
    {
        var count = b[1];
        long authority = 0;
        for (var i = 2; i < 8; i++) authority = (authority << 8) | b[i];
        var parts = new List<string> { "S", b[0].ToString(), authority.ToString() };
        for (var i = 0; i < count; i++) parts.Add(BitConverter.ToUInt32(b, 8 + 4 * i).ToString());
        return string.Join("-", parts);
    }

    public static byte[] SidToBytes(string sid)
    {
        var p = sid.Split('-');
        var subs = p[3..].Select(uint.Parse).ToArray();
        var o = new byte[8 + 4 * subs.Length];
        o[0] = byte.Parse(p[1]);
        o[1] = (byte)subs.Length;
        var authority = long.Parse(p[2]);
        for (var i = 7; i >= 2; i--)
        {
            o[i] = (byte)(authority & 0xFF);
            authority >>= 8;
        }
        for (var i = 0; i < subs.Length; i++) BitConverter.GetBytes(subs[i]).CopyTo(o, 8 + 4 * i);
        return o;
    }

    protected readonly IFilesystem Fs;
    protected readonly IDirectory Dir;
    readonly int scanDepth;
    readonly int parallelism;
    Dictionary<string, (string Name, string Kind)> sidCache = [];

    /// <summary>scanDepth: deepest level read, 0 = whole tree. How deep the matrix shows is up to the UI.
    /// parallelism: folders read at the same time (the ports must be thread-safe for more than 1).</summary>
    public AdProvider(IFilesystem fs, IDirectory directory, string share, int scanDepth = 0, int parallelism = 1)
    {
        Fs = fs;
        Dir = directory;
        Share = fs.ToUnc(share);
        this.scanDepth = scanDepth;
        this.parallelism = parallelism;
    }

    public virtual string Name => "ad";

    public string Share { get; }

    public string WhoAmI() => Dir.WhoAmI();

    // --- Filesystem ------------------------------------------------------------------

    /// <summary>SID -> (DOMAIN\name, kind).</summary>
    (string Name, string Kind) Lookup(string s)
    {
        (string Name, string Kind) hit;
        bool known;
        lock (sidCache) known = sidCache.TryGetValue(s, out hit);
        if (!known)
        {
            try
            {
                var (name, dom, typ) = Fs.LookupSid(s);
                hit = (dom != "" ? $"{dom}\\{name}" : name, SidKind.GetValueOrDefault(typ, "group"));
            }
            catch (KeyNotFoundException)
            {
                hit = (s, "unknown");
            }
            lock (sidCache) sidCache[s] = hit; // parallel scan: two threads may look up the same SID, harmless
        }
        return (hit.Name, WellKnown.Contains(s) ? "wellknown" : hit.Kind);
    }

    /// <summary>(protected, Allow/Deny ACEs, whether there are other ACE types).</summary>
    (bool Protected, List<Ace> Aces, bool Other) ReadAclFull(string rel)
    {
        var (prot, aces, other, _) = ReadAclWithId(rel, byHandle: false);
        return (prot, aces, other);
    }

    /// <summary>byHandle: through IFilesystem.ReadFolder (the scan: the folder itself and its identity); otherwise by path.</summary>
    (bool Protected, List<Ace> Aces, bool Other, string Id) ReadAclWithId(string rel, bool byHandle)
    {
        bool prot;
        List<RawAce> raw;
        var id = "";
        if (byHandle) (prot, raw, id) = Fs.ReadFolder(rel);
        else (prot, raw) = Fs.ReadDacl(rel);
        var aces = new List<Ace>();
        var other = false;
        foreach (var (atype, aflags, mask, sid) in raw)
        {
            if (atype is not (AccessAllowed or AccessDenied))
            {
                other = true;
                continue;
            }
            var (name, kind) = Lookup(sid);
            aces.Add(new Ace(sid, name, kind, mask, atype == AccessAllowed, (aflags & M.InheritedAce) != 0, aflags));
        }
        return (prot, aces, other, id);
    }

    void CheckFolder(string rel)
    {
        if (rel != "" && rel.Split('\\').Any(M.BadComponent))
            throw new UnauthorizedAccessException($"Invalid folder path: {Msg.Quote(rel)}");
        if (scanDepth > 0 && M.LevelOf(rel) > scanDepth)
            throw new UnauthorizedAccessException($"{rel} is below the scanned depth ({scanDepth})");
    }

    public (bool Protected, List<Ace> Aces) FolderAcl(string path)
    {
        CheckFolder(path);
        var (prot, aces, _) = ReadAclFull(path);
        return (prot, aces);
    }

    public bool FolderExists(string path) => Fs.Exists(path);

    public void CreateFolder(string path)
    {
        var parts = path.Split('\\');
        if (parts.Any(M.BadComponent) || M.BadFolderName(parts[^1]))
            throw new UnauthorizedAccessException($"Invalid new folder: {Msg.Quote(path)}");
        CheckFolder(path);
        var parent = string.Join("\\", parts[..^1]);
        if (parent != "" && !Fs.Exists(parent)) throw new UnauthorizedAccessException($"{parent} does not exist");
        if (Fs.Exists(path)) throw new IOException($"{path} already exists");
        Fs.Mkdir(path);
    }

    void RefuseLinksBelow(string path)
    {
        var prefix = path != "" ? M.Lower(path) + "\\" : "";
        var hit = Fs.Links.Where(x => M.Lower(x).StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (hit.Count > 0)
            throw new UnauthorizedAccessException(
                $"{(path != "" ? path : "the share root")} has a junction or link below it ({hit[0]}); inheritance would "
                + "propagate through it. Remove the link first.");
    }

    public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => SetFolderAcl(path, isProtected, aces, null);

    public Principal? ResolveAccount(string sid)
    {
        var (name, kind) = Lookup(sid);
        return new Principal(sid, name, kind);
    }

    public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces, Folder? before)
    {
        CheckFolder(path);
        RefuseLinksBelow(path);
        if (ReadAclFull(path).Other) // callback/object ACEs would be lost on rewrite
            throw new UnauthorizedAccessException($"{path} has ACL entries of other types; owlseye does not rewrite it");
        var raw = aces.OrderBy(a => a.Allow) // canonical: Deny before Allow
            .Select(a => new RawAce(a.Allow ? AccessAllowed : AccessDenied, a.Flags & ~M.InheritedAce, a.Mask, a.Sid)).ToList();
        Fs.WriteDacl(path, isProtected, raw, before is null ? null : ExpectedAcl.Of(before));
    }

    /// <summary>One folder: its ACL and the subfolders to read next. One unreadable folder never aborts the scan.</summary>
    (Folder Folder, List<string> Children) ReadOne(string rel, int level)
    {
        if (rel != "" && M.BadComponent(rel[(rel.LastIndexOf('\\') + 1)..]))
        {
            // Win32 strips trailing dots/spaces and misparses ?*"<>|: (creatable via Samba, WSL, \\?\):
            // the ACL read would hit another folder. Flag it, never read or write it, do not descend.
            return (new Folder(rel, level, OtherAces: true, Error: "Name Windows cannot address as written"), []);
        }
        bool prot, other;
        List<Ace> aces;
        string id;
        try
        {
            (prot, aces, other, id) = ReadAclWithId(rel, byHandle: true);
        }
        catch (Exception e) // no READ_CONTROL, undecodable ACE: a finding
        {
            return (new Folder(rel, level, OtherAces: true, Error: $"ACL not readable: {e.Message}"), []);
        }
        var folder = new Folder(rel, level, prot, aces, other, Id: id);
        if (scanDepth > 0 && level >= scanDepth) return (folder, []); // safeguard for huge shares; 0 = whole tree
        try
        {
            var names = Fs.Subdirs(rel);
            return (folder, names.Select(n => rel != "" ? $"{rel}\\{n}" : n).ToList());
        }
        catch (Exception e) // not allowed to list folder: finding instead of abort
        {
            return (folder with { Error = $"Contents not readable: {e.Message}" }, []);
        }
    }

    /// <summary>All folders in tree order (children by name, case-insensitive), the order the rest relies on.</summary>
    Dictionary<string, Folder> Walk(Progress progress)
    {
        if (parallelism > 1) return WalkParallel(progress);
        // iterative: users nest folders deeper than a recursion would like
        var folders = new Dictionary<string, Folder>();
        var stack = new Stack<(string Rel, int Level)>();
        stack.Push(("", 0));
        while (stack.Count > 0)
        {
            var (rel, level) = stack.Pop();
            progress.Set(phase: "Reading folders", path: rel, done: folders.Count);
            var (folder, children) = ReadOne(rel, level);
            folders[rel] = folder;
            foreach (var c in children.OrderByDescending(M.Lower, M.Ci)) // stack: pop yields A before B
                stack.Push((c, level + 1));
        }
        return folders;
    }

    /// <summary>Level by level, several folders at a time: over SMB every ACL read and listing waits for the network,
    /// so a large share is read several times faster. The result is put into the same order as the sequential walk.</summary>
    Dictionary<string, Folder> WalkParallel(Progress progress)
    {
        var found = new ConcurrentDictionary<string, Folder>();
        var level = new List<string> { "" };
        for (var depth = 0; level.Count > 0; depth++)
        {
            var next = new ConcurrentBag<string>();
            var d = depth;
            Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, rel =>
            {
                var (folder, children) = ReadOne(rel, d);
                found[rel] = folder;
                progress.Set(phase: "Reading folders", path: rel, done: found.Count);
                foreach (var c in children) next.Add(c);
            });
            level = [.. next];
        }
        var o = new Dictionary<string, Folder>();
        foreach (var k in found.Keys.Order(Comparer<string>.Create(M.TreeCompare))) o[k] = found[k];
        return o;
    }

    // --- Directory ------------------------------------------------------------------

    List<Row> Search(List<string> flts, IReadOnlyList<string> attrs)
    {
        var o = new List<Row>();
        for (var i = 0; i < flts.Count; i += Batch)
            o.AddRange(Dir.Query(Dir.RootDn(), "(|" + string.Concat(flts.Skip(i).Take(Batch)) + ")", attrs));
        return o;
    }

    /// <summary>All accounts in the ACLs (explicit, and what the root inherits from above), the hidden ones too (the matrix
    /// can show them, M.ShowHidden); not Creator Owner and Owner Rights.</summary>
    Dictionary<string, Principal> Principals(Dictionary<string, Folder> folders)
    {
        var found = new Dictionary<string, Ace>();
        foreach (var f in folders.Values)
            foreach (var a in f.Aces)
                if ((!a.Inherited || f.Level == 0) && a.Sid is not (M.CreatorOwner or M.OwnerRights))
                    found[a.Sid] = a;
        var rows = Search(found.Keys.Order(StringComparer.Ordinal).Where(s => s.StartsWith("S-1-5-21-"))
            .Select(s => $"(objectSid={LdapEscape(s)})").ToList(), ["distinguishedName", "objectSid"]);
        var dnBySid = new Dictionary<string, string>();
        foreach (var r in rows)
            if (r.Bytes("objectSid") is { } b) dnBySid[SidFromBytes(b)] = r.S("distinguishedName")!;
        return found.ToDictionary(kv => kv.Key, kv => new Principal(kv.Key, kv.Value.Name, kv.Value.Kind, dnBySid.GetValueOrDefault(kv.Key, "")));
    }

    /// <summary>Group columns with all (also nested) members; user columns themselves.</summary>
    (Dictionary<string, Group>, Dictionary<string, User>) Members(Dictionary<string, Principal> principals)
    {
        string[] attrs = ["distinguishedName", "sAMAccountName", "objectClass", "member", "objectSid", "displayName", "userAccountControl"];
        var groups = new Dictionary<string, Group>();
        var users = new Dictionary<string, User>();
        var todo = principals.Values.Where(p => p.Dn != "").Select(p => p.Dn).ToHashSet();
        var seen = new HashSet<string>();
        while (todo.Count > 0)
        {
            var batch = todo.Order(StringComparer.Ordinal).ToList();
            todo = [];
            seen.UnionWith(batch);
            foreach (var r in Search(batch.Select(d => $"(distinguishedName={LdapEscape(d)})").ToList(), attrs))
            {
                var dn = r.S("distinguishedName")!;
                var classes = r.Multi("objectClass");
                var sid = r.Bytes("objectSid") is { } b ? SidFromBytes(b) : "";
                if (classes.Contains("group"))
                {
                    var g = new Group(dn, r.S("sAMAccountName") ?? "", r.Multi("member").ToHashSet(), sid);
                    groups[dn] = g;
                    todo.UnionWith(g.Members.Where(m => !seen.Contains(m)));
                }
                else if (classes.Contains("user") && !classes.Contains("computer")) users[dn] = UserOf(r, dn, sid);
            }
        }
        AddPrimaryMembers(groups, users, attrs);
        return (groups, users);
    }

    static User UserOf(Row r, string dn, string sid)
    {
        var uac = r.L("userAccountControl");
        var sam = r.S("sAMAccountName") ?? "";
        return new User(dn, sam, r.S("displayName") is { Length: > 0 } d ? d : sam, (uac & 2) == 0, sid);
    }

    /// <summary>A user's primary group (typically Domain Users) is not in the group's "member" attribute but in the user's
    /// primaryGroupID (the group's RID). Without this, Domain Users would look empty on a share where it has rights.</summary>
    void AddPrimaryMembers(Dictionary<string, Group> groups, Dictionary<string, User> users, string[] attrs)
    {
        var bySid = new Dictionary<string, Group>();
        foreach (var g in groups.Values)
            if (g.Sid.StartsWith("S-1-5-21-")) bySid[g.Sid] = g;
        if (bySid.Count == 0) return;
        var rids = bySid.Keys.Select(s => s[(s.LastIndexOf('-') + 1)..]).Distinct().Order(StringComparer.Ordinal).ToList();
        string[] withPrimary = [.. attrs, "primaryGroupID"];
        for (var i = 0; i < rids.Count; i += Batch)
        {
            var flt = "(&(objectClass=user)(|" + string.Concat(rids.Skip(i).Take(Batch).Select(r => $"(primaryGroupID={r})")) + "))";
            foreach (var r in Dir.Query(Dir.RootDn(), flt, withPrimary))
            {
                if (r.Multi("objectClass").Contains("computer") || r.Bytes("objectSid") is not { } b) continue;
                var sid = SidFromBytes(b);
                var groupSid = sid[..sid.LastIndexOf('-')] + "-" + r.L("primaryGroupID");
                if (!bySid.TryGetValue(groupSid, out var g)) continue;
                var dn = r.S("distinguishedName")!;
                g.Members.Add(dn);
                if (!users.ContainsKey(dn)) users[dn] = UserOf(r, dn, sid);
            }
        }
    }

    public List<Principal> FindGroups(string q)
    {
        q = q.Trim();
        if (q == "") return [];
        var flt = $"(&(objectClass=group)(sAMAccountName={LdapEscape(q)}*))";
        var rows = Dir.Query(Dir.RootDn(), flt, ["distinguishedName", "objectSid", "sAMAccountName"]);
        var o = new List<Principal>();
        foreach (var r in rows.OrderBy(r => M.Lower(r.S("sAMAccountName") ?? ""), M.Ci).Take(50))
        {
            var sid = r.Bytes("objectSid") is { } b ? SidFromBytes(b) : "";
            if (sid != "" && !M.Hides(sid, r.S("sAMAccountName")))
            {
                var (name, kind) = Lookup(sid);
                o.Add(new Principal(sid, name, kind, r.S("distinguishedName") ?? ""));
            }
        }
        // Built-in groups are not in the directory; names are localized (Hauptbenutzer = Power Users)
        foreach (var (sid, aliases) in BuiltinChoices)
        {
            var (name, kind) = Lookup(sid);
            var shortName = name[(name.LastIndexOf('\\') + 1)..];
            if (kind != "unknown" && new[] { shortName }.Concat(aliases).Any(n => M.Lower(n).StartsWith(M.Lower(q), StringComparison.Ordinal)))
                o.Add(new Principal(sid, name, kind));
        }
        return o;
    }

    public Snapshot Scan(Progress? progress = null)
    {
        progress ??= new Progress(); // nobody watching
        sidCache = []; // do not carry renames and transient lookup errors across scans
        (Dir as ICachingDirectory)?.Forget();
        Fs.Links.Clear();
        var folders = Walk(progress);
        progress.Set(phase: "Looking up the accounts in the directory", path: "");
        var principals = Principals(folders);
        progress.Set(phase: "Reading group members");
        var (groups, users) = Members(principals);
        return new Snapshot(Share, folders, principals, groups, users, Clock.Now());
    }
}
