// View models for the pages and panels: what the Razor components render.

using System.Text.Json.Nodes;

namespace Owlseye.Ui;

/// <summary>An error to show to the admin. Status 404: what was asked for does not exist; 400: anything else.</summary>
public sealed class UserError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Where to go after an action, with a flash message.</summary>
public sealed record Outcome(string Url, string? Message = null, bool Error = false);

public static class Labels
{
    public static string? Next(string? v) => v switch
    {
        null => "R",
        "R|" => "R",
        "R" => "W",
        "W|" => "W",
        "W-" => "W",
        "W" => null,
        _ => null,
    };

    public static string Label(string? v) => v switch
    {
        null => "No access",
        "R|" => "R| List (this folder only)",
        "R" => "R Read",
        "W|" => "W| Modify (this folder only)",
        "W-" => "W- Write, no delete (read and write, but nothing can be deleted)",
        "W" => "W Modify (read, write and delete)",
        "F" => "F Full control",
        _ => v,
    };

    public static string Short(string? v) => v ?? "–";


    /// <summary>A value in a change: cell values as they are, owner entries in words.</summary>
    public static string Value(string? sid, string? v) => sid is not null && M.IsOwnerSid(sid) ? OwnerEntryText(sid, v) : Short(v);

    public static string OwnerEntryText(string sid, string? v) => (sid, v) switch
    {
        (_, null) => "nothing",
        (M.CreatorOwner, "F") => "full control",
        (_, "W") => "Modify",
        (M.OwnerRights, "V") => "no personal rights",
        _ => "special rights",
    };

    /// <summary>ACL entries in Windows terms, e.g. "Modify (this folder, subfolders and files)".</summary>
    public static string Describe(IEnumerable<Ace> aces) =>
        string.Join("; ", aces.Select(a => $"{(a.Allow ? "" : "Deny: ")}{RightsName(a.Mask)} ({Scope(a.Flags)})"));

    static string RightsName(uint mask)
    {
        switch (mask)
        {
            case M.Full: return "Full control";
            case 0x10000000: return "Full control (generic)";
            case 0xE0010000: return "Modify (generic)";
            case 0xA0000000: return "Read and execute (generic)";
            case 0x120089: return "Read";
            case 0x100116: return "Write";
        }
        // the largest standard right it contains, plus what goes beyond it
        var (name, @base) = (mask & M.Modify) == M.Modify ? ("Modify", M.Modify)
            : (mask & M.WriteNoDelete) == M.WriteNoDelete ? ("Read, execute and write, no delete", M.WriteNoDelete)
            : (mask & M.Read) == M.Read ? ("Read and execute", M.Read)
            : ("", 0u);
        if (@base == 0) return $"special rights 0x{mask:X}";
        var extra = mask & ~@base;
        var parts = new List<string>();
        if ((extra & M.Delete) != 0) parts.Add("delete");
        if ((extra & M.DeleteChild) != 0) parts.Add("delete subfolders and files");
        if ((extra & M.WriteDac) != 0) parts.Add("change permissions");
        if ((extra & M.WriteOwner) != 0) parts.Add("take ownership");
        var rest = extra & ~(M.Delete | M.DeleteChild | M.WriteDac | M.WriteOwner);
        if (rest != 0) parts.Add($"0x{rest:X}");
        return parts.Count == 0 ? name : $"{name} + {string.Join(", ", parts)}";
    }

    static string Scope(int flags)
    {
        var only = (flags & M.InheritOnly) != 0;
        return (flags & M.OiCi) switch
        {
            M.OiCi => only ? "subfolders and files only" : "this folder, subfolders and files",
            M.ContainerInherit => only ? "subfolders only" : "this folder and subfolders",
            M.ObjectInherit => only ? "files only" : "this folder and files",
            _ => "this folder only",
        };
    }

    /// <summary>CSS class of a right ("R|" -> "RL").</summary>
    public static string Css(string? v) => (v ?? "").Replace("|", "L");

    static readonly string[] HiddenNames = ["administrators", "administratoren", "system", "creator owner", "ersteller-besitzer"];

    /// <summary>Is someone searching for an account that owlseye sets itself (hence no column)?</summary>
    public static bool HiddenHint(string q)
    {
        q = q.Trim().ToLowerInvariant();
        return q != "" && HiddenNames.Any(n => n.StartsWith(q, StringComparison.Ordinal));
    }
}

/// <param name="Value">displayed right (after the pending changes)</param>
/// <param name="Kind">direct | inherited | pending | auto | blocked | none</param>
/// <param name="Source">folder an inherited right comes from</param>
/// <param name="Before">for pending/auto: explicit entry before</param>
public sealed record CellInfo(string? Value, string Kind, string Tip, string? Source = null, bool Standard = true,
    string? Before = null, string? BlockedRight = null, bool Full = false, bool Movable = false)
{
    /// <summary>CSS classes and text of the matrix button.</summary>
    public (string Css, string Text) Display()
    {
        var v = Labels.Css(Value);
        return Kind switch
        {
            "pending" => ((v == "" ? "empty" : v) + " pend", Value ?? "–"),
            "auto" => ((v == "" ? "empty" : v) + " auto", Value ?? "–"),
            "direct" when Movable => (v + " movable", Value!), // W not converted to "keep-folder" yet: marked, not special
            "direct" => (v + (Standard ? "" : " odd"), Value + (Standard ? "" : "*")),
            "inherited" => ("inh", Value ?? ""),
            "blocked" => ("blocked", "⊘"),
            _ => ("empty", ""),
        };
    }
}

