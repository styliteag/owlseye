// Emulation of AD and file server for development without Windows.
//
// A sim directory contains:
//   state.json   AD objects with real attribute names and types, SID table, explicit ACLs
//   share/       real folder tree = share root (creating/deleting folders takes effect on the next scan)
//
// The logic from AdProvider runs on top unchanged, so the same code as on Windows.
// Writes land atomically in state.json and survive a restart. Anyone who edits state.json
// by hand simulates a change in ADUC; it is read on the next access.

using System.Text.Json.Nodes;

namespace Owlseye.Providers;

/// <summary>Read state.json (anew when the file changes) and write atomically.</summary>
public sealed class SimState(string simDir)
{
    public const string FileName = "state.json";
    public string PathOf { get; } = System.IO.Path.Combine(simDir, FileName);
    DateTime mtime = DateTime.MinValue;
    long size = -1;
    JsonObject data = [];

    public JsonObject Data
    {
        get
        {
            var fi = new FileInfo(PathOf);
            if (!fi.Exists) throw new FileNotFoundException($"Sim state missing: {PathOf}");
            if (fi.LastWriteTimeUtc != mtime || fi.Length != size)
            {
                data = JsonNode.Parse(FileLock.ReadAllText(PathOf)) as JsonObject ?? [];
                mtime = fi.LastWriteTimeUtc;
                size = fi.Length;
            }
            return data;
        }
    }

    public void Save(JsonObject d)
    {
        var text = Json.Pretty(d);
        Json.WriteAtomic(PathOf, text, ".state-");
        data = (JsonObject)JsonNode.Parse(text)!; // cache what a reader of the file gets (numbers as parsed, not as CLR types)
        var fi = new FileInfo(PathOf);
        mtime = fi.LastWriteTimeUtc;
        size = fi.Length;
    }

    public static void Write(string path, JsonObject d) => Json.WriteAtomic(path, Json.Pretty(d), ".state-");

    JsonObject? aclsOf;
    Dictionary<string, JsonNode?> aclsLower = [];

    /// <summary>The "acls" object keyed by lowercased path, built once per version of the data (a scan reads it for every
    /// folder and its parents).</summary>
    public Dictionary<string, JsonNode?> Acls()
    {
        var d = Data;
        if (!ReferenceEquals(d, aclsOf))
        {
            aclsLower = (d.Obj("acls") ?? []).ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value, StringComparer.Ordinal);
            aclsOf = d;
        }
        return aclsLower;
    }

    /// <summary>JSON value -> CLR value for the LDAP emulation: string, long, bool, List&lt;object?&gt;.</summary>
    public static object? Clr(JsonNode? n) => n switch
    {
        null => null,
        JsonArray a => a.Select(Clr).ToList(),
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Clr(kv.Value)),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => n.ToJsonString(),
    };
}

public static class SimQuery
{
    static readonly HashSet<string> MultiAttrs = new(StringComparer.OrdinalIgnoreCase) { "member", "objectclass" }; // ADODB returns these as tuple

    static bool InScope(string dn, string searchBase, string scope)
    {
        dn = dn.ToLowerInvariant();
        searchBase = searchBase.ToLowerInvariant();
        return scope switch
        {
            "base" => dn == searchBase,
            "subtree" => dn == searchBase || dn.EndsWith("," + searchBase, StringComparison.Ordinal),
            _ => throw new ArgumentException($"Unknown scope: {scope}"),
        };
    }

    /// <summary>LDAP search over {DN: attributes}, rows like ADODB (also for the local provider).</summary>
    public static List<Row> QueryObjects(IEnumerable<KeyValuePair<string, Dictionary<string, object?>>> objects, string searchBase,
        string flt, IReadOnlyList<string> attrs, string scope)
    {
        var node = LdapFilter.Parse(flt);
        var rows = new List<Row>();
        foreach (var (dn, obj) in objects)
        {
            var full = new Dictionary<string, object?>(obj) { ["distinguishedName"] = dn };
            if (!InScope(dn, searchBase, scope) || !LdapFilter.Matches(node, full)) continue;
            var row = new Row();
            foreach (var a in attrs) row[a] = Field(full, a);
            rows.Add(row);
        }
        return rows;
    }

    static object? Field(Dictionary<string, object?> obj, string attr)
    {
        var v = obj.FirstOrDefault(kv => string.Equals(kv.Key, attr, StringComparison.OrdinalIgnoreCase)).Value;
        if (MultiAttrs.Contains(attr))
            return v switch
            {
                null => null,
                string s => s == "" ? null : new[] { s },
                System.Collections.IEnumerable e => e.Cast<object>().Select(x => x.ToString()!).ToArray() is { Length: > 0 } arr ? arr : null,
                _ => new[] { v.ToString()! },
            };
        if (attr.Equals("objectSid", StringComparison.OrdinalIgnoreCase)) // ADODB returns the SID as binary
            return v is string sid && sid != "" ? AdProvider.SidToBytes(sid) : null;
        return v switch
        {
            string s when s == "" => null,
            System.Collections.ICollection { Count: 0 } => null,
            _ => v,
        };
    }
}

