// Access rights report: who may do what on the share, as evidence for audits (data protection, ISO 27001) and as the
// replacement for a hand-kept Excel list. Built from the scan as it is (pending changes are not part of it); shown as a page
// (printable to PDF) and exported as an Excel workbook.

using System.Globalization;
using System.Text.Json.Nodes;
using Owlseye.Ui;

namespace Owlseye.Reports;

/// <param name="Value">effective right (own entry or inherited), null = none</param>
/// <param name="Own">entry on this folder (not inherited)</param>
/// <param name="Standard">own entry is one of the four standard entries</param>
public sealed record ReportCell(string? Value, bool Own, bool Standard)
{
    /// <summary>"W", "(W)" for inherited, "W*" for a non-standard own entry, "" for none.</summary>
    public string Text => Value is null ? "" : Own ? Value + (Standard ? "" : "*") : $"({Value})";
}

public sealed record ReportRow(Folder Folder, IReadOnlyList<ReportCell> Cells);

public sealed record AccessRow(string Folder, string User, string Display, bool Enabled, string Right, string Via);

public sealed record GroupMembers(Principal Account, IReadOnlyList<User>? Members);

public sealed record UserAccess(User User, IReadOnlyList<(string Folder, string Right)> Folders);

/// <param name="What">change | inheritance | clear | folder created | kept outside change | desired state deleted | not finished</param>
public sealed record ChangeRow(string Time, string Actor, string Reason, string What, string Folder, string Account, string Before, string After);

public sealed class RightsReport
{
    public required string Share { get; init; }
    public required string Provider { get; init; }
    public required string CreatedBy { get; init; }
    public required string CreatedAt { get; init; }
    public required string ScannedAt { get; init; }
    public required int MaxLevel { get; init; }
    public required int FoldersScanned { get; init; }
    public required IReadOnlyList<Principal> Columns { get; init; }
    public required IReadOnlyList<ReportRow> Matrix { get; init; }
    public required IReadOnlyList<AccessRow> Access { get; init; }
    public required IReadOnlyList<UserAccess> PerUser { get; init; }
    public required IReadOnlyList<GroupMembers> Groups { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<ChangeRow> Changes { get; init; }
    public required int ChangeDays { get; init; }
    public required int PendingCount { get; init; }

    public int UsersWithAccess => PerUser.Count(u => u.Folders.Count > 0);

    public string FileStem => "owlseye-report-" + BaselineStore.FileName(Share)["desired-".Length..^".json".Length] + "-"
        + CreatedAt[..10];
}

public static class ReportBuilder
{
    static string Root(string path) => path != "" ? path : "(root)";

    static string ShortName(string name) => name[(name.LastIndexOf('\\') + 1)..];

