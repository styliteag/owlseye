// Demo data: the file share of a made-up company (management, operations with a sales and a service team, HR, IT, a
// few programs), with a few typical legacy issues. Writes to memory only.

namespace Owlseye.Providers;

public static class Demo
{
    public const string Domain = "DEMO";
    public const string Base = "DC=demo,DC=local";
    public const string Share = @"\\fs01\Data";

    public static readonly (string Sam, string Display)[] Users =
    [
        ("aadams", "Alice Adams"), ("bbrown", "Ben Brown"), ("cclark", "Chloe Clark"), ("ddavis", "Daniel Davis"),
        ("eevans", "Emma Evans"), ("ffoster", "Felix Foster"), ("ggreen", "Grace Green"), ("hhill", "Henry Hill"),
        ("iirwin", "Ivy Irwin"), ("jjones", "Jack Jones"), ("kking", "Kate King"), ("llewis", "Liam Lewis"),
        ("mmoore", "Mia Moore"), ("nnelson", "Noah Nelson"), ("oowen", "Olivia Owen"),
    ];

    /// <summary>Universal security groups; members: users or groups (ordered like the Python dict).</summary>
    public static readonly (string Sam, string[] Members)[] Groups =
    [
        ("G-AllUsers", Users.Select(u => u.Sam).ToArray()),
        ("G-Management", ["cclark"]),
        ("G-Projects", ["ddavis"]),
        ("G-Operations", ["G-Sales-Lead", "G-Sales-Staff", "G-Service-Lead", "G-Service-Staff"]),
        ("G-Sales-Lead", ["aadams"]),
        ("G-Sales-QA", ["bbrown"]),
        ("G-Sales-Staff", ["eevans", "ffoster"]),
        ("G-Sales-Media", ["eevans", "ggreen"]),
        ("G-Service-Lead", ["hhill"]),
        ("G-Service-QA", ["iirwin"]),
        ("G-Service-Staff", ["jjones", "kking"]),
        ("G-HR", ["llewis"]),
        ("G-Transfer", ["mmoore", "nnelson"]),
        ("P-Payroll", ["nnelson", "cclark"]),
        ("G-IT", ["oowen"]),
        ("G-Interns", []),
    ];

    const string Ops = "Operations";

    static readonly (string Path, bool Protected, (string Group, string Cell)[] Cells)[] FolderList =
    [
        ("", true, [("G-AllUsers", "R|"), ("G-IT", "W")]),
        ("HR", true, [("G-HR", "W")]),
        ("Public", true, [("G-AllUsers", "W")]),
        (@"Public\Transfer", true, [("G-AllUsers", "W"), ("G-Transfer", "W")]),
        (Ops, true,
        [
            ("G-Management", "W"), ("G-Operations", "R|"), ("G-Sales-Lead", "R|"), ("G-Sales-QA", "R|"), ("G-Sales-Staff", "R|"),
            ("G-Sales-Media", "R|"), ("G-Service-Lead", "R|"), ("G-Service-Staff", "R|"),
        ]),
        ($@"{Ops}\Sales-Staff", false, [("G-Sales-Lead", "W"), ("G-Sales-Staff", "W")]),
        ($@"{Ops}\Sales-Staff\2025", false, []),
        ($@"{Ops}\Sales-Staff\2025\Offsite", false, []),
        ($@"{Ops}\Sales-Lead", true, [("G-Management", "W"), ("G-Sales-Lead", "W")]),
        ($@"{Ops}\Sales-QA", true, [("G-Management", "W"), ("G-Sales-Lead", "W"), ("G-Sales-QA", "W")]),
        ($@"{Ops}\Service-Staff", false, [("G-Service-Lead", "W"), ("G-Service-Staff", "W")]),
        ($@"{Ops}\Service-Lead", true, [("G-Management", "W"), ("G-Service-Lead", "W")]),
        ($@"{Ops}\QA", true, [("G-Management", "W"), ("G-Sales-Lead", "W"), ("G-Service-Lead", "W")]),
        ($@"{Ops}\Service-QA", true, [("G-Management", "W"), ("G-Service-Lead", "W"), ("G-Service-QA", "W")]),
        ($@"{Ops}\Sales-Media", true, [("G-Management", "W"), ("G-Sales-Media", "W")]),
        ("Programs", true, [("G-AllUsers", "R")]),
        (@"Programs\CRM", true, [("G-AllUsers", "W"), ("G-Projects", "W")]),
        (@"Programs\Payroll", true, [("P-Payroll", "W")]),
        (@"Programs\ERP", true, [("G-Projects", "W")]),
    ];

