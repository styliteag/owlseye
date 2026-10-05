// Compare desired (baseline) against actual: outside changes to entries and to inheritance,
// on all scanned levels (regardless of how deep the matrix currently shows).
//
// Accept shifts the desired state. Discard puts the desired values into the matrix as pending changes;
// they then go through preview and apply normally (including automatic R|).
//
// Shares can have tens of thousands of folders and as many desired cells: everything here is linear in the number of
// cells and items (lookups by sid and lowercased path), never one pass over all cells per item.

using System.Text.Json.Serialization;

namespace Owlseye;

/// <summary>change: added | removed | changed (entry); broken | restored (inheritance); folder_gone (folder missing).
/// before = desired, after = actual.</summary>
public sealed record DriftItem(string Change, string Sid = "", string Name = "", string Path = "", string? Before = null, string? After = null)
{
    [JsonIgnore] public string Key => $"{Change}|{Sid}|{Path}";
}

/// <summary>Pending changes that restore the desired state (go through preview and apply).</summary>
public sealed class Revert
{
    public Dictionary<(string Sid, string Path), string?> Cells { get; } = [];
    public Dictionary<string, bool> Folders { get; } = []; // inheritance
    public Dictionary<string, Principal> Extra { get; } = []; // columns for accounts that appear in no ACL anymore
    public Dictionary<string, string> NewFolders { get; } = []; // missing folders -> parent folder
}

public static class Drift
{
    public static Desired DesiredOf(Snapshot snap)
    {
        var cells = Rights.ExplicitCells(snap).ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        var names = new Dictionary<string, string>();
        foreach (var (sid, _) in cells.Keys)
            if (snap.Principals.TryGetValue(sid, out var p)) names[sid] = p.Name;
        var prot = snap.Folders.Where(kv => Acl.CanToggle(kv.Value) && kv.Value.Protected).Select(kv => kv.Key).ToHashSet();
        return new Desired { Cells = cells, Names = names, Protected = prot };
    }

    /// <summary>Is a desired cell of an account owlseye hides (Domain Admins, "hidden" in config.json)? Such cells are
    /// no deviations: owlseye neither shows nor touches these accounts, also when the file still has them.</summary>
    static bool Hidden(Desired desired, string sid) => M.IsHidden(sid, desired.Names.GetValueOrDefault(sid));

    public static List<DriftItem> Diff(Snapshot snap, Desired desired)
    {
        var cur = DesiredOf(snap);
        var have = snap.Folders.Keys.Select(M.Lower).ToHashSet();
        var gone = desired.Cells.Keys.Where(k => !Hidden(desired, k.Sid)).Select(k => k.Path).Union(desired.Protected).Distinct()
            .OrderBy(M.Lower, M.Ci).Where(p => !have.Contains(M.Lower(p))).ToList();
        var o = gone.Select(p => new DriftItem("folder_gone", Path: p)).ToList();
        var skip = gone.Select(M.Lower).ToHashSet();
        // Windows paths are case-insensitive: a folder renamed only in case (Data -> data) is the same folder. Compare
        // the desired cells under the folder's current spelling, or the drift would point at a path that is not scanned.
        var spelling = new Dictionary<string, string>();
        foreach (var p in snap.Folders.Keys) spelling[M.Lower(p)] = p;
        var want0 = new Dictionary<(string Sid, string Path), string>();
        foreach (var ((sid, path), v) in desired.Cells)
            if (!Hidden(desired, sid)) want0[(sid, spelling.GetValueOrDefault(M.Lower(path), path))] = v;
        foreach (var k in cur.Cells.Keys.Union(want0.Keys))
        {
            var (sid, path) = k;
            var b = want0.GetValueOrDefault(k);
            var a = cur.Cells.GetValueOrDefault(k);
            if (b == a || (desired.Legacy && b == "W" && a == "F") || skip.Contains(M.Lower(path))) continue;
            var name = cur.Names.GetValueOrDefault(sid) is { Length: > 0 } n ? n : desired.Names.GetValueOrDefault(sid, sid);
            if (M.IsHidden(sid, name)) continue; // also while the matrix shows the hidden accounts (M.ShowHidden)
            o.Add(new DriftItem(b is null ? "added" : a is null ? "removed" : "changed", sid, name, path, b, a));
        }
        var want = desired.Protected.Select(M.Lower).ToHashSet();
        foreach (var (p, f) in snap.Folders)
            if (Acl.CanToggle(f) && f.Protected != want.Contains(M.Lower(p)))
                o.Add(new DriftItem(f.Protected ? "broken" : "restored", Path: p));
        return o.OrderBy(d => M.Lower(d.Path), M.Ci).ThenBy(d => M.Lower(d.Name), M.Ci).ThenBy(d => d.Change, StringComparer.Ordinal).ToList();
    }