/// <summary>One folder row of the matrix. The cells are computed when first asked for: the UI draws only the rows in
/// view, so on a large share most rows never need them.</summary>
public sealed class MatrixRow(Folder folder, Func<IReadOnlyList<CellInfo>> cells, int contentSig, bool isProtected, bool pendingFolder,
    bool isNew, string deep, bool hasChildren, bool flagged, string top, bool pendingInheritance = false, bool moved = false,
    bool kept = false)
{
    readonly Lazy<IReadOnlyList<CellInfo>> lazyCells = new(cells);

    public Folder Folder { get; } = folder;
    public IReadOnlyList<CellInfo> Cells => lazyCells.Value;
    public bool Protected { get; } = isProtected;
    public bool PendingFolder { get; } = pendingFolder;
    public bool IsNew { get; } = isNew;
    public string Deep { get; } = deep;
    public bool HasChildren { get; } = hasChildren;
    public bool Flagged { get; } = flagged;
    public string Top { get; } = top;
    public bool PendingInheritance { get; } = pendingInheritance;

    /// <summary>Moved here (Rights.Stale): its inherited entries are not what its parent passes down.</summary>
    public bool Moved { get; } = moved;

    /// <summary>Users cannot delete, rename or move this folder (Rights.Kept), pending changes included.</summary>
    public bool Kept { get; } = kept;

    /// <summary>Changes whenever anything shown in the row changes (contentSig covers its cells, pending changes and
    /// blocked rights): the UI redraws only rows whose signature changed.</summary>
    public int Sig { get; } = HashCode.Combine(HashCode.Combine(folder.Path, folder.Name, isProtected, pendingFolder, isNew, deep, hasChildren, flagged),
        pendingInheritance, contentSig, moved, kept);
}

public sealed record MatrixView(
    IReadOnlyList<Principal> Columns,
    IReadOnlyList<MatrixRow> Rows,
    IReadOnlyDictionary<string, string> UsersIn,
    string? PlanError,
    string Q,
    int PendingCount,
    IReadOnlyList<string> PendingNew,
    int DriftCount,
    int MaxLevel,
    IReadOnlySet<string> ExtraSids);

public sealed record CellPanel(Principal P, Folder F, CellInfo Info, IReadOnlyList<string?> Options, string? Current,
    IReadOnlyList<User>? Members, bool CanBreak, bool CanRestore, int MaxLevel, string? Entry = null);

public sealed record Grant(string Name, string Right, string? Source);

/// <summary>What owlseye checked, shown above the findings so that an empty list means something.</summary>
/// <param name="Kept">folders users cannot delete, rename or move (Rights.Kept)</param>
/// <param name="StillMovable">of these, the ones some W entry still lets users move (Rights.StillMovable)</param>
/// <param name="WMinus">own W- entries (write without delete), which "Convert all to W|" changes</param>
public sealed record FindingChecks(int Folders, int Moved, int Kept, int StillMovable, int WMinus = 0);

/// <summary>An entry of a hidden account on a folder (SYSTEM, Domain Admins, …), shown in the folder panel.</summary>
public sealed record HiddenEntry(string Name, string Right, bool Inherited);

/// <summary>Creator Owner or Owner Rights on a folder (Rights.OwnerEntryOf).</summary>
/// <param name="Value">from the folder's own entries, pending change included</param>
/// <param name="Inherited">from inherited entries</param>
public sealed record OwnerEntryView(string Sid, string? Value, string? Inherited, bool Pending);

public sealed record FolderPanel(Folder F, string Share, bool CanAdd, IReadOnlyList<string> NewHere, bool IsNew, bool Protected,
    bool Pending, bool CanToggle, bool Clearing, bool CanClear, IReadOnlyList<Grant> Grants, IReadOnlyList<Finding> Findings,
    IReadOnlyList<HiddenEntry> Hidden, IReadOnlyList<OwnerEntryView> OwnerEntries, bool CanSetOwnerEntries,
    bool Stale = false, bool Reinheriting = false, bool Kept = false, bool KeptPending = false,
    IReadOnlyList<string>? StillMovable = null, bool CanKeep = false);

public sealed record GroupsPanel(string Gq, IReadOnlyList<Principal> Hits, IReadOnlySet<string> Shown, bool HiddenHint);

public sealed record PreviewView(Plan? Plan, string? Error, IReadOnlyList<Impact> Gained, IReadOnlyList<Impact> Lost, int Cells,
    string Phash, IReadOnlyList<HiddenLoss>? HiddenLosses = null, IReadOnlyList<Principal>? Required = null);

public sealed record DriftView(IReadOnlyList<DriftItem> Items, IReadOnlyList<Impact> Impact, BaselineInfo Info, string BaselineError,
    Desired Desired, string AuditPath, string TakenAt, string Share);

public sealed record UserDetail(Folder Folder, string Right, IReadOnlyList<string> Via);

public sealed record UsersView(IReadOnlyList<User> Users, User? Selected, IReadOnlyList<UserDetail> Detail, string Q);

public sealed record FolderEntry(Ace Ace, string? Value);

public sealed record FolderView(Folder F, string Share, IReadOnlyList<(User User, string Right)> Who, IReadOnlyList<Finding> Findings,
    IReadOnlyList<FolderEntry> Entries);

public sealed record ShareView(string Share, string Provider, string TakenAt, IReadOnlyList<string> Recent, bool CanSwitch, int PendingCount);

public sealed record AuditView(string Path, IReadOnlyList<JsonObject> Entries);