    public static readonly IReadOnlyDictionary<string, string> WellKnown = new Dictionary<string, string>
    {
        [M.System] = "NT AUTHORITY\\SYSTEM",
        [M.Admins] = "BUILTIN\\Administrators",
        ["S-1-3-0"] = "\\CREATOR OWNER",
        ["S-1-1-0"] = "\\Everyone",
        ["S-1-5-11"] = "NT AUTHORITY\\Authenticated Users",
        ["S-1-5-32-545"] = "BUILTIN\\Users",
    };

    static readonly Ace[] AdminAces =
    [
        new(M.System, "NT AUTHORITY\\SYSTEM", "wellknown", M.Full, Flags: M.OiCi),
        new(M.Admins, "BUILTIN\\Administrators", "wellknown", M.Full, Flags: M.OiCi),
    ];

    static string Display(string sam) => Users.First(u => u.Sam == sam).Display;

    public static string Udn(string sam) => $"CN={Display(sam)},OU=Staff,{Base}";

    public static string Gdn(string sam) => $"CN={sam},OU=Groups,{Base}";

    static int GroupIndex(string sam) => Array.FindIndex(Groups, g => g.Sam == sam);

    public static bool IsGroup(string sam) => GroupIndex(sam) >= 0;

    public static string Gsid(string sam) => $"S-1-5-21-1-2-3-{2000 + GroupIndex(sam)}";

    public static string Usid(string sam) => $"S-1-5-21-1-2-3-{1100 + Array.FindIndex(Users, u => u.Sam == sam)}";

    static Ace AceOf(string sam, string value)
    {
        var (mask, flags) = M.Standard[value];
        return new Ace(Gsid(sam), $"{Domain}\\{sam}", "group", mask, Flags: flags);
    }

    /// <summary>Recompute inherited ACEs like NTFS (CI entries of the parent folder, without protected folders).</summary>
    public static Dictionary<string, Folder> WithInherited(IReadOnlyDictionary<string, Folder> folders)
    {
        var o = new Dictionary<string, Folder>();
        foreach (var f in folders.Values.OrderBy(f => f.Level))
        {
            var ex = f.Explicit;
            var parent = f.Parent is not null ? o.GetValueOrDefault(f.Parent) : null;
            IEnumerable<Ace> inherited = [];
            if (parent is not null && !f.Protected)
                inherited = parent.Aces.Where(a => (a.Flags & M.ContainerInherit) != 0)
                    .Select(a => a with { Inherited = true, Flags = (a.Flags & ~M.InheritOnly) | M.InheritedAce }).ToList();
            o[f.Path] = f with { Aces = [.. ex, .. inherited] };
        }
        return o;
    }

    /// <summary>Snapshot without columns (scan computes them from the ACLs) and the SID table (sid -> (name, kind)).</summary>
    public static (Snapshot Snap, Dictionary<string, (string Name, string Kind)> Names) Build()
    {
        var users = Users.ToDictionary(u => Udn(u.Sam), u => new User(Udn(u.Sam), u.Sam, u.Display, true, Usid(u.Sam)));
        var groups = Groups.ToDictionary(g => Gdn(g.Sam),
            g => new Group(Gdn(g.Sam), g.Sam, g.Members.Select(m => IsGroup(m) ? Gdn(m) : Udn(m)).ToHashSet(), Gsid(g.Sam)));
        var aces = new Dictionary<string, List<Ace>>();
        var folders = new Dictionary<string, Folder>();
        foreach (var (path, prot, cells) in FolderList)
        {
            aces[path] = [.. prot ? AdminAces : [], .. cells.Select(c => AceOf(c.Group, c.Cell))];
            folders[path] = new Folder(path, M.LevelOf(path), prot);
        }

        // legacy issues for the findings
        aces[@"Public\Transfer"].Add(new Ace(Usid("mmoore"), $"{Domain}\\mmoore", "user", M.Standard["W"].Mask, Flags: M.OiCi));
        aces["Public"].Add(new Ace("S-1-5-11", WellKnown["S-1-5-11"], "wellknown", M.Standard["R"].Mask, Flags: M.OiCi));
        aces["Programs"].Add(new Ace("S-1-5-21-1-2-3-4711", "S-1-5-21-1-2-3-4711", "unknown", 0x1200A9, Flags: M.OiCi));
        aces[@"Programs\Payroll"].Add(new Ace(Gsid("G-IT"), $"{Domain}\\G-IT", "group", M.Full, Flags: M.OiCi));
        aces[@"Programs\ERP"].RemoveAll(a => a.Sid == M.Admins);
        aces[$@"{Ops}\Sales-Staff\2025\Offsite"].Add(AceOf("G-Interns", "W"));
        foreach (var (p, f) in folders.ToList()) folders[p] = f with { Aces = aces[p] };

        var names = WellKnown.ToDictionary(kv => kv.Key, kv => (kv.Value, "wellknown"));
        foreach (var g in groups.Values) names[g.Sid] = ($"{Domain}\\{g.Sam}", "group");
        foreach (var u in users.Values) names[u.Sid] = ($"{Domain}\\{u.Sam}", "user");
        var snap = new Snapshot(Share, WithInherited(folders), [], groups, users);
        return (snap, names);
    }