    /// <summary>A desired state written before format 2 in today's terms: W where the entry is full control now becomes
    /// F (the old version saved full control as W and did not tell them apart, so nothing it reported gets lost).
    /// Called before such a state is saved again. Others are returned as they are.</summary>
    public static Desired Upgrade(Desired desired, Snapshot snap)
    {
        if (!desired.Legacy) return desired;
        var cur = Rights.ExplicitCells(snap);
        var cells = new Dictionary<(string Sid, string Path), string>(desired.Cells.Count);
        foreach (var (k, v) in desired.Cells)
            cells[k] = v == "W" && cur.GetValueOrDefault(k)?.Value == "F" ? "F" : v;
        return new Desired { Cells = cells, Names = desired.Names, Protected = desired.Protected };
    }

    /// <summary>Desired cells, protected folders and names, indexed so that each change costs a lookup.</summary>
    sealed class Editable
    {
        readonly Dictionary<(string Sid, string Path), string> cells;
        readonly Dictionary<(string Sid, string Lower), List<string>> spellings = []; // a folder may appear in two cases
        readonly Dictionary<string, HashSet<(string Sid, string Path)>> byPath = []; // lowercased path -> cells
        readonly Dictionary<string, string> prot = []; // lowercased path -> spelling
        readonly Dictionary<string, string> names;

        public Editable(Desired d)
        {
            cells = new Dictionary<(string Sid, string Path), string>(d.Cells);
            names = new Dictionary<string, string>(d.Names);
            foreach (var k in cells.Keys) Index(k);
            foreach (var p in d.Protected) prot[M.Lower(p)] = p;
        }

        void Index((string Sid, string Path) k)
        {
            var lower = M.Lower(k.Path);
            if (!spellings.TryGetValue((k.Sid, lower), out var l)) spellings[(k.Sid, lower)] = l = [];
            if (!l.Contains(k.Path)) l.Add(k.Path);
            if (!byPath.TryGetValue(lower, out var s)) byPath[lower] = s = [];
            s.Add(k);
        }

        void Remove((string Sid, string Path) k)
        {
            if (!cells.Remove(k)) return;
            var lower = M.Lower(k.Path);
            if (spellings.TryGetValue((k.Sid, lower), out var l)) l.Remove(k.Path);
            if (byPath.TryGetValue(lower, out var s)) s.Remove(k);
        }

        /// <summary>Set or remove a cell; an entry under another spelling of the same path (case) is replaced.</summary>
        public void SetCell(string sid, string path, string? value, string? name = null)
        {
            if (spellings.TryGetValue((sid, M.Lower(path)), out var l))
                foreach (var other in l.Where(p => p != path).ToList()) Remove((sid, other));
            if (value is null)
            {
                Remove((sid, path));
                return;
            }
            cells[(sid, path)] = value;
            Index((sid, path));
            if (name is not null) names[sid] = name;
        }

        public void ForgetFolder(string path)
        {
            var lower = M.Lower(path);
            if (byPath.TryGetValue(lower, out var s))
                foreach (var k in s.ToList()) Remove(k);
            prot.Remove(lower);
        }

        public void SetProtected(string path, bool value)
        {
            prot.Remove(M.Lower(path));
            if (value) prot[M.Lower(path)] = path;
        }

