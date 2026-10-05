// Recompute the explicit ACL of a folder: set cell, break/restore inheritance.
//
// Breaking inheritance copies all inherited entries explicitly (like "Convert inherited permissions into explicit"):
// nobody loses access, and the matrix then shows everything as own entries that can be removed
// selectively. Restoring removes explicit copies of what the parent folder inherits anyway.
// Foreign entries (Deny, special rights, accounts outside a changed cell) stay unchanged.

namespace Owlseye;

public static class Acl
{
    public static readonly IReadOnlyList<Ace> AdminAces =
    [
        new(M.System, "NT AUTHORITY\\SYSTEM", "wellknown", M.Full, Flags: M.OiCi),
        new(M.Admins, "BUILTIN\\Administrators", "wellknown", M.Full, Flags: M.OiCi),
    ];

    public static (string Sid, bool Allow, uint Mask, int Flags) Key(Ace a) => (a.Sid, a.Allow, a.Mask, a.Flags & ~M.InheritedAce);

    /// <summary>Same ACE set (order does not matter), compared by Key.</summary>
    public static bool SameKeys(IEnumerable<Ace> a, IEnumerable<Ace> b) =>
        a.Select(Key).Order().SequenceEqual(b.Select(Key).Order());

    public static List<Ace> Dedupe(IEnumerable<Ace> aces)
    {
        var seen = new HashSet<(string, bool, uint, int)>();
        var o = new List<Ace>();
        foreach (var a in aces)
            if (seen.Add(Key(a))) o.Add(a);
        return o;
    }

    /// <summary>Toggling inheritance works everywhere except at the root (also deeper than the matrix, for cleanup).</summary>
    public static bool CanToggle(Folder f) => f.Level >= 1;

    /// <summary>Explicit = previously explicit + all inherited entries, as own entries.</summary>
    public static List<Ace> AfterBreak(Folder f)
    {
        var converted = f.Aces.Where(a => a.Inherited).Select(a => a with { Inherited = false, Flags = a.Flags & ~M.InheritedAce });
        return Dedupe([.. f.Explicit, .. converted]);
    }

    /// <summary>Explicit minus what the parent folder passes down to subfolders (CI); "this folder only" stays.</summary>
    public static List<Ace> AfterRestore(Folder f, Folder? parent)
    {
        if (parent is null) return [.. f.Explicit];
        var passed = parent.Aces.Where(a => (a.Flags & M.ContainerInherit) != 0).Select(a => (a.Sid, a.Allow, a.Mask)).ToHashSet();
        return f.Explicit.Where(a => !passed.Contains((a.Sid, a.Allow, a.Mask))).ToList();
    }

    /// <summary>Replace all Allow entries of the account with the standard entry for `value` (null = remove).</summary>
    public static List<Ace> SetCell(IEnumerable<Ace> explicitAces, Principal p, string? value)
    {
        var rest = explicitAces.Where(a => !(a.Allow && a.Sid == p.Sid)).ToList();
        if (value is null) return rest;
        var (mask, flags) = M.Standard[value];
        rest.Add(new Ace(p.Sid, p.Name, p.Kind, mask, true, false, flags));
        return rest;
    }

    /// <summary>The accounts that must have full control (config.json "full_control", by default SYSTEM and
    /// Administrators) with full control on this folder, subfolders and files, where missing. On a folder that inherits
    /// (the share root), full control inherited from above counts.</summary>
    public static List<Ace> EnsureAdmins(IEnumerable<Ace> explicitAces, IReadOnlyList<Principal> required,
        IEnumerable<Ace>? inherited = null)
    {
        var list = explicitAces.ToList();
        List<Ace> all = [.. list, .. inherited ?? []];
        list.AddRange(required.Where(p => !Rights.HasFullControl(all, p.Sid)).Select(p =>
            AdminAces.FirstOrDefault(a => a.Sid == p.Sid) ?? new Ace(p.Sid, p.Name, p.Kind, M.Full, Flags: M.OiCi)));
        return list;
    }

    /// <summary>Deny before Allow, otherwise keep order.</summary>
    public static List<Ace> Canonical(IEnumerable<Ace> explicitAces) => explicitAces.OrderBy(a => a.Allow).ToList();
}

/// <summary>Create subfolders: validate name, limit level. The new folder inherits from the parent folder;
/// set rights on it afterwards like on any folder in the matrix (even before applying).</summary>
public static class Create
{
    /// <summary>new: path -> parent. Returns the paths in creation order (parents first).
    /// Throws ArgumentException on invalid name, too deep a level, missing parent folder or duplicate.</summary>
    public static List<string> Plan(Snapshot snap, IReadOnlyDictionary<string, string> newFolders, int maxLevel)
    {
        var existing = snap.Folders.Keys.Select(M.Lower).ToHashSet();
        var planned = newFolders.Keys.Select(M.Lower).ToHashSet();
        foreach (var (path, parent) in newFolders)
        {
            var name = path[(path.LastIndexOf('\\') + 1)..];
            if ((parent != "" ? parent + "\\" + name : name) != path || M.BadFolderName(name))
                throw new ArgumentException($"Invalid folder name: {Msg.Quote(name)}");
            if (name.Length > M.MaxFolderName)
                throw new ArgumentException($"Folder names can have at most {M.MaxFolderName} characters ({name.Length} given)");
            if (existing.Contains(M.Lower(path))) throw new ArgumentException($"{path} already exists");
            if (M.LevelOf(path) > maxLevel) throw new ArgumentException($"New folders can only be created down to level {maxLevel}");
            if (!existing.Contains(M.Lower(parent)) && !planned.Contains(M.Lower(parent)))
                throw new ArgumentException($"{parent} does not exist");
        }
        return newFolders.Keys.OrderBy(M.LevelOf).ThenBy(M.Lower, M.Ci).ToList();
    }
}

/// <summary>Formatting of names in messages.</summary>
public static class Msg
{
    /// <summary>A name in quotes, with control characters escaped: single quotes unless the name contains one and no
    /// double quote.</summary>
    public static string Quote(string s)
    {
        var q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new System.Text.StringBuilder().Append(q);
        foreach (var c in s)
        {
            if (c == q || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32 || c == 127) sb.Append($"\\x{(int)c:x2}");
            else sb.Append(c);
        }
        return sb.Append(q).ToString();
    }
}