    public static Dictionary<string, Principal> PrincipalsOf(Snapshot snap, Dictionary<string, (string Name, string Kind)> names)
    {
        var dns = new Dictionary<string, string>();
        foreach (var g in snap.Groups.Values) dns[g.Sid] = g.Dn;
        foreach (var u in snap.Users.Values) dns[u.Sid] = u.Dn;
        var o = new Dictionary<string, Principal>();
        foreach (var f in snap.Folders.Values)
            foreach (var a in f.Aces)
                if (!M.IsHidden(a.Sid, a.Name) && (!a.Inherited || f.Level == 0))
                {
                    var (name, kind) = names.TryGetValue(a.Sid, out var n) ? n : (a.Sid, "unknown");
                    o[a.Sid] = new Principal(a.Sid, name, kind, dns.GetValueOrDefault(a.Sid, ""));
                }
        return o;
    }
}

public sealed class DemoProvider : IProvider
{
    Snapshot state;
    readonly Dictionary<string, (string Name, string Kind)> names;

    public DemoProvider() => (state, names) = Demo.Build();

    public string Name => "demo";

    public string Share => Demo.Share;

    public string WhoAmI() => $"{Demo.Domain}\\admin (Demo)";

    public Snapshot Scan(Progress? progress = null) // instant: nothing to report
    {
        var snap = state with
        {
            Folders = new Dictionary<string, Folder>(state.Folders),
            Groups = state.Groups.ToDictionary(kv => kv.Key, kv => kv.Value with { Members = [.. kv.Value.Members] }),
            Users = new Dictionary<string, User>(state.Users),
        };
        return snap with { Principals = Demo.PrincipalsOf(snap, names), TakenAt = Clock.Now() };
    }

    public (bool Protected, List<Ace> Aces) FolderAcl(string path)
    {
        var f = state.Folders[path];
        return (f.Protected, [.. f.Aces]);
    }

    void Set(Dictionary<string, Folder> folders) => state = state with { Folders = Demo.WithInherited(folders) };

    public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces)
    {
        if (!state.Folders.TryGetValue(path, out var f)) throw new UnauthorizedAccessException($"{path} does not exist");
        var ex = aces.Select(a => a with { Inherited = false }).ToList();
        Set(new Dictionary<string, Folder>(state.Folders) { [path] = f with { Protected = isProtected, Aces = ex } });
    }

    public bool FolderExists(string path) => state.Folders.ContainsKey(path);

    public void CreateFolder(string path)
    {
        if (state.Folders.ContainsKey(path)) throw new IOException(path);
        Set(new Dictionary<string, Folder>(state.Folders) { [path] = new Folder(path, M.LevelOf(path)) });
    }

    public Principal? ResolveAccount(string sid) =>
        names.TryGetValue(sid, out var n) ? new Principal(sid, n.Name, n.Kind) : new Principal(sid, sid, "unknown");

    public List<Principal> FindGroups(string q)
    {
        q = q.Trim().ToLowerInvariant();
        var hits = state.Groups.Values.Where(g => q != "" && g.Sam.ToLowerInvariant().StartsWith(q, StringComparison.Ordinal))
            .OrderBy(g => g.Sam, StringComparer.Ordinal);
        var o = hits.Select(g => new Principal(g.Sid, $"{Demo.Domain}\\{g.Sam}", "group", g.Dn)).ToList();
        foreach (var sid in new[] { "S-1-5-32-545", "S-1-5-11", "S-1-1-0" }) // built-in groups, see AdProvider.BuiltinChoices
        {
            var name = Demo.WellKnown[sid];
            if (q != "" && name[(name.LastIndexOf('\\') + 1)..].ToLowerInvariant().StartsWith(q, StringComparison.Ordinal))
                o.Add(new Principal(sid, name, "wellknown"));
        }
        return o;
    }
}
