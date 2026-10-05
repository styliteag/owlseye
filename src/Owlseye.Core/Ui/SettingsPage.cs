// The settings page: what config.json holds besides provider and share, editable at run time and logged.

using System.Text.Json.Nodes;

namespace Owlseye.Ui;

/// <param name="File">the config.json in use, null if none</param>
/// <param name="SaveTo">where saving goes: that file if it can be written, else the admin's own one</param>
/// <param name="Required">the full_control accounts as found in this share's ACLs</param>
public sealed record SettingsView(string? File, string SaveTo, bool CanSave, string? Blocked, int ScanDepth, string Write,
    IReadOnlyList<string> Hidden, IReadOnlyList<string> FullControl, IReadOnlyList<Principal> Required, string Audit,
    string Baseline, string AuditPath, string? BaselineDir, string Provider);

public sealed record SettingsInput(int ScanDepth, string Write, IReadOnlyList<string> Hidden, IReadOnlyList<string> FullControl,
    string Audit, string Baseline);

public sealed partial class Session
{
    static bool Writable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The config.json in use if it can be written, else the admin's own one (read at the next start before
    /// a config.json next to the exe). A file given with --config is the only place: if it is read-only, saving is off.</summary>
    (string Target, string? Blocked) SaveTarget()
    {
        var file = St.Cfg.File;
        if (file is not null && Writable(file)) return (file, null);
        if (St.ConfigFromCommandLine && file is not null)
            return (file, $"{file} (given with --config) cannot be written; change it there or start owlseye without --config.");
        return (Launch.PersonalConfig, null);
    }

    public SettingsView SettingsPage()
    {
        lock (St.Lock)
        {
            var c = St.Cfg;
            var (target, blocked) = SaveTarget();
            var reason = blocked ?? (St.PendingCount > 0 ? "Apply or discard the pending changes first: saving rescans the share." : null);
            return new SettingsView(c.File, target, reason is null, reason, c.ScanDepth, c.Write, c.Hidden, c.FullControl,
                St.Ready ? Rights.RequiredFullControl(St.Snap) : [], c.Audit, c.Baseline, c.AuditPath, c.BaselineDir, c.Provider);
        }
    }

    static List<string> Clean(IEnumerable<string> xs) =>
        xs.SelectMany(x => x.Split('\n')).Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Saves the settings to config.json, applies them (hidden accounts, W, full control at once; the scan
    /// depth with a new scan; log and desired-state folders after a restart) and logs old and new values.</summary>
    public Outcome SaveSettings(SettingsInput input, string reason)
    {
        string message;
        lock (St.Lock)
        {
            var (target, blocked) = SaveTarget();
            if (blocked is not null) return new Outcome("/settings", blocked, true);
            if (St.PendingCount > 0) return new Outcome("/settings", "Apply or discard the pending changes first: saving rescans the share.", true);
            if (input.ScanDepth is < 0 or > 100) return new Outcome("/settings", "Scan depth must be 0 (whole tree) to 100.", true);
            if (input.Write is not ("modify" or "no-delete")) return new Outcome("/settings", "W must mean \"modify\" or \"no-delete\".", true);
            var old = St.Cfg;
            var neu = old with
            {
                ScanDepth = input.ScanDepth,
                Write = input.Write,
                Hidden = Clean(input.Hidden),
                FullControl = Clean(input.FullControl) is { Count: > 0 } fc ? fc : M.DefaultFullControl,
                Audit = input.Audit.Trim(),
                Baseline = input.Baseline.Trim(),
            };
            var changes = new JsonObject();
            void Diff(string key, JsonNode? before, JsonNode? after)
            {
                if (before?.ToJsonString() != after?.ToJsonString()) changes[key] = new JsonObject { ["before"] = before, ["after"] = after };
            }
            JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)x!).ToArray());
            Diff("scan_depth", old.ScanDepth, neu.ScanDepth);
            Diff("write", old.Write, neu.Write);
            Diff("hidden", Arr(old.Hidden), Arr(neu.Hidden));
            Diff("full_control", Arr(old.FullControl), Arr(neu.FullControl));
            Diff("audit", old.Audit, neu.Audit);
            Diff("baseline", old.Baseline, neu.Baseline);
            if (changes.Count == 0) return new Outcome("/settings", "Nothing changed.");
            try
            {
                // the admin's own file starts as a copy of the one in use, so provider, share etc. stay
                if (target != old.File && old.File is not null && File.Exists(old.File) && !File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(old.File, target);
                }
                Config.Save(target, neu);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return new Outcome("/settings", $"Settings not saved: {e.Message}", true);
            }
            M.Configure(neu.Write, neu.Hidden, neu.FullControl);
            St.ApplySettings(neu with { File = target });
            St.Audit.Append(new JsonObject
            {
                ["kind"] = "settings",
                ["actor"] = St.Actor,
                ["provider"] = St.Provider.Name,
                ["share"] = St.Snap.Share,
                ["reason"] = reason.Trim(),
                ["status"] = "ok",
                ["file"] = target,
                ["changes"] = changes,
            });
            var restart = changes.ContainsKey("audit") || changes.ContainsKey("baseline");
            if (changes.ContainsKey("scan_depth") && St.OpenShare is not null && St.Ready)
                St.Switch(St.OpenShare(St.Provider.Share)); // a provider with the new depth, scanned anew
            else
                St.Rescan();
            message = $"Settings saved to {target}."
                + (changes.ContainsKey("scan_depth") && St.OpenShare is null ? " The scan depth applies at the next start." : "")
                + (restart ? " The log and desired-state folders apply at the next start." : "");
        }
        St.NotifyChanged();
        return new Outcome("/settings", message);
    }
}
