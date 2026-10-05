// Compare desired (baseline) against actual: outside changes to entries and to inheritance,
// on all scanned levels (regardless of how deep the matrix currently shows).
//
// Accept shifts the desired state. Discard puts the desired values into the matrix as pending changes;
// they then go through preview and apply normally (including automatic R|).

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

    public static List<DriftItem> Diff(Snapshot snap, Desired desired)
    {
        var cur = DesiredOf(snap);
        var have = snap.Folders.Keys.Select(M.Lower).ToHashSet();
        var gone = desired.Cells.Keys.Select(k => k.Path).Union(desired.Protected).Distinct()
            .OrderBy(M.Lower, M.Ci).Where(p => !have.Contains(M.Lower(p))).ToList();
        var o = gone.Select(p => new DriftItem("folder_gone", Path: p)).ToList();
        var skip = gone.Select(M.Lower).ToHashSet();
        // Windows paths are case-insensitive: a folder renamed only in case (Data -> data) is the same folder. Compare
        // the desired cells under the folder's current spelling, or the drift would point at a path that is not scanned.
        var spelling = new Dictionary<string, string>();
        foreach (var p in snap.Folders.Keys) spelling[M.Lower(p)] = p;
        var want0 = new Dictionary<(string Sid, string Path), string>();
        foreach (var ((sid, path), v) in desired.Cells) want0[(sid, spelling.GetValueOrDefault(M.Lower(path), path))] = v;
        foreach (var k in cur.Cells.Keys.Union(want0.Keys))
        {
            var (sid, path) = k;
            var b = want0.GetValueOrDefault(k);
            var a = cur.Cells.GetValueOrDefault(k);
            if (b == a || skip.Contains(M.Lower(path))) continue;
            var name = cur.Names.GetValueOrDefault(sid) is { Length: > 0 } n ? n : desired.Names.GetValueOrDefault(sid, sid);
            o.Add(new DriftItem(b is null ? "added" : a is null ? "removed" : "changed", sid, name, path, b, a));
        }
        var want = desired.Protected.Select(M.Lower).ToHashSet();
        foreach (var (p, f) in snap.Folders)
            if (Acl.CanToggle(f) && f.Protected != want.Contains(M.Lower(p)))
                o.Add(new DriftItem(f.Protected ? "broken" : "restored", Path: p));
        return o.OrderBy(d => M.Lower(d.Path), M.Ci).ThenBy(d => M.Lower(d.Name), M.Ci).ThenBy(d => d.Change, StringComparer.Ordinal).ToList();
    }

    /// <summary>New desired state in which `items` are no longer deviations.</summary>
    public static Desired Accept(Desired desired, IEnumerable<DriftItem> items)
    {
        var cells = new Dictionary<(string Sid, string Path), string>(desired.Cells);
        var names = new Dictionary<string, string>(desired.Names);
        var prot = new HashSet<string>(desired.Protected);
        foreach (var d in items)
        {
            if (d.Change == "folder_gone")
            {
                cells = cells.Where(kv => M.Lower(kv.Key.Path) != M.Lower(d.Path)).ToDictionary();
                prot = prot.Where(p => M.Lower(p) != M.Lower(d.Path)).ToHashSet();
            }
            else if (d.Change is "broken" or "restored")
            {
                prot = prot.Where(p => M.Lower(p) != M.Lower(d.Path)).ToHashSet();
                if (d.Change == "broken") prot.Add(d.Path);
            }
            else
            {
                SetCell(cells, d.Sid, d.Path, d.After);
                if (d.After is not null) names[d.Sid] = d.Name;
            }
        }
        return new Desired { Cells = cells, Names = names, Protected = prot };
    }

    /// <summary>Set or remove a desired cell; an entry under another spelling of the same path (case) is replaced.</summary>
    static void SetCell(Dictionary<(string Sid, string Path), string> cells, string sid, string path, string? value)
    {
        var lower = M.Lower(path);
        foreach (var k in cells.Keys.Where(k => k.Sid == sid && k.Path != path && M.Lower(k.Path) == lower).ToList()) cells.Remove(k);
        if (value is null) cells.Remove((sid, path));
        else cells[(sid, path)] = value;
    }

    /// <summary>Desired state after own apply: carry forward the written folders.</summary>
    public static Desired Applied(Desired desired, IEnumerable<AclOp> ops)
    {
        var cells = new Dictionary<(string Sid, string Path), string>(desired.Cells);
        var names = new Dictionary<string, string>(desired.Names);
        var prot = new HashSet<string>(desired.Protected);
        foreach (var o in ops)
        {
            foreach (var c in o.Changes)
            {
                SetCell(cells, c.Sid, o.Path, c.After);
                if (c.After is not null) names[c.Sid] = c.Name;
            }
            if (M.LevelOf(o.Path) >= 1)
            {
                prot = prot.Where(p => M.Lower(p) != M.Lower(o.Path)).ToHashSet();
                if (o.ProtectedAfter) prot.Add(o.Path);
            }
        }
        return new Desired { Cells = cells, Names = names, Protected = prot };
    }

    /// <summary>Everything can be discarded; deleted folders are recreated empty by owlseye (with their desired rights).</summary>
    public static bool Revertible(DriftItem d) => true;

    public static Revert RevertOf(Snapshot snap, Desired desired, IEnumerable<DriftItem> items)
    {
        var o = new Revert();
        var have = snap.Folders.Keys.Select(M.Lower).ToHashSet();

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
                foreach (var ((sid, path), v) in desired.Cells)
                    if (M.Lower(path) == M.Lower(d.Path))
                    {
                        o.Cells[(sid, d.Path)] = v;
                        Column(sid, desired.Names.GetValueOrDefault(sid, sid));
                    }
                if (desired.Protected.Any(p => M.Lower(p) == M.Lower(d.Path))) o.Folders[d.Path] = true;
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
        var folders = new Dictionary<string, Folder>(snap.Folders);
        foreach (var (p, v) in r.Folders) folders[p] = snap.Folders[p] with { Protected = v };
        var principals = new Dictionary<string, Principal>(snap.Principals);
        foreach (var (k, v) in r.Extra) principals[k] = v;
        var desired = snap with { Folders = folders, Principals = principals };
        desired = Planner.WithCells(desired, r.Cells, new HashSet<string>());
        return Planner.ImpactOf(desired, snap);
    }
}
