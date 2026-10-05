// Changeset (cells, inheritance, new folders) -> ACL operations per folder + effect on users.
//
// R| is maintained automatically: when an account gets a right on a folder, it gets R| on every
// parent folder up to the root where it otherwise has nothing. When the last right below goes away,
// that R| goes away too (only then; an R| set by hand stays).

using System.Text.Json.Serialization;
using Owlseye.Ui;

namespace Owlseye;

public sealed class PlanError(string message) : Exception(message);

public sealed record Change(string Sid, string Name, string Path, string? Before, string? After, bool Auto = false);

public sealed record AclOp(
    string Path,
    bool ProtectedBefore,
    bool ProtectedAfter,
    IReadOnlyList<Ace> Before, // explicit ACEs before
    IReadOnlyList<Ace> After, // explicit ACEs after
    IReadOnlyList<Change> Changes,
    bool NewFolder = false,
    bool Cleared = false); // default restored: inherits, no own entries

public sealed record Impact(string User, string Display, string Path, string? Before, string? After);

/// <summary>A right a change removes that the matrix does not show (see Planner.HiddenLosses).</summary>
public sealed record HiddenLoss(string Name, string Path, string Before, string After, string Lost);

public sealed class Plan
{
    public required List<AclOp> AclOps { get; init; }
    public required List<string> CreateOps { get; init; }
    public required List<Impact> Impact { get; init; }
    [JsonIgnore] public required Snapshot SnapAfter { get; init; }

    /// <summary>Rights.Matrix(SnapAfter), computed once by the planner.</summary>
    [JsonIgnore] public required Dictionary<(string Sid, string Path), Cell> CellsAfter { get; init; }
    public Dictionary<(string Sid, string Path), string?> Auto { get; init; } = [];

    public bool Empty => AclOps.Count == 0 && CreateOps.Count == 0;
}

public static class Planner
{
    /// <summary>Snapshot with cells set; SYSTEM/Administrators on changed protected folders. creatorOwner: path -> what
    /// Creator Owner gets there (Acl.SetCreatorOwner).</summary>
    public static Snapshot WithCells(Snapshot work, IEnumerable<KeyValuePair<(string Sid, string Path), string?>> values,
        IReadOnlySet<string> touched, IReadOnlyDictionary<string, string?>? creatorOwner = null)
    {
        var byPath = new Dictionary<string, List<(string Sid, string? V)>>();
        foreach (var ((sid, path), v) in values)
        {
            if (!byPath.TryGetValue(path, out var l)) byPath[path] = l = [];
            l.Add((sid, v));
        }
        var folders = new Dictionary<string, Folder>(work.Folders);
        var required = Rights.RequiredFullControl(work);
        foreach (var path in byPath.Keys.Union(touched).Union(creatorOwner?.Keys ?? []))
        {
            var f = folders[path];
            IEnumerable<Ace> ex = f.Explicit;
            foreach (var (sid, v) in byPath.GetValueOrDefault(path) ?? [])
                ex = Acl.SetCell(ex, work.Principals[sid], v);
            if (creatorOwner is not null && creatorOwner.TryGetValue(path, out var co)) ex = Acl.SetCreatorOwner(ex, co);
            if (f.Protected || f.Level == 0) ex = Acl.EnsureAdmins(ex, required, f.Aces.Where(a => a.Inherited));
            folders[path] = f with { Aces = [.. Acl.Canonical(ex), .. f.Aces.Where(a => a.Inherited)] };
        }
        return work with { Folders = folders };
    }