public sealed class SimDirectory(SimState state) : IDirectory
{
    public string WhoAmI() => state.Data.Str("whoami") ?? "";

    public string RootDn() => state.Data.Str("root") ?? "";

    public List<Row> Query(string searchBase, string filter, IReadOnlyList<string> attrs, string scope = "subtree")
    {
        var objects = (state.Data.Obj("objects") ?? []).Select(kv =>
            KeyValuePair.Create(kv.Key, (kv.Value as JsonObject ?? []).ToDictionary(a => a.Key, a => SimState.Clr(a.Value))));
        return SimQuery.QueryObjects(objects, searchBase, filter, attrs, scope);
    }
}

public sealed class SimFs(SimState state, string shareDir) : IFilesystem
{
    public HashSet<string> Links { get; } = [];

    public string ToUnc(string path)
    {
        var p = path.TrimEnd('\\', '/');
        if (p == "") p = path;
        if (p.StartsWith(@"\\")) return p;
        var drives = (state.Data.Obj("drives") ?? []).ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => (string)kv.Value!);
        if (p.Length >= 2 && drives.TryGetValue(p[..2].ToUpperInvariant(), out var unc)) return unc + p[2..];
        throw new ArgumentException($"Neither UNC nor a mapped drive: {path}");
    }

    string DirOf(string rel) => rel != "" ? Path.Combine([shareDir, .. rel.Split('\\')]) : shareDir;

    public (bool Protected, List<RawAce> Aces) ReadDacl(string rel)
    {
        if (!Directory.Exists(DirOf(rel))) throw new DirectoryNotFoundException($"Folder missing in the sim: {DirOf(rel)}");
        var entry = state.Acls().GetValueOrDefault(rel.ToLowerInvariant());
        var ex = (entry.Arr("aces") ?? []).Select(a => new RawAce((int)a![0]!, (int)a[1]!, (uint)(long)a[2]!, (string)a[3]!)).ToList();
        var prot = entry.Bool("protected") ?? false;
        if (prot || rel == "") return (prot, ex);
        var parent = rel.Contains('\\') ? rel[..rel.LastIndexOf('\\')] : "";
        var inherited = ReadDacl(parent).Aces.Where(a => (a.Flags & M.ContainerInherit) != 0)
            .Select(a => a with { Flags = (a.Flags & ~M.InheritOnly) | M.InheritedAce }); // the entry applies at the subfolder
        return (prot, [.. ex, .. inherited]);
    }

    /// <summary>The folder as it really is on disk; refuse if any link makes it land elsewhere (same check as Win32Fs).</summary>
    string Verified(string rel)
    {
        var p = DirOf(rel);
        if (!Directory.Exists(p)) throw new DirectoryNotFoundException($"Folder missing in the sim: {p}");
        var cur = shareDir;
        foreach (var part in rel == "" ? [] : rel.Split('\\'))
        {
            cur = Path.Combine(cur, part);
            var di = new DirectoryInfo(cur);
            if (di.LinkTarget is not null || di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException($"{rel} goes through a link; owlseye does not write there");
        }
        return p;
    }

    public void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces)
    {
        Verified(rel);
        var data = (JsonObject)state.Data.DeepClone();
        var acls = data.Obj("acls") ?? [];
        var keep = new JsonObject();
        foreach (var (k, v) in acls)
            if (!string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)) keep[k] = v?.DeepClone();
        keep[rel] = new JsonObject
        {
            ["protected"] = isProtected,
            ["aces"] = new JsonArray(aces.Select(a => (JsonNode)new JsonArray(a.Type, a.Flags, (long)a.Mask, a.Sid)).ToArray()),
        };
        data["acls"] = keep;
        state.Save(data);
    }

    public bool Exists(string rel) => Directory.Exists(DirOf(rel));

    public void Mkdir(string rel)
    {
        Verified(rel.Contains('\\') ? rel[..rel.LastIndexOf('\\')] : "");
        var p = DirOf(rel);
        if (Directory.Exists(p) || File.Exists(p)) throw new IOException($"{rel} already exists");
        Directory.CreateDirectory(p);
        try
        {
            Verified(rel);
        }
        catch (UnauthorizedAccessException) // landed elsewhere (a link appeared in between): take it back
        {
            Directory.Delete(p);
            throw;
        }
    }

    public (string Name, string Domain, int Use) LookupSid(string sid)
    {
        if ((state.Data.Obj("sids") ?? []).TryGetPropertyValue(sid, out var hit) && hit is JsonArray a)
            return ((string)a[0]!, (string)a[1]!, (int)a[2]!);
        throw new KeyNotFoundException(sid);
    }

    public List<string> Subdirs(string rel)
    {
        var names = new List<string>();
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        foreach (var e in new DirectoryInfo(DirOf(rel)).EnumerateFileSystemInfos("*", opts))
        {
            if (e.Name.StartsWith('.') || e.Name.Contains('\\')) continue;
            if (e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                Links.Add(rel != "" ? $"{rel}\\{e.Name}" : e.Name);
            else if (e is DirectoryInfo) names.Add(e.Name);
        }
        return names;
    }
}

