// Rights computation from the ACLs: cells (account x folder), inheritance, effective user rights, findings.
//
// Computed only from the explicit entries and the inheritance that owlseye reproduces itself
// (same rule as NTFS: R/W pass into subfolders until one is protected; R|/W| do not). This allows
// repeating the same computation for the planned state before anything is written. Only exception:
// what the root inherits from above the share appears only as inherited ACE in the snapshot.

namespace Owlseye;

public sealed record Explicit(string Value, bool Standard);

public sealed record Cell(string? Direct, string? Effective, string? Source, bool Standard = true);

public sealed record Finding(string Severity, string Path, string Text);

/// <summary>What depends only on one scan (groups, users, cells): computed once per scan and handed to the planner,
/// which otherwise recomputes it for every plan. Only valid for exactly this snapshot instance.</summary>
public sealed class RightsCache
{
    public RightsCache(Snapshot snap, Dictionary<(string Sid, string Path), Cell>? cells = null)
    {
        Snap = snap;
        Cells = cells ?? Rights.Matrix(snap);
        Transitive = Rights.Transitive(snap);
        UserSids = Rights.UserSids(snap, Transitive);
        UserRights = Rights.UserRights(snap, null, Cells, UserSids);
    }

    public Snapshot Snap { get; }
    public Dictionary<(string Sid, string Path), Cell> Cells { get; }
    public Dictionary<string, HashSet<string>> Transitive { get; }
    public Dictionary<string, HashSet<string>> UserSids { get; }
    public Dictionary<string, Dictionary<string, string>> UserRights { get; }

    Dictionary<string, int>? usersPerGroup;

    /// <summary>Number of users Rights.MembersOf would return for the account, or null if unknown; without building the
    /// lists (the matrix shows the count in every column header and asks after every click).</summary>
    public int? MemberCount(Snapshot snap, string sid)
    {
        if (!SameMembers(snap) || !snap.Principals.TryGetValue(sid, out var p)) return Rights.MembersOf(snap, sid)?.Count;
        if (M.Everyone.Contains(sid)) return snap.Users.Count;
        if (p.Kind == "user") return p.Dn != "" && snap.Users.ContainsKey(p.Dn) ? 1 : null;
        if (p.Dn == "" || !snap.Groups.ContainsKey(p.Dn)) return null;
        if (usersPerGroup is null)
        {
            var counts = new Dictionary<string, int>();
            foreach (var u in snap.Users.Keys)
                foreach (var g in Transitive.GetValueOrDefault(u) ?? [])
                    counts[g] = counts.GetValueOrDefault(g) + 1;
            usersPerGroup = counts;
        }
        return usersPerGroup.GetValueOrDefault(p.Dn);
    }

    /// <summary>Groups and users are shared (same dictionaries) between a scan and every planned state derived from it.</summary>
    public bool SameMembers(Snapshot other) => ReferenceEquals(other.Groups, Snap.Groups) && ReferenceEquals(other.Users, Snap.Users);

    /// <summary>Rights.UserRights for a planned state derived from this scan: only users with an account whose cells
    /// changed are recomputed, the others are taken from the scan. Same result as Rights.UserRights(after).</summary>
    public Dictionary<string, Dictionary<string, string>> UserRightsAfter(Snapshot after, Dictionary<(string Sid, string Path), Cell> cellsAfter)
    {
        var changed = new HashSet<string>();
        foreach (var (k, c) in cellsAfter)
            if (!Cells.TryGetValue(k, out var b) || b.Effective != c.Effective) changed.Add(k.Sid);
        foreach (var (k, b) in Cells)
            if (b.Effective is not null && !cellsAfter.ContainsKey(k)) changed.Add(k.Sid);
        var o = new Dictionary<string, Dictionary<string, string>>(UserRights);
        if (changed.Count == 0) return o;
        var affected = UserSids.Where(kv => kv.Value.Overlaps(changed)).Select(kv => kv.Key).ToList();
        if (affected.Count == 0) return o;
        var bySid = new Dictionary<string, List<(string Path, string Right)>>();
        foreach (var ((sid, path), c) in cellsAfter)
            if (c.Effective is not null)
            {
                if (!bySid.TryGetValue(sid, out var l)) bySid[sid] = l = [];
                l.Add((path, c.Effective));
            }
        foreach (var udn in affected)
        {
            var per = new Dictionary<string, string>();
            foreach (var sid in UserSids[udn])
                foreach (var (path, r) in bySid.GetValueOrDefault(sid) ?? [])
                    per[path] = M.Stronger(per.GetValueOrDefault(path), r)!;
            o[udn] = per;
        }
        return o;
    }
}

