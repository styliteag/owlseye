using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Owlseye.Providers;

namespace Owlseye.Windows;

/// <summary>AD via ADSI (System.DirectoryServices) with integrated logon, read-only (accounts and members; the admin
/// keeps maintaining groups in AD). No password, no service account.</summary>
public sealed class AdsiDirectory : IDirectory
{
    static readonly HashSet<string> MultiAttrs = new(StringComparer.OrdinalIgnoreCase) { "member", "objectClass" };

    static string AdsPath(string dn) => "LDAP://" + dn.Replace("/", "\\/");

    /// <summary>Kerberos/NTLM with signing and sealing: memberships and SIDs are not readable or changeable on the way.</summary>
    const AuthenticationTypes Auth = AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

    public string WhoAmI() => WindowsIdentity.GetCurrent().Name;

    string? root;

    public string RootDn()
    {
        if (root is not null) return root;
        using var e = new DirectoryEntry("LDAP://RootDSE", null, null, Auth);
        return root = (string)e.Properties["defaultNamingContext"].Value!;
    }

    public List<Row> Query(string searchBase, string filter, IReadOnlyList<string> attrs, string scope = "subtree")
    {
        using var entry = new DirectoryEntry(AdsPath(searchBase), null, null, Auth);
        using var searcher = new DirectorySearcher(entry, filter, attrs.ToArray(), scope == "base" ? SearchScope.Base : SearchScope.Subtree)
        {
            PageSize = 1000,
        };
        using var results = searcher.FindAll();
        var rows = new List<Row>();
        foreach (SearchResult r in results)
        {
            var row = new Row();
            foreach (var a in attrs)
            {
                var vals = r.Properties[a];
                if (MultiAttrs.Contains(a))
                {
                    var all = vals is { Count: > 0 } ? vals.Cast<object>().Select(v => v.ToString()!).ToList() : Ranged(r, a);
                    row[a] = all.Count > 0 ? all.ToArray() : null;
                }
                else if (vals is null || vals.Count == 0) row[a] = null;
                else row[a] = vals[0] is int i ? (long)i : vals[0];
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A multi-valued attribute with more values than the server returns at once (member of a group with more
    /// than 1500 members) comes back as "member;range=0-1499" instead of "member": fetch the rest range by range.</summary>
    static List<string> Ranged(SearchResult r, string attr)
    {
        var values = new List<string>();
        string? Key(ResultPropertyCollection props) => props.PropertyNames.Cast<string>()
            .FirstOrDefault(n => n.StartsWith(attr + ";range=", StringComparison.OrdinalIgnoreCase));
        var key = Key(r.Properties);
        if (key is null) return values;
        values.AddRange(r.Properties[key].Cast<object>().Select(v => v.ToString()!));
        using var entry = r.GetDirectoryEntry();
        while (!key.EndsWith("*", StringComparison.Ordinal))
        {
            using var s = new DirectorySearcher(entry, "(objectClass=*)", [$"{attr};range={values.Count}-*"], SearchScope.Base);
            var next = s.FindOne();
            key = next is null ? null : Key(next.Properties);
            if (key is null) break;
            var got = next!.Properties[key].Cast<object>().Select(v => v.ToString()!).ToList();
            if (got.Count == 0) break;
            values.AddRange(got);
        }
        return values;
    }
}

/// <summary>The local user database (SAM) of this machine, presented in LDAP form (OU=Groups / OU=Users under
/// DC=&lt;PC&gt;). Local groups appear directly in the ACLs, users are their members: exactly the file server model,
/// just without AD.</summary>
public sealed unsafe partial class LocalDirectory : IDirectory, ICachingDirectory
{
    const long LocalGroup = -2147483644; // security group, local
    static readonly HashSet<uint> BuiltinRids = [500, 501, 503, 504]; // Administrator, Guest, DefaultAccount, WDAGUtilityAccount
    const int UfAccountDisable = 0x2;

    public LocalDirectory()
    {
        Computer = Environment.MachineName;
        Root = $"DC={Computer}";
        GroupsOu = $"OU=Groups,{Root}";
        UsersOu = $"OU=Users,{Root}";
    }

    public string Computer { get; }
    public string Root { get; }
    public string GroupsOu { get; }
    public string UsersOu { get; }

    public string Sid(string name) =>
        ((SecurityIdentifier)new NTAccount(Computer, name).Translate(typeof(SecurityIdentifier))).Value;

    [StructLayout(LayoutKind.Sequential)]
    struct UserInfo20
    {
        public nint Name, FullName, Comment;
        public uint Flags, UserId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LocalGroupInfo1
    {
        public nint Name, Comment;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserEnum(string? server, int level, int filter, out nint buf, int prefMax, out int read, out int total, ref uint resume);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupEnum(string? server, int level, out nint buf, int prefMax, out int read, out int total, ref nuint resume);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(string? server, string group, int level, out nint buf, int prefMax, out int read, out int total, ref nuint resume);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buf);

    const int MaxPreferred = -1, MoreData = 234, FilterNormalAccount = 2;

    static List<T> Paged<T>(Func<(int Rc, nint Buf, int Read)> call, Func<nint, T> item, int size)
    {
        var o = new List<T>();
        while (true)
        {
            var (rc, buf, read) = call();
            if (rc != 0 && rc != MoreData) throw new IOException($"NetApi error {rc}");
            try
            {
                for (var i = 0; i < read; i++) o.Add(item(buf + i * size));
            }
            finally
            {
                if (buf != 0) NetApiBufferFree(buf);
            }
            if (rc != MoreData) return o;
        }
    }

    Dictionary<string, Dictionary<string, object?>> Objects()
    {
        var objects = new Dictionary<string, Dictionary<string, object?>>();
        var users = new Dictionary<string, string>(); // SID -> DN
        uint ur = 0;
        var userList = Paged(() =>
        {
            var rc = NetUserEnum(null, 20, FilterNormalAccount, out var b, MaxPreferred, out var r, out _, ref ur);
            return (rc, b, r);
        }, p =>
        {
            var u = Marshal.PtrToStructure<UserInfo20>(p);
            return (Name: Marshal.PtrToStringUni(u.Name)!, Full: Marshal.PtrToStringUni(u.FullName) ?? "", u.Flags, u.UserId);
        }, Marshal.SizeOf<UserInfo20>());
        foreach (var u in userList)
        {
            if (BuiltinRids.Contains(u.UserId)) continue;
            string sid;
            try
            {
                sid = Sid(u.Name);
            }
            catch (IdentityNotMappedException)
            {
                continue;
            }
            var dn = $"CN={u.Name},{UsersOu}";
            objects[dn] = new()
            {
                ["objectClass"] = new List<object> { "top", "person", "organizationalPerson", "user" },
                ["sAMAccountName"] = u.Name,
                ["displayName"] = u.Full != "" ? u.Full : u.Name,
                ["userAccountControl"] = (long)(u.Flags & UfAccountDisable),
                ["objectSid"] = sid,
            };
            users[sid] = dn;
        }
        nuint gr = 0;
        var groupList = Paged(() =>
        {
            var rc = NetLocalGroupEnum(null, 1, out var b, MaxPreferred, out var r, out _, ref gr);
            return (rc, b, r);
        }, p =>
        {
            var g = Marshal.PtrToStructure<LocalGroupInfo1>(p);
            return (Name: Marshal.PtrToStringUni(g.Name)!, Comment: Marshal.PtrToStringUni(g.Comment) ?? "");
        }, Marshal.SizeOf<LocalGroupInfo1>());
        foreach (var g in groupList)
        {
            string sid;
            try
            {
                sid = Sid(g.Name);
            }
            catch (IdentityNotMappedException) // BUILTIN (Administrators, Users, …) is not under <PC>\
            {
                continue;
            }
            if (sid.StartsWith("S-1-5-32-")) continue;
            nuint mr = 0;
            var members = Paged(() =>
            {
                var rc = NetLocalGroupGetMembers(null, g.Name, 0, out var b, MaxPreferred, out var r, out _, ref mr);
                return (rc, b, r);
            }, p => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value, nint.Size);
            objects[$"CN={g.Name},{GroupsOu}"] = new()
            {
                ["objectClass"] = new List<object> { "top", "group" },
                ["sAMAccountName"] = g.Name,
                ["groupType"] = LocalGroup,
                ["member"] = members.Where(users.ContainsKey).Select(s => (object)users[s]).ToList(),
                ["info"] = g.Comment,
                ["objectSid"] = sid,
            };
        }
        return objects;
    }

    public string WhoAmI() => WindowsIdentity.GetCurrent().Name;

    public string RootDn() => Root;

    Dictionary<string, Dictionary<string, object?>>? cached;
    long cachedAt;
    readonly Lock gate = new();

    public void Forget()
    {
        lock (gate) cached = null;
    }

    /// <summary>The object table, read once per scan (and at most every 15 seconds otherwise): a scan sends several
    /// queries in a row (accounts, members level by level, primary groups) and the group search one per keystroke.</summary>
    Dictionary<string, Dictionary<string, object?>> Table()
    {
        lock (gate)
        {
            if (cached is null || Environment.TickCount64 - cachedAt > 15_000)
            {
                cached = Objects();
                cachedAt = Environment.TickCount64;
            }
            return cached;
        }
    }

    public List<Row> Query(string searchBase, string filter, IReadOnlyList<string> attrs, string scope = "subtree") =>
        SimQuery.QueryObjects(Table(), searchBase, filter, attrs, scope);
}
