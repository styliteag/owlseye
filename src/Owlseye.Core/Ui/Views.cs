// View models for the pages and panels (what the Jinja templates got as context in the Python version).

using System.Text.Json.Nodes;

namespace Owlseye.Ui;

/// <summary>An error to show to the admin (HTTP 400/404 in the Python version).</summary>
public sealed class UserError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Where to go after an action, with a flash message (a redirect with ?msg= in the Python version).</summary>
public sealed record Outcome(string Url, string? Message = null, bool Error = false);

public static class Labels
{
    public static string? Next(string? v) => v switch
    {
        null => "R",
        "R|" => "R",
        "R" => "W",
        "W|" => "W",
        "W" => null,
        _ => null,
    };

    public static string Label(string? v) => v switch
    {
        null => "No access",
        "R|" => "R| List (this folder only)",
        "R" => "R Read",
        "W|" => "W| Write (this folder only)",
        "W" => "W Write",
        _ => v,
    };

    public static string Short(string? v) => v ?? "–";

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
    string? Before = null, string? BlockedRight = null)
{
    /// <summary>CSS classes and text of the matrix button.</summary>
    public (string Css, string Text) Display()
    {
        var v = Labels.Css(Value);
        return Kind switch
        {
            "pending" => ((v == "" ? "empty" : v) + " pend", Value ?? "–"),
            "auto" => ((v == "" ? "empty" : v) + " auto", Value ?? "–"),
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
    bool isNew, string deep, bool hasChildren, bool flagged, string top, bool pendingInheritance = false)
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

    /// <summary>Changes whenever anything shown in the row changes (contentSig covers its cells, pending changes and
    /// blocked rights): the UI redraws only rows whose signature changed.</summary>
    public int Sig { get; } = HashCode.Combine(HashCode.Combine(folder.Path, folder.Name, isProtected, pendingFolder, isNew, deep, hasChildren, flagged),
        pendingInheritance, contentSig);
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
    IReadOnlyList<User>? Members, bool CanBreak, bool CanRestore, int MaxLevel);

public sealed record Grant(string Name, string Right, string? Source);

public sealed record FolderPanel(Folder F, string Share, bool CanAdd, IReadOnlyList<string> NewHere, bool IsNew, bool Protected,
    bool Pending, bool CanToggle, bool Clearing, bool CanClear, IReadOnlyList<Grant> Grants, IReadOnlyList<Finding> Findings);

public sealed record GroupsPanel(string Gq, IReadOnlyList<Principal> Hits, IReadOnlySet<string> Shown, bool HiddenHint);

public sealed record PreviewView(Plan? Plan, string? Error, IReadOnlyList<Impact> Gained, IReadOnlyList<Impact> Lost, int Cells,
    string Phash);

public sealed record DriftView(IReadOnlyList<DriftItem> Items, IReadOnlyList<Impact> Impact, BaselineInfo Info, string BaselineError,
    Desired Desired, string AuditPath, string TakenAt, string Share);

public sealed record UserDetail(Folder Folder, string Right, IReadOnlyList<string> Via);

public sealed record UsersView(IReadOnlyList<User> Users, User? Selected, IReadOnlyList<UserDetail> Detail, string Q);

public sealed record FolderEntry(Ace Ace, string? Value);

public sealed record FolderView(Folder F, string Share, IReadOnlyList<(User User, string Right)> Who, IReadOnlyList<Finding> Findings,
    IReadOnlyList<FolderEntry> Entries);

public sealed record ShareView(string Share, string Provider, string TakenAt, IReadOnlyList<string> Recent, bool CanSwitch, int PendingCount);

public sealed record AuditView(string Path, IReadOnlyList<JsonObject> Entries);