        public Desired ToDesired() => new() { Cells = cells, Names = names, Protected = prot.Values.ToHashSet() };
    }

    /// <summary>New desired state in which `items` are no longer deviations.</summary>
    public static Desired Accept(Desired desired, IEnumerable<DriftItem> items)
    {
        var e = new Editable(desired);
        foreach (var d in items)
        {
            if (d.Change == "folder_gone") e.ForgetFolder(d.Path);
            else if (d.Change is "broken" or "restored") e.SetProtected(d.Path, d.Change == "broken");
            else e.SetCell(d.Sid, d.Path, d.After, d.After is not null ? d.Name : null);
        }
        return e.ToDesired();
    }

    /// <summary>Desired state after own apply: carry forward the written folders.</summary>
    public static Desired Applied(Desired desired, IEnumerable<AclOp> ops)
    {
        var e = new Editable(desired);
        foreach (var o in ops)
        {
            foreach (var c in o.Changes) e.SetCell(c.Sid, o.Path, c.After, c.After is not null ? c.Name : null);
            if (M.LevelOf(o.Path) >= 1) e.SetProtected(o.Path, o.ProtectedAfter);
        }
        return e.ToDesired();
    }

    /// <summary>Everything can be discarded; deleted folders are recreated empty by owlseye (with their desired rights).</summary>
    public static bool Revertible(DriftItem d) => true;

    public static Revert RevertOf(Snapshot snap, Desired desired, IEnumerable<DriftItem> items)
    {
        var o = new Revert();
        var have = snap.Folders.Keys.Select(M.Lower).ToHashSet();
        ILookup<string, KeyValuePair<(string Sid, string Path), string>>? byPath = null; // only needed for missing folders
        var protLower = desired.Protected.Select(M.Lower).ToHashSet();

        void Column(string sid, string name)
        {
            if (!snap.Principals.ContainsKey(sid)) o.Extra[sid] = new Principal(sid, name, "group");
        }

        foreach (var d in items)
        {
            if (d.Change is "broken" or "restored") o.Folders[d.Path] = d.Change == "restored";
            else if (d.Change == "folder_gone")
            {
                // create folder including missing parents, then set its desired entries and inheritance
                foreach (var p in new[] { d.Path }.Concat(M.Ancestors(d.Path)))
                    if (p != "" && !have.Contains(M.Lower(p)))
                        o.NewFolders[p] = p.Contains('\\') ? p[..p.LastIndexOf('\\')] : "";
                byPath ??= desired.Cells.Where(kv => !Hidden(desired, kv.Key.Sid)).ToLookup(kv => M.Lower(kv.Key.Path));
                foreach (var ((sid, _), v) in byPath[M.Lower(d.Path)])
                {
                    o.Cells[(sid, d.Path)] = v;
                    Column(sid, desired.Names.GetValueOrDefault(sid, sid));
                }
                if (protLower.Contains(M.Lower(d.Path))) o.Folders[d.Path] = true;
            }
            else
            {
                o.Cells[(d.Sid, d.Path)] = d.Before;
                Column(d.Sid, d.Name);
            }
        }
        return o;
    }

    /// <summary>What the outside changes did for users (desired -> actual). Missing folders do not count.</summary>
    public static List<Impact> ImpactOf(Snapshot snap, IEnumerable<DriftItem> items)
    {
        var r = RevertOf(snap, new Desired(), items.Where(d => d.Change != "folder_gone"));
        if (r.Cells.Count == 0 && r.Folders.Count == 0) return [];
        var folders = new Dictionary<string, Folder>(snap.Folders);
        foreach (var (p, v) in r.Folders) folders[p] = snap.Folders[p] with { Protected = v };
        var principals = new Dictionary<string, Principal>(snap.Principals);
        foreach (var (k, v) in r.Extra) principals[k] = v;
        var desired = snap with { Folders = folders, Principals = principals };
        desired = Planner.WithCells(desired, r.Cells, new HashSet<string>());
        return Planner.ImpactOf(desired, snap);
    }
}