    /// <summary>The report for the share as scanned. Folders and columns as in the matrix (its depth); changes of the
    /// last `changeDays` days from the log.</summary>
    public static RightsReport Build(State st, int changeDays = 90)
    {
        lock (st.Lock)
        {
            var snap = st.Snap;
            var cells = st.Cells;
            var cache = st.Cache is { } c && ReferenceEquals(c.Snap, snap) ? c : new RightsCache(snap, cells);
            var folders = st.Folders(snap);
            var used = cells.Keys.Select(k => k.Sid).ToHashSet();
            var cols = st.Columns().Where(p => used.Contains(p.Sid)).ToList(); // as scanned: not added or pending-only columns

            var matrix = folders.Select(f => new ReportRow(f, cols.Select(p =>
            {
                cells.TryGetValue((p.Sid, f.Path), out var cell);
                var own = cell is { Direct: not null } && cell.Source == f.Path;
                return new ReportCell(cell?.Effective, own, cell?.Standard ?? true);
            }).ToList())).ToList();

            var users = snap.Users.Values.OrderBy(u => M.Lower(u.Display), M.Ci).ThenBy(u => u.Sam, StringComparer.Ordinal).ToList();
            var access = new List<AccessRow>();
            var perUser = new List<UserAccess>();
            foreach (var u in users)
            {
                var eff = cache.UserRights.GetValueOrDefault(u.Dn) ?? [];
                var list = new List<(string, string)>();
                foreach (var f in folders)
                {
                    if (!eff.TryGetValue(f.Path, out var right)) continue;
                    list.Add((f.Path, right));
                    var via = string.Join("; ", Rights.Via(snap, u.Dn, f.Path, cells, cache.UserSids));
                    access.Add(new AccessRow(Root(f.Path), u.Sam, u.Display, u.Enabled, right, via));
                }
                perUser.Add(new UserAccess(u, list));
            }

            var groups = cols.Select(p => new GroupMembers(p,
                Rights.MembersOf(snap, p.Sid, cache.Transitive)?.Where(snap.Users.ContainsKey).Select(d => snap.Users[d])
                    .OrderBy(u => M.Lower(u.Display), M.Ci).ToList())).ToList();

            return new RightsReport
            {
                Share = snap.Share,
                Provider = st.Provider.Name,
                CreatedBy = st.Actor,
                CreatedAt = Clock.Now(),
                ScannedAt = snap.TakenAt,
                MaxLevel = st.Cfg.MaxLevel,
                FoldersScanned = snap.Folders.Count,
                Columns = cols,
                Matrix = matrix,
                Access = access,
                PerUser = perUser,
                Groups = groups,
                Findings = st.Findings,
                Changes = Changes(st, changeDays),
                ChangeDays = changeDays,
                PendingCount = st.PendingCount,
            };
        }
    }