public static class Rights
{
    public const string Above = "(above the share)"; // source of a right the root inherits from above
    public const uint WriteBits = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000000 | 0x10000000; // write, append, delete, GENERIC_*

    public static readonly IReadOnlyDictionary<string, string> Broad = new Dictionary<string, string>
    {
        ["S-1-1-0"] = "Everyone",
        ["S-1-5-11"] = "Authenticated Users",
        ["S-1-5-32-545"] = "BUILTIN\\Users",
    };

    static readonly string[] Reach = ["R|", "R", "W|", "W"]; // every right on a folder requires being able to reach it

    /// <summary>Explicit Allow ACEs of an account on a folder -> (cell value, exactly the standard entry?).</summary>
    public static (string? Value, bool Standard) Classify(IReadOnlyList<Ace> aces)
    {
        if (aces.Count == 0) return (null, true);
        var inheritable = aces.Where(a => (a.Flags & M.OiCi) != 0).ToList();
        IReadOnlyList<Ace> pool = inheritable.Count > 0 ? inheritable : aces;
        var write = pool.Any(a => (a.Mask & WriteBits) != 0);
        var value = inheritable.Count > 0 ? (write ? "W" : "R") : (write ? "W|" : "R|");
        var a0 = aces[0];
        var std = M.Standard[value];
        return (value, aces.Count == 1 && a0.Mask == std.Mask && (a0.Flags & ~0x10) == std.Flags);
    }

    /// <summary>(sid, path) -> explicit entry. Only Allow ACEs, without SYSTEM/Administrators/Creator Owner.</summary>
    public static Dictionary<(string Sid, string Path), Explicit> ExplicitCells(Snapshot snap)
    {
        var by = new Dictionary<(string, string), List<Ace>>();
        foreach (var (path, f) in snap.Folders)
            foreach (var a in f.Explicit)
                if (a.Allow && !M.Hidden.Contains(a.Sid))
                {
                    if (!by.TryGetValue((a.Sid, path), out var l)) by[(a.Sid, path)] = l = [];
                    l.Add(a);
                }
        var o = new Dictionary<(string, string), Explicit>();
        foreach (var (k, v) in by)
        {
            var (value, standard) = Classify(v);
            o[k] = new Explicit(value!, standard);
        }
        return o;
    }

    /// <summary>sid -> inheritable right that the root inherits from above the share.</summary>
    public static Dictionary<string, string> Outer(Snapshot snap)
    {
        if (!snap.Folders.TryGetValue("", out var root) || root.Protected) return [];
        var by = new Dictionary<string, List<Ace>>();
        foreach (var a in root.Aces)
            if (a.Inherited && a.Allow && !M.Hidden.Contains(a.Sid) && (a.Flags & M.OiCi) != 0)
            {
                if (!by.TryGetValue(a.Sid, out var l)) by[a.Sid] = l = [];
                l.Add(a);
            }
        var o = new Dictionary<string, string>();
        foreach (var (sid, aces) in by)
            if (Classify(aces).Value is { } v) o[sid] = v;
        return o;
    }