public sealed class SimProvider : AdProvider
{
    public const string ShareDir = "share";

    public SimProvider(string simDir, int scanDepth = 0) : this(new SimState(simDir), simDir, scanDepth) { }

    SimProvider(SimState state, string simDir, int scanDepth)
        : base(new SimFs(state, Path.Combine(simDir, ShareDir)), new SimDirectory(state),
            state.Data.Obj("config").Str("share") ?? "", scanDepth)
    {
    }

    public override string Name => "sim";
}

/// <summary>Creates a sim directory: demo share -> state.json (raw AD attributes) + folder tree.
/// Derived things (inherited ACEs, accounts in the ACLs, group resolution) deliberately are not in
/// state.json; the emulation or AdProvider computes them as on Windows.</summary>
public static class SimSeed
{
    const long Universal = -2147483640; // security group, universal
    const int UacNormal = 0x200, UacDisabled = 0x2;
    static readonly Dictionary<string, int> SidType = new() { ["user"] = 1, ["group"] = 2, ["wellknown"] = 5 };

    public static JsonObject ToState(Snapshot snap, Dictionary<string, (string Name, string Kind)> names, string root, string whoami)
    {
        var objects = new JsonObject();
        foreach (var u in snap.Users.Values)
            objects[u.Dn] = new JsonObject
            {
                ["objectClass"] = new JsonArray("top", "person", "organizationalPerson", "user"),
                ["sAMAccountName"] = u.Sam,
                ["displayName"] = u.Display,
                ["userAccountControl"] = UacNormal | (u.Enabled ? 0 : UacDisabled),
                ["objectSid"] = u.Sid,
            };
        foreach (var g in snap.Groups.Values)
            objects[g.Dn] = new JsonObject
            {
                ["objectClass"] = new JsonArray("top", "group"),
                ["sAMAccountName"] = g.Sam,
                ["groupType"] = Universal,
                ["member"] = new JsonArray(g.Members.Order(StringComparer.Ordinal).Select(m => (JsonNode)m!).ToArray()),
                ["objectSid"] = g.Sid,
            };
        // The file server resolves every known SID, not only those already in an ACL
        var sids = new JsonObject();
        foreach (var (sid, (name, kind)) in names)
        {
            var i = name.LastIndexOf('\\');
            var dom = i >= 0 ? name[..i] : "";
            var shortName = name[(i + 1)..];
            sids[sid] = new JsonArray(shortName, dom, SidType.GetValueOrDefault(kind, 5));
        }
        var acls = new JsonObject();
        foreach (var (path, f) in snap.Folders)
            acls[path] = new JsonObject
            {
                ["protected"] = f.Protected,
                ["aces"] = new JsonArray(f.Explicit.Select(a => (JsonNode)new JsonArray(a.Allow ? 0 : 1, a.Flags, a.Mask, a.Sid)).ToArray()),
            };
        return new JsonObject
        {
            ["root"] = root,
            ["whoami"] = whoami,
            ["config"] = new JsonObject { ["share"] = snap.Share },
            ["drives"] = new JsonObject { ["S:"] = snap.Share },
            ["objects"] = objects,
            ["sids"] = sids,
            ["acls"] = acls,
        };
    }

    /// <summary>Create sim directory from the demo share. Never overwrites an existing state.json.</summary>
    public static string Seed(string simDir)
    {
        var state = Path.Combine(simDir, SimState.FileName);
        if (File.Exists(state)) throw new IOException($"{state} already exists");
        var (snap, names) = Demo.Build();
        var json = ToState(snap, names, Demo.Base, $"{Demo.Domain}\\admin (Sim)");
        var share = Path.Combine(simDir, SimProvider.ShareDir);
        foreach (var path in snap.Folders.Keys)
            Directory.CreateDirectory(path != "" ? Path.Combine([share, .. path.Split('\\')]) : share);
        SimState.Write(state, json);
        return simDir;
    }
}