    static HashSet<string> Protect(Snapshot snap, Dictionary<string, Folder> folders, Dictionary<string, bool> changes, List<string> created)
    {
        var touched = new HashSet<string>();
        foreach (var (path, protect) in changes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            folders.TryGetValue(path, out var f);
            if (created.Contains(path) && f is not null && Acl.CanToggle(f))
            {
                // New folder (e.g. restored): protected means only own entries + SYSTEM/Administrators
                folders[path] = f with { Protected = protect };
                touched.Add(path);
                continue;
            }
            if (f is null || !snap.Folders.ContainsKey(path) || !Acl.CanToggle(f))
                throw new PlanError($"Inheritance can only be changed on existing folders below the root: {Msg.Quote(path)}");
            if (f.OtherAces)
                throw new PlanError($"{path} has ACL entries of other types (e.g. conditional); owlseye does not rewrite it");
            if (f.Protected == protect) continue;
            if (protect && f.Aces.Any(a => a.Inherited && a.Kind == "unknown"))
                throw new PlanError($"{path} inherits an unresolved SID; fix that before breaking inheritance");
            if (protect) folders[path] = f with { Protected = true, Aces = Acl.AfterBreak(f) };
            else
            {
                var parent = f.Parent is not null ? snap.Folders.GetValueOrDefault(f.Parent) : null;
                folders[path] = f with { Protected = false, Aces = Acl.AfterRestore(f, parent) };
            }
            touched.Add(path);
        }
        return touched;
    }