    /// <summary>(sid, path) -> cell, only where there is an entry or a right.</summary>
    public static Dictionary<(string Sid, string Path), Cell> Matrix(Snapshot snap)
    {
        var exp = ExplicitCells(snap);
        var own = new Dictionary<string, Dictionary<string, Explicit>>();
        foreach (var ((sid, path), e) in exp)
        {
            if (!own.TryGetValue(path, out var d)) own[path] = d = [];
            d[sid] = e;
        }
        var passes = new Dictionary<string, Dictionary<string, (string V, string Src)>>();
        var o = new Dictionary<(string, string), Cell>();
        Dictionary<string, (string, string)>? outer = null;
        foreach (var f in snap.Folders.Values.OrderBy(f => f.Level))
        {
            Dictionary<string, (string V, string Src)> incoming;
            if (f.Protected) incoming = [];
            else if (f.Parent is null)
                incoming = outer ??= Outer(snap).ToDictionary(kv => kv.Key, kv => (kv.Value, Above));
            else incoming = passes.GetValueOrDefault(f.Parent) ?? [];
            var mine = own.GetValueOrDefault(f.Path) ?? [];
            var passing = new Dictionary<string, (string V, string Src)>(incoming);
            foreach (var (sid, e) in mine)
            {
                var have = passing.TryGetValue(sid, out var p) ? p.V : null;
                if (e.Value is "R" or "W" && M.Rank(e.Value) > M.Rank(have)) passing[sid] = (e.Value, f.Path);
            }
            passes[f.Path] = passing;
            foreach (var sid in incoming.Keys.Union(mine.Keys))
            {
                var e = mine.GetValueOrDefault(sid);
                var d = e?.Value;
                string? inh = null, src = null;
                if (incoming.TryGetValue(sid, out var i)) (inh, src) = (i.V, i.Src);
                var (eff, esrc) = M.Rank(d) >= M.Rank(inh) ? (d, f.Path) : (inh, src);
                o[(sid, f.Path)] = new Cell(d, eff, esrc, e?.Standard ?? true);
            }
        }
        return o;
    }

    // --- Users ---------------------------------------------------------------------------------

    /// <summary>member_dn -> all group DNs it is a member of (also nested).</summary>
    public static Dictionary<string, HashSet<string>> Transitive(Snapshot snap)
    {
        var direct = new Dictionary<string, List<string>>();
        foreach (var (gdn, g) in snap.Groups)
            foreach (var m in g.Members)
            {
                if (!direct.TryGetValue(m, out var l)) direct[m] = l = [];
                l.Add(gdn);
            }
        // A walk per member, without memo: the Python version memoized results that a cycle had cut short (A in B, B in A),
        // so members of A lost B. Nesting is shallow, so walking it for every member is cheap.
        var o = new Dictionary<string, HashSet<string>>();
        foreach (var (p, groups) in direct)
        {
            var acc = new HashSet<string>();
            var todo = new Stack<string>(groups);
            while (todo.Count > 0)
            {
                var g = todo.Pop();
                if (g == p || !acc.Add(g)) continue;
                foreach (var up in direct.GetValueOrDefault(g) ?? []) todo.Push(up);
            }
            o[p] = acc;
        }
        return o;
    }

    /// <summary>user_dn -> SIDs through which the user can appear in ACLs (own, groups, Everyone etc.).</summary>
    public static Dictionary<string, HashSet<string>> UserSids(Snapshot snap, Dictionary<string, HashSet<string>>? tg = null)
    {
        tg ??= Transitive(snap);
        var byDn = snap.Groups.Values.Where(g => g.Sid != "").ToDictionary(g => g.Dn, g => g.Sid);
        var o = new Dictionary<string, HashSet<string>>();
        foreach (var (udn, u) in snap.Users)
        {
            var s = new HashSet<string> { u.Sid };
            s.UnionWith(M.Everyone);
            foreach (var g in tg.GetValueOrDefault(udn) ?? [])
                if (byDn.TryGetValue(g, out var gs)) s.Add(gs);
            s.Remove("");
            o[udn] = s;
        }
        return o;
    }

    /// <summary>Does the folder have something of its own (entries, broken inheritance, read error)? Below the
    /// matrix depth this is a deviation and is shown; folders that only inherit are not.</summary>
    public static bool Deviates(Folder f) =>
        f.Protected || f.Error != "" || f.OtherAces || f.Explicit.Any(a => !M.Hidden.Contains(a.Sid));