    /// <summary>What owlseye changed (and what admins accepted) in the last days, one row per entry, newest first.</summary>
    static List<ChangeRow> Changes(State st, int days)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var o = new List<ChangeRow>();
        foreach (var e in st.Audit.Entries(10_000))
        {
            if (!M.SameShare(e.Str("share") ?? "", st.Snap.Share)) continue;
            if (!DateTimeOffset.TryParse(e.Str("ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) || ts < since) continue;
            var time = e.Str("ts")!;
            var actor = e.Str("actor") ?? "";
            var reason = e.Str("reason") ?? "";
            void Add(string what, string folder = "", string account = "", string? before = null, string? after = null) =>
                o.Add(new ChangeRow(time, actor, reason, what, folder, account, Labels.Short(before), Labels.Short(after)));

            switch (e.Str("kind"))
            {
                case "change" or "change_start":
                    if (e.Str("kind") == "change_start") Add("not finished (owlseye was stopped while writing)");
                    foreach (var p in (e.Arr("create_ops") ?? []).OfType<JsonValue>())
                        if (p.TryGetValue<string>(out var created)) Add("folder created", created);
                    foreach (var a in (e.Arr("acl_ops") ?? []).OfType<JsonObject>())
                    {
                        if (!a.ContainsKey("protected_before")) continue; // earlier model
                        var path = Root(a.Str("path") ?? "");
                        if (a.Bool("cleared") == true) Add("clear (inherits, no own entries)", path);
                        else if (a.Bool("protected_before") != a.Bool("protected_after"))
                            Add(a.Bool("protected_after") == true ? "inheritance broken" : "inheritance restored", path);
                        foreach (var ch in (a.Arr("changes") ?? []).OfType<JsonObject>())
                            Add(ch.Bool("auto") == true ? "change (automatic R|)" : "change", path, ShortName(ch.Str("name") ?? ""),
                                ch.Str("before"), ch.Str("after"));
                    }
                    break;
                case "drift_accept":
                    foreach (var d in (e.Arr("accepted") ?? []).OfType<JsonObject>())
                    {
                        var ch = d.Str("change");
                        var what = ch switch
                        {
                            "folder_gone" => "kept: folder missing",
                            "broken" or "restored" => $"kept: inheritance {ch}",
                            _ => $"kept outside change ({ch})",
                        };
                        Add(what, Root(d.Str("path") ?? ""), ShortName(d.Str("name") ?? ""), d.Str("before"), d.Str("after"));
                    }
                    break;
                case "baseline_reset":
                    Add("desired state deleted (current state became the desired state)");
                    break;
                case "baseline_init":
                    Add("current state taken as the first desired state");
                    break;
            }
        }
        return o;
    }

    /// <summary>The report as an Excel workbook: summary, matrix, one row per user and folder (to filter), groups and
    /// members, findings, changes.</summary>
    public static void WriteXlsx(RightsReport r, string path)
    {
        var summary = new List<IReadOnlyList<string>>
        {
            new[] { "Share", r.Share },
            new[] { "Created", $"{r.CreatedAt} by {r.CreatedBy}" },
            new[] { "Scanned", r.ScannedAt },
            new[] { "Mode", r.Provider },
            new[] { "Folders", $"{r.Matrix.Count} shown (matrix depth {r.MaxLevel} and deeper deviations) of {r.FoldersScanned} scanned" },
            new[] { "Accounts with rights", r.Columns.Count.ToString() },
            new[] { "Users with access", $"{r.UsersWithAccess} of {r.PerUser.Count}" },
            new[] { "Findings", string.Join(", ", r.Findings.GroupBy(f => f.Severity).Select(g => $"{g.Count()} {g.Key}")) is { Length: > 0 } fs ? fs : "none" },
            new[] { "State", r.PendingCount > 0 ? $"as scanned; {r.PendingCount} pending change(s) in owlseye are not included" : "as scanned" },
            new[] { "", "" },
            new[] { "Matrix legend", "W write, R read (this folder and below); W| R| this folder only; (W) inherited; * special entry (not one of the four standard entries)" },
            new[] { "Access", "one row per user and folder with the strongest right and the accounts it comes through" },
        };
        var matrixHeader = new List<string> { "Folder", "Inheritance" };
        matrixHeader.AddRange(r.Columns.Select(p => p.Short));
        var matrixRows = r.Matrix.Select(row =>
        {
            var cells = new List<string> { Root(row.Folder.Path), row.Folder.Level > 0 && row.Folder.Protected ? "broken" : "" };
            cells.AddRange(row.Cells.Select(c => c.Text));
            return (IReadOnlyList<string>)cells;
        });
        var groupRows = r.Groups.SelectMany(g => g.Members is null
            ? [new[] { g.Account.Short, g.Account.Name, g.Account.Kind, "", "members not known (built-in or not in the directory)" }]
            : g.Members.Count == 0
                ? [new[] { g.Account.Short, g.Account.Name, g.Account.Kind, "", "no members" }]
                : g.Members.Select(u => (IReadOnlyList<string>)new[] { g.Account.Short, g.Account.Name, g.Account.Kind, u.Sam, u.Display + (u.Enabled ? "" : " (disabled)") }));
        Xlsx.Write(path,
        [
            new Sheet("Summary", ["Report", "owlseye access rights"], summary, [24, 110]),
            new Sheet("Matrix", matrixHeader, matrixRows, [48, 12, .. r.Columns.Select(_ => 5.5)], FreezeColumns: 1, RotateHeader: true),
            new Sheet("Access", ["Folder", "User", "Name", "Enabled", "Right", "Via"],
                r.Access.Select(a => (IReadOnlyList<string>)new[] { a.Folder, a.User, a.Display, a.Enabled ? "yes" : "no", a.Right, a.Via }),
                [48, 16, 26, 9, 7, 70]),
            new Sheet("Groups", ["Group", "Account", "Kind", "Member", "Name"], groupRows, [26, 36, 11, 16, 40]),
            new Sheet("Findings", ["Severity", "Folder", "Finding"],
                r.Findings.Select(f => (IReadOnlyList<string>)new[] { f.Severity, Root(f.Path), f.Text }), [10, 48, 90]),
            new Sheet($"Changes ({r.ChangeDays} days)", ["Time (UTC)", "Who", "Reason", "Change", "Folder", "Account", "Before", "After"],
                r.Changes.Select(c => (IReadOnlyList<string>)new[] { c.Time, c.Actor, c.Reason, c.What, c.Folder, c.Account, c.Before, c.After }),
                [22, 22, 30, 30, 40, 18, 8, 8]),
        ]);
    }
}