    /// <summary>Add or remove R| on parent folders (see file comment).</summary>
    static Dictionary<(string Sid, string Path), string?> Traverse(Dictionary<(string Sid, string Path), Explicit> before,
        Snapshot after, Dictionary<(string Sid, string Path), string?> want, RightsCache? cache)
    {
        var cells = Rights.Matrix(after);
        var members = cache is not null && cache.SameMembers(after);
        var tg = members ? cache!.Transitive : Rights.Transitive(after);
        var ur = members ? cache!.UserRightsAfter(after, cells) : Rights.UserRights(after, cells: cells, userSids: Rights.UserSids(after, tg));
        var auto = new Dictionary<(string, string), string?>();
        foreach (var ((sid, path), v) in want.OrderBy(kv => kv.Key.Sid, StringComparer.Ordinal).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal))
        {
            if (v is null) continue;
            foreach (var anc in M.Ancestors(path))
                if (after.Folders.ContainsKey(anc) && !want.ContainsKey((sid, anc)) && !Rights.Covered(after, cells, sid, anc, ur, tg))
                    auto[(sid, anc)] = "R|";
        }
        var table = before.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.Value);
        foreach (var (k, v) in want) table[k] = v;

        bool Below(string sid, string anc)
        {
            var prefix = anc != "" ? M.Lower(anc) + "\\" : "";
            return table.Any(kv => kv.Key.Sid == sid && kv.Value is not null && kv.Key.Path != anc && M.Lower(kv.Key.Path).StartsWith(prefix, StringComparison.Ordinal));
        }

        foreach (var ((sid, path), v) in want.OrderBy(kv => -M.LevelOf(kv.Key.Path)))
        {
            if (v is not null || !before.ContainsKey((sid, path))) continue;
            foreach (var anc in M.Ancestors(path))
            {
                if (!before.TryGetValue((sid, anc), out var e)) continue;
                if (e.Value != "R|" || !e.Standard || want.ContainsKey((sid, anc)) || Below(sid, anc)) break;
                auto[(sid, anc)] = table[(sid, anc)] = null;
            }
        }
        return auto;
    }

    /// <summary>changes: (sid, path) -> desired explicit entry (null, R|, R, W|, W).
    /// folders: path -> inheritance broken (true) or restored (false).
    /// maxLevel: matrix depth; limits only new folders (cells and inheritance work everywhere, also for cleanup).
    /// newFolders: path -> parent. extra: accounts not yet in any ACL (new columns).
    /// clear: reset folder to the default (inherits, no own entries, also no Deny/special entries);
    /// takes precedence over cells and inheritance of the same folder.</summary>
    public static Plan Build(
        Snapshot snap,
        IReadOnlyDictionary<(string Sid, string Path), string?> changesIn,
        IReadOnlyDictionary<string, bool>? foldersIn = null,
        int maxLevel = 3,
        IReadOnlyDictionary<string, string>? newFolders = null,
        IReadOnlyDictionary<string, Principal>? extra = null,
        IEnumerable<string>? clearIn = null,
        RightsCache? cache = null)
    {
        if (cache is not null && !ReferenceEquals(cache.Snap, snap)) cache = null;
        List<string> createOps;
        try
        {
            createOps = Create.Plan(snap, newFolders ?? new Dictionary<string, string>(), maxLevel);
        }
        catch (ArgumentException e)
        {
            throw new PlanError(e.Message);
        }
        var clear = (clearIn ?? []).ToHashSet();
        var folders = new Dictionary<string, bool>(foldersIn ?? new Dictionary<string, bool>());
        var changes = new Dictionary<(string Sid, string Path), string?>(changesIn);
        foreach (var path in clear)
        {
            if (!snap.Folders.TryGetValue(path, out var f) || !Acl.CanToggle(f))
                throw new PlanError($"Only existing folders below the root can be cleared: {Msg.Quote(path)}");
            if (f.OtherAces)
                throw new PlanError($"{path} has ACL entries of other types or cannot be read; owlseye does not rewrite it");
            folders.Remove(path);
            if (f.Protected) folders[path] = false;
            changes = changes.Where(kv => kv.Key.Path != path).ToDictionary();
            foreach (var (sid, p) in Rights.ExplicitCells(snap).Keys)
                if (p == path) changes[(sid, p)] = null;
        }
        var principals = new Dictionary<string, Principal>(snap.Principals);
        foreach (var (k, v) in extra ?? new Dictionary<string, Principal>()) principals[k] = v;
        var workFolders = new Dictionary<string, Folder>(snap.Folders);
        foreach (var path in createOps) workFolders[path] = new Folder(path, M.LevelOf(path));
        var touched = Protect(snap, workFolders, folders, createOps);
        touched.UnionWith(createOps);
        touched.UnionWith(clear);
        var work = snap with { Folders = workFolders, Principals = principals };

        var required = Rights.RequiredFullControl(work);
        foreach (var ((sid, path), v) in changes)
        {
            if (sid is M.OwnerRights) throw new PlanError("Owner Rights is not set in owlseye");
            if (sid is M.CreatorOwner) // not a column: set in the folder panel, entries for subfolders and files only
            {
                if (v is not (null or "W" or "F")) throw new PlanError("Creator Owner can have Modify (W), full control (F) or nothing");
                if (!work.Folders.TryGetValue(path, out var cf)) throw new PlanError($"Unknown folder {Msg.Quote(path)}");
                if (cf.OtherAces) throw new PlanError($"{path} has ACL entries of other types (e.g. conditional); owlseye does not rewrite it");
                continue;
            }
            if (M.Hides(sid, principals.GetValueOrDefault(sid)?.Name))
                throw new PlanError("SYSTEM, Administrators, Domain Admins and the other hidden accounts can be set only "
                    + "while the matrix shows them (\"Hidden accounts\" above the matrix)");
            if (!principals.ContainsKey(sid)) throw new PlanError($"Unknown account {sid}");
            if (!work.Folders.TryGetValue(path, out var f)) throw new PlanError($"Unknown folder {Msg.Quote(path)}");
            // what EnsureAdmins would add again: a full-control account keeps full control on the root (unless inherited)
            // and on folders with broken inheritance
            if (v != "F" && required.Any(p => p.Sid == sid)
                && (f.Protected || (f.Level == 0 && !Rights.HasFullControl(f.Aces.Where(a => a.Inherited), sid))))
                throw new PlanError($"{principals[sid].Short} keeps full control on {(path != "" ? path : "the root")}: it is one "
                    + "of the accounts that must have full control (Settings)");
            if (v is not null && !M.Cells.Contains(v)) throw new PlanError($"Right must be one of {string.Join(", ", M.Cells)} or empty");
            if (v is not null && principals[sid].Kind == "unknown")
                throw new PlanError($"{principals[sid].Name} cannot be resolved (deleted or foreign account?); owlseye does not give it rights");
            if (f.OtherAces)
                throw new PlanError($"{path} has ACL entries of other types (e.g. conditional); owlseye does not rewrite it");
        }

        var creatorOwner = changes.Where(kv => kv.Key.Sid == M.CreatorOwner && Rights.CreatorOwnerOf(work.Folders[kv.Key.Path]) != kv.Value)
            .ToDictionary(kv => kv.Key.Path, kv => kv.Value);
        changes = changes.Where(kv => kv.Key.Sid != M.CreatorOwner).ToDictionary();
        var before = Rights.ExplicitCells(work);

        bool Differs((string, string) k, string? v) => // same value on special entry: normalize
            before.TryGetValue(k, out var e) ? e.Value != v || !e.Standard : v is not null;

        var want = changes.Where(kv => Differs(kv.Key, kv.Value)).ToDictionary();
        var auto = Traverse(before, WithCells(work, want, touched, creatorOwner), want, cache);
        var merged = new Dictionary<(string Sid, string Path), string?>(want);
        foreach (var (k, v) in auto) merged[k] = v;
        var final = WithCells(work, merged, touched, creatorOwner);
        if (clear.Count > 0) // what is still explicit after removing the cells (Deny, SYSTEM etc.) is dropped too
        {
            var fs = new Dictionary<string, Folder>(final.Folders);
            foreach (var p in clear) fs[p] = fs[p] with { Aces = fs[p].Aces.Where(a => a.Inherited).ToList() };
            final = final with { Folders = fs };
        }

        var old = Rights.ExplicitCells(snap);
        var newCells = Rights.ExplicitCells(final);
        var paths = want.Keys.Select(k => k.Path).Union(auto.Keys.Select(k => k.Path)).Union(touched).Union(creatorOwner.Keys)
            .OrderBy(M.LevelOf).ThenBy(M.Lower, M.Ci).ToList();
        var ops = new List<AclOp>();
        foreach (var path in paths)
        {
            if (path != "" && path.Split('\\').Any(M.BadComponent)) // already in the preview, not only when writing
                throw new PlanError(
                    $"owlseye does not write ACLs on {Msg.Quote(path)} (name ends with a dot or space, or has control characters)");
            var f0 = snap.Folders.GetValueOrDefault(path);
            var f1 = final.Folders[path];
            IReadOnlyList<Ace> b = f0 is not null ? f0.Explicit : [];
            var a = f1.Explicit;
            var prot0 = f0?.Protected ?? false;
            if (prot0 == f1.Protected && Acl.SameKeys(b, a)) continue; // also new folders without own entries: creating is enough
            var sids = old.Keys.Where(k => k.Path == path).Select(k => k.Sid)
                .Union(newCells.Keys.Where(k => k.Path == path).Select(k => k.Sid))
                .OrderBy(s => principals.TryGetValue(s, out var p) ? M.Lower(p.Short) : s, M.Ci);
            var ch = new List<Change>();
            foreach (var sid in sids)
            {
                var v0 = old.GetValueOrDefault((sid, path));
                var v1 = newCells.GetValueOrDefault((sid, path));
                if (v0?.Value != v1?.Value || (v0 is not null && v1 is not null && v0 != v1))
                {
                    var name = principals.TryGetValue(sid, out var p) ? p.Name : sid;
                    ch.Add(new Change(sid, name, path, v0?.Value, v1?.Value, auto.ContainsKey((sid, path))));
                }
            }
            var co0 = f0 is not null ? Rights.CreatorOwnerOf(f0) : null;
            var co1 = Rights.CreatorOwnerOf(f1);
            if (co0 != co1) ch.Add(new Change(M.CreatorOwner, M.CreatorOwnerName, path, co0, co1));
            ops.Add(new AclOp(path, prot0, f1.Protected, b, a, ch, f0 is null, clear.Contains(path)));
        }
        var cellsAfter = Rights.Matrix(final);
        return new Plan
        {
            AclOps = ops, CreateOps = createOps, Impact = ImpactOf(snap, final, null, cache, cellsAfter), SnapAfter = final,
            CellsAfter = cellsAfter, Auto = auto,
        };
    }

    public static List<Impact> ImpactOf(Snapshot before, Snapshot after, int? maxLevel = null, RightsCache? cache = null,
        Dictionary<(string Sid, string Path), Cell>? cellsAfter = null)
    {
        var useCache = cache is not null && maxLevel is null && ReferenceEquals(cache.Snap, before) && cache.SameMembers(after);
        var b = useCache ? cache!.UserRights : Rights.UserRights(before, maxLevel);
        var a = useCache ? cache!.UserRightsAfter(after, cellsAfter ?? Rights.Matrix(after)) : Rights.UserRights(after, maxLevel, cellsAfter);
        var o = new List<Impact>();
        foreach (var (udn, u) in before.Users)
        {
            var rb = b.GetValueOrDefault(udn) ?? [];
            var ra = a.GetValueOrDefault(udn) ?? [];
            if (ReferenceEquals(rb, ra)) continue; // not affected (RightsCache.UserRightsAfter keeps the scan's dictionary)
            foreach (var (path, r) in rb)
                if (ra.GetValueOrDefault(path) != r) o.Add(new Impact(u.Sam, u.Display, path, r, ra.GetValueOrDefault(path)));
            foreach (var (path, r) in ra)
                if (!rb.ContainsKey(path)) o.Add(new Impact(u.Sam, u.Display, path, null, r));
        }
        return o.OrderBy(i => M.Lower(i.Path), M.Ci).ThenBy(i => i.User, StringComparer.Ordinal).ThenBy(i => i.Path, StringComparer.Ordinal).ToList();
    }

    public static bool Gained(Impact i) => M.Rank(i.After) > M.Rank(i.Before);

    /// <summary>Full-control entries owlseye adds on a folder it writes because config.json "full_control" requires them
    /// (Acl.EnsureAdmins): accounts of `required` with full control after, not before, and not a cell set by hand.</summary>
    public static List<Ace> AddedFullControl(AclOp op, IEnumerable<Principal> required)
    {
        static bool Full(Ace a) => a.Allow && (a.Mask & M.Full) == M.Full && (a.Flags & M.OiCi) == M.OiCi;
        var want = required.Select(p => p.Sid).ToHashSet();
        var had = op.Before.Where(Full).Select(a => a.Sid).ToHashSet();
        var changed = op.Changes.Select(c => c.Sid).ToHashSet();
        return op.After.Where(a => Full(a) && want.Contains(a.Sid) && !had.Contains(a.Sid) && !changed.Contains(a.Sid)).ToList();
    }

    /// <summary>Rights a change removes although the cell still grants something afterwards, beyond what the visible
    /// step (e.g. W -> R) explains: delete, change permissions, take ownership. Happens when a special entry (full
    /// control, Modify while W means "no delete", …) is replaced by a standard entry. The matrix and the user impact
    /// cannot show this, so the preview lists it.</summary>
    public static List<HiddenLoss> HiddenLosses(Plan plan)
    {
        const uint shown = M.Delete | M.DeleteChild | M.WriteDac | M.WriteOwner;
        var o = new List<HiddenLoss>();
        foreach (var op in plan.AclOps.Where(op => !op.NewFolder))
            foreach (var c in op.Changes.Where(c => c.After is not null && c.Sid != M.CreatorOwner))
            {
                var before = op.Before.Where(a => a.Sid == c.Sid && a.Allow).ToList();
                var after = op.After.Where(a => a.Sid == c.Sid && a.Allow).ToList();
                var visible = StdMask(c.Before) & ~StdMask(c.After);
                var lost = Specific(before) & ~Specific(after) & shown & ~visible;
                if (lost == 0) continue;
                var what = new List<string>();
                if ((lost & M.Delete) != 0) what.Add("delete");
                if ((lost & M.DeleteChild) != 0) what.Add("delete subfolders and files");
                if ((lost & M.WriteDac) != 0) what.Add("change permissions");
                if ((lost & M.WriteOwner) != 0) what.Add("take ownership");
                o.Add(new HiddenLoss(c.Name, c.Path, Labels.Describe(before), Labels.Describe(after), string.Join(", ", what)));
            }
        return o;

        static uint StdMask(string? v) => v is null ? 0 : M.Standard[v].Mask;

        static uint Specific(IEnumerable<Ace> aces) => aces.Aggregate(0u, (m, a) =>
        {
            var x = a.Mask;
            if ((x & 0x10000000) != 0) x |= M.Full; // GENERIC_ALL
            if ((x & 0x40000000) != 0) x |= 0x120116; // GENERIC_WRITE
            return m | x;
        });
    }
}