    /// <summary>user_dn -> {path: strongest effective right} (folders with access, up to maxLevel; null = all).</summary>
    public static Dictionary<string, Dictionary<string, string>> UserRights(Snapshot snap, int? maxLevel = null,
        Dictionary<(string Sid, string Path), Cell>? cells = null, Dictionary<string, HashSet<string>>? userSids = null)
    {
        cells ??= Matrix(snap);
        var bySid = new Dictionary<string, List<(string Path, string Right)>>();
        foreach (var ((sid, path), c) in cells)
            if (c.Effective is not null && (maxLevel is null || snap.Folders[path].Level <= maxLevel))
            {
                if (!bySid.TryGetValue(sid, out var l)) bySid[sid] = l = [];
                l.Add((path, c.Effective));
            }
        var o = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (udn, sids) in userSids ?? UserSids(snap))
        {
            var per = new Dictionary<string, string>();
            foreach (var sid in sids)
                foreach (var (path, r) in bySid.GetValueOrDefault(sid) ?? [])
                    per[path] = M.Stronger(per.GetValueOrDefault(path), r)!;
            o[udn] = per;
        }
        return o;
    }

    static string ShortOf(Snapshot snap, string sid) => snap.Principals.TryGetValue(sid, out var p) ? p.Short : sid;

    /// <summary>Through which accounts a user reaches `path`.</summary>
    public static List<string> Via(Snapshot snap, string userDn, string path, Dictionary<(string Sid, string Path), Cell>? cells = null,
        Dictionary<string, HashSet<string>>? userSids = null)
    {
        cells ??= Matrix(snap);
        var sids = (userSids ?? UserSids(snap)).GetValueOrDefault(userDn) ?? [];
        var o = new List<string>();
        foreach (var sid in sids.OrderBy(s => snap.Principals.TryGetValue(s, out var p) ? M.Lower(p.Short) : s, M.Ci))
        {
            if (cells.TryGetValue((sid, path), out var c) && c.Effective is not null)
            {
                var where = c.Source == path ? "here" : $"from {(string.IsNullOrEmpty(c.Source) ? "root" : c.Source)}";
                o.Add($"{ShortOf(snap, sid)} ({c.Effective}, {where})");
            }
        }
        return o;
    }

    /// <summary>User DNs that get rights through this account; null if owlseye does not know.</summary>
    public static List<string>? MembersOf(Snapshot snap, string sid, Dictionary<string, HashSet<string>>? tg = null)
    {
        if (!snap.Principals.TryGetValue(sid, out var p)) return null;
        if (M.Everyone.Contains(sid)) return snap.Users.Keys.OrderBy(x => x, M.Ci).ToList();
        // a "user" that is not among the users read (computer account, gMSA: SidTypeUser too) has no members owlseye knows
        if (p.Kind == "user") return p.Dn != "" && snap.Users.ContainsKey(p.Dn) ? [p.Dn] : null;
        if (p.Dn == "" || !snap.Groups.ContainsKey(p.Dn)) return null;
        tg ??= Transitive(snap);
        return snap.Users.Keys.Where(u => tg.TryGetValue(u, out var gs) && gs.Contains(p.Dn)).OrderBy(x => x, M.Ci).ToList();
    }

    // --- Blocked, findings -----------------------------------------------------------------------

    /// <summary>(sid, path) -> (right on the parent folder, parent folder) for accounts that get stuck at a folder with
    /// broken inheritance: inheritable right above, nothing here.</summary>
    public static Dictionary<(string Sid, string Path), (string Right, string Parent)> BlockedCells(Snapshot snap,
        Dictionary<(string Sid, string Path), Cell> cells)
    {
        var o = new Dictionary<(string, string), (string, string)>();
        var byPath = new Dictionary<string, List<(string Sid, Cell C)>>();
        foreach (var ((sid, p), c) in cells)
        {
            if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = [];
            l.Add((sid, c));
        }
        foreach (var (path, f) in snap.Folders)
        {
            if (!f.Protected || f.Level < 1) continue;
            foreach (var (sid, c) in byPath.GetValueOrDefault(f.Parent!) ?? [])
                if (c.Effective is "R" or "W" && !cells.ContainsKey((sid, path)))
                    o[(sid, path)] = (c.Effective, f.Parent!);
        }
        return o;
    }

    /// <summary>Does the account get into `path`? Own entry (also inherited) or: every member already has access there
    /// through another account (typically: G-AllUsers with R| on the root).</summary>
    public static bool Covered(Snapshot snap, Dictionary<(string Sid, string Path), Cell> cells, string sid, string path,
        Dictionary<string, Dictionary<string, string>> ur, Dictionary<string, HashSet<string>>? tg = null)
    {
        if (cells.ContainsKey((sid, path))) return true;
        var members = MembersOf(snap, sid, tg);
        return members is { Count: > 0 } && members.All(u => ur.TryGetValue(u, out var r) && r.ContainsKey(path));
    }

    /// <summary>(sid, parent folder) -> folders below where the account has rights but cannot get in (no R| above).</summary>
    public static Dictionary<(string Sid, string Path), List<string>> Unreachable(Snapshot snap,
        Dictionary<(string Sid, string Path), Cell> cells, int maxLevel = 3)
    {
        var ur = UserRights(snap, maxLevel, cells);
        var tg = Transitive(snap);
        var o = new Dictionary<(string, string), List<string>>();
        foreach (var ((sid, path), c) in cells)
        {
            snap.Principals.TryGetValue(sid, out var p);
            if (!Reach.Contains(c.Direct) || snap.Folders[path].Level > maxLevel || p is { Kind: "unknown" }) continue;
            foreach (var anc in M.Ancestors(path))
                if (snap.Folders.ContainsKey(anc) && !Covered(snap, cells, sid, anc, ur, tg))
                {
                    if (!o.TryGetValue((sid, anc), out var l)) o[(sid, anc)] = l = [];
                    l.Add(path);
                }
        }
        return o;
    }

    static readonly Dictionary<string, int> SevRank = new() { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };

    public static List<Finding> Findings(Snapshot snap, int maxLevel = 3, Dictionary<(string Sid, string Path), Cell>? cells = null)
    {
        cells ??= Matrix(snap);
        var o = new List<Finding>();
        foreach (var (path, f) in snap.Folders.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (f.Error != "") o.Add(new("high", path, $"owlseye cannot read this folder as administrator. {f.Error}"));
            if (M.SuspiciousName(f.Name))
                o.Add(new("medium", path, "Name has invisible, combining or mixed-script characters (lookalike?)"));
            if (f.Protected && f.Level > maxLevel) o.Add(new("medium", path, $"Inheritance broken below level {maxLevel}"));
            if (f.Protected || f.Level == 0)
            {
                var full = f.Explicit.Where(a => a.Allow && (a.Mask & M.Full) == M.Full && (a.Flags & M.OiCi) == M.OiCi)
                    .Select(a => a.Sid).ToHashSet();
                var missing = new[] { (M.System, "SYSTEM"), (M.Admins, "Administrators") }
                    .Where(x => !full.Contains(x.Item1)).Select(x => x.Item2).ToList();
                if (missing.Count > 0) o.Add(new("medium", path, $"{string.Join(" and ", missing)} without full control here"));
            }
            foreach (var a in f.Explicit)
            {
                if (M.Hidden.Contains(a.Sid)) continue;
                if (!a.Allow) o.Add(new("medium", path, $"Deny entry for {a.Name}"));
                else if (a.Kind == "unknown") o.Add(new("high", path, $"Unresolved SID {a.Sid}"));
                else if (Broad.ContainsKey(a.Sid) && f.Level > 0 && (a.Flags & M.OiCi) != 0)
                    o.Add(new("high", path, $"Broad permission for {Broad[a.Sid]}"));
                else if (a.Kind == "user") o.Add(new("high", path, $"Direct user entry for {a.Name}"));
                else if (f.Level > maxLevel) o.Add(new("medium", path, $"Explicit entry below level {maxLevel}: {a.Name}"));
                else if ((a.Flags & M.InheritOnly) != 0 && (a.Flags & M.OiCi) == 0)
                    o.Add(new("low", path, $"Entry for {a.Name} applies to nothing"));
            }
        }
        foreach (var ((sid, path), c) in cells)
            if (c.Direct is not null && !c.Standard && snap.Folders[path].Level <= maxLevel)
                o.Add(new("low", path, $"Non-standard entry for {ShortOf(snap, sid)} (shown as {c.Direct})"));
        foreach (var ((sid, anc), below) in Unreachable(snap, cells, maxLevel))
            o.Add(new("medium", anc, $"{ShortOf(snap, sid)} cannot open this folder to reach {below[0]} (R| missing)"));
        return o.OrderBy(x => SevRank[x.Severity]).ThenBy(x => M.Lower(x.Path), M.Ci).ThenBy(x => x.Text, M.Ci).ToList();
    }
}
