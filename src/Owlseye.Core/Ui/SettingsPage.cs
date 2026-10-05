// The settings page: what config.json holds besides provider and share, editable at run time and logged.

using System.Text.Json.Nodes;

namespace Owlseye.Ui;

/// <param name="File">the config.json in use, null if none</param>
/// <param name="SaveTo">where saving goes: that file if it can be written, else the admin's own one</param>
/// <param name="Required">the full_control accounts as found in this share's ACLs</param>
/// <param name="State">the state folder as set ("" = the data folder)</param>
public sealed record SettingsView(string? File, string SaveTo, bool CanSave, string? Blocked, int ScanDepth, string Write,
    IReadOnlyList<string> Hidden, IReadOnlyList<string> FullControl, IReadOnlyList<Principal> Required, string State,
    string StateDir, string AuditPath, string? BaselineDir, string Provider, string SharedFile = "", bool SharedExists = false);

public sealed record SettingsInput(int ScanDepth, string Write, IReadOnlyList<string> Hidden, IReadOnlyList<string> FullControl,
    string State);

/// <param name="Moved">files moved into the new state folder</param>
/// <param name="Existing">the new folder already held owlseye state (another admin uses it): nothing was moved</param>
public sealed record StateMove(IReadOnlyList<string> Moved, bool Existing);

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
            var reason = blocked ?? (St.PendingCount > 0 ? "Apply or discard the pending changes first: saving works the rights out anew." : null);
            return new SettingsView(c.File, target, reason is null, reason, c.ScanDepth, c.Write, c.Hidden, c.FullControl,
                St.Ready ? Rights.RequiredFullControl(St.Snap) : [], c.State, c.StateDir, c.AuditPath, c.BaselineDir, c.Provider,
                c.SharedFile, File.Exists(c.SharedFile));
        }
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves the desired-state files and the logs from where `from` keeps them into the state folder of `to`,
    /// if that folder holds none yet. All are copied first and the originals deleted afterwards: a failure leaves
    /// everything where it was. If the folder already holds owlseye state (another admin uses it), nothing is moved
    /// and that state is used from now on.</summary>
    public static StateMove MoveState(Config from, Config to)
    {
        var dir = to.StateDir;
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFiles(dir, "desired-*.json").Any() || Directory.EnumerateFiles(dir, "audit*.jsonl").Any()
            || File.Exists(to.SharedFile))
            return new StateMove([], true);
        var files = new List<(string From, string To)>();
        if (from.BaselineDir is { } bd && to.BaselineDir is { } nd && Directory.Exists(bd))
            foreach (var f in Directory.EnumerateFiles(bd, "desired-*.json")) files.Add((f, Path.Combine(nd, Path.GetFileName(f))));
        if (File.Exists(from.AuditPath)) files.Add((from.AuditPath, to.AuditPath)); // the log in use, under its new name
        if (File.Exists(from.SharedFile)) files.Add((from.SharedFile, to.SharedFile)); // rewritten with the values saved
        if (Directory.Exists(from.StateDir)) // the logs of the other providers along with it
            foreach (var f in Directory.EnumerateFiles(from.StateDir, "audit-*.jsonl"))
                if (!files.Any(x => SamePath(x.From, f))) files.Add((f, Path.Combine(dir, Path.GetFileName(f))));
        files = files.Where(x => !SamePath(x.From, x.To)).ToList();
        var copied = new List<string>();
        try
        {
            foreach (var (src, dst) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
                File.Copy(src, dst, overwrite: false);
                copied.Add(dst);
            }
        }
        catch
        {
            foreach (var c in copied) File.Delete(c);
            throw;
        }
        foreach (var (src, _) in files) File.Delete(src);
        return new StateMove(copied, false);
    }

    static List<string> Clean(IEnumerable<string> xs) =>
        xs.SelectMany(x => x.Split('\n')).Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Saves the settings (shared ones to the state folder, where that is to config.json), applies them at
    /// once and logs old and new values. Only a new scan depth reads the share again; W, hidden and full-control accounts
    /// and the state folder are worked out from the last scan.</summary>
    public Outcome SaveSettings(SettingsInput input, string reason)
    {
        string message;
        lock (St.Lock)
        {
            var (target, blocked) = SaveTarget();
            if (blocked is not null) return new Outcome("/settings", blocked, true);
            if (St.PendingCount > 0) return new Outcome("/settings", "Apply or discard the pending changes first: saving works the rights out anew.", true);
            if (input.ScanDepth is < 0 or > 100) return new Outcome("/settings", "Scan depth must be 0 (whole tree) to 100.", true);
            if (input.Write is not ("modify" or "no-delete")) return new Outcome("/settings", "W must mean \"modify\" or \"no-delete\".", true);
            var old = St.Cfg;
            var next = old with
            {
                ScanDepth = input.ScanDepth,
                Write = input.Write,
                Hidden = Clean(input.Hidden),
                FullControl = Clean(input.FullControl) is { Count: > 0 } fc ? fc : M.DefaultFullControl,
                State = input.State.Trim().TrimEnd('\\'),
            };
            if (next.State != old.State) next = next with { Audit = "", Baseline = "" }; // both follow the state folder now
            var moveState = !SamePath(old.AuditPath, next.AuditPath) || old.BaselineDir != next.BaselineDir
                || !SamePath(old.SharedFile, next.SharedFile);
            StateMove? moved = null;
            var adopted = false;
            if (moveState)
                try
                {
                    moved = MoveState(old, next);
                    if (moved.Existing && File.Exists(next.SharedFile))
                    {
                        next = next.WithShared(); // the rules of the admins who use this folder already
                        adopted = true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                              or System.Text.Json.JsonException)
                {
                    return new Outcome("/settings", $"State folder not changed: {e.Message}", true);
                }
            var changes = new JsonObject();
            void Diff(string key, JsonNode? before, JsonNode? after)
            {
                if (before?.ToJsonString() != after?.ToJsonString()) changes[key] = new JsonObject { ["before"] = before, ["after"] = after };
            }
            JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)x!).ToArray());
            Diff("scan_depth", old.ScanDepth, next.ScanDepth);
            Diff("write", old.Write, next.Write);
            Diff("hidden", Arr(old.Hidden), Arr(next.Hidden));
            Diff("full_control", Arr(old.FullControl), Arr(next.FullControl));
            Diff("state", old.State, next.State);
            Diff("audit", old.Audit, next.Audit);
            Diff("baseline", old.Baseline, next.Baseline);
            if (changes.Count == 0) return new Outcome("/settings", "Nothing changed.");
            try
            {
                if (!adopted) Config.SaveShared(next.SharedFile, next); // for everyone who uses this state folder
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return new Outcome("/settings", $"Shared settings not saved to {next.SharedFile}: {e.Message}", true);
            }
            try
            {
                // the admin's own file starts as a copy of the one in use, so provider, share etc. stay
                if (target != old.File && old.File is not null && File.Exists(old.File) && !File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(old.File, target);
                }
                Config.Save(target, next);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return new Outcome("/settings", $"Settings not saved: {e.Message}", true);
            }
            M.Configure(next.Write, next.Hidden, next.FullControl);
            if (moveState) St.UseState(next with { File = target }); // the log entry below goes into the new log
            else St.ApplySettings(next with { File = target });
            St.Audit.Append(new JsonObject
            {
                ["kind"] = "settings",
                ["actor"] = St.Actor,
                ["provider"] = St.Provider.Name,
                ["share"] = St.Snap.Share,
                ["reason"] = reason.Trim(),
                ["status"] = "ok",
                ["file"] = target,
                ["shared"] = next.SharedFile,
                ["adopted"] = adopted,
                ["changes"] = changes,
                ["moved"] = moved is null ? null : new JsonArray(moved.Moved.Select(m => (JsonNode)m!).ToArray()),
            });
            if (changes.ContainsKey("scan_depth") && St.OpenShare is not null && St.Ready)
                St.Switch(St.OpenShare(St.Provider.Share)); // a provider with the new depth, scanned anew
            else if (!St.Ready)
                St.Rescan();
            else
                St.Rescan(St.Snap); // W, hidden and full-control accounts, the state folder: worked out from the last scan
            message = (adopted ? $"Now using the state folder {next.StateDir} and its settings (those of the admins who use it)."
                    : $"Settings saved to {next.SharedFile}.")
                + (changes.ContainsKey("scan_depth") && St.OpenShare is null ? " The scan depth applies at the next start." : "")
                + (moved is null ? "" : moved.Existing
                    ? adopted ? " Nothing was moved." : $" {next.StateDir} already held owlseye state: it is used from now on, nothing was moved."
                    : $" Moved {moved.Moved.Count} {(moved.Moved.Count == 1 ? "file" : "files")} to {next.StateDir}.");
        }
        St.NotifyChanged();
        return new Outcome("/settings", message);
    }
}

/// <param name="Value">what goes into the setting: a keyword, "DOMAIN\name" or a SID</param>
/// <param name="Hint">where it was found</param>
public sealed record AccountChoice(string Value, string Label, string Hint);

public sealed partial class Session
{
    (Snapshot Snap, Dictionary<string, string> Accounts)? aclAccounts;

    /// <summary>All accounts in the scanned ACLs (sid -> name), hidden ones too, once per scan.</summary>
    Dictionary<string, string> AclAccounts()
    {
        var snap = St.Snap;
        if (aclAccounts is { } c && ReferenceEquals(c.Snap, snap)) return c.Accounts;
        var o = new Dictionary<string, string>();
        foreach (var a in snap.Folders.Values.SelectMany(f => f.Aces))
            if (!o.ContainsKey(a.Sid) || o[a.Sid] == a.Sid) o[a.Sid] = a.Name != "" ? a.Name : a.Sid;
        aclAccounts = (snap, o);
        return o;
    }

    /// <summary>Accounts for the settings page's pickers: the keywords SYSTEM, Administrators and Domain Admins, the
    /// accounts in this share's ACLs (also hidden ones such as a backup group) and groups from the directory.</summary>
    public List<AccountChoice> AccountChoices(string q)
    {
        q = q.Trim();
        if (q == "") return [];
        bool Hit(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var o = new List<AccountChoice>();
        void Add(string value, string label, string hint)
        {
            if (!o.Any(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase))) o.Add(new AccountChoice(value, label, hint));
        }
        foreach (var (k, hint) in new[] { ("SYSTEM", @"NT AUTHORITY\SYSTEM"), ("Administrators", "the file server's own group"),
                     ("Domain Admins", "RID 512, in any language") })
            if (Hit(k)) Add(k, k, hint);
        lock (St.Lock)
        {
            if (St.Ready)
                foreach (var (sid, name) in AclAccounts().OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                    if (Hit(name) || Hit(sid)) Add(M.IsSid(name) ? sid : name, name, $"in the ACLs · {sid}");
        }
        if (q.Length >= 2)
        {
            try
            {
                foreach (var p in St.Provider.FindGroups(q)) Add(p.Name, p.Name, $"directory · {p.Sid}");
            }
            catch (Exception e) when (e is not OutOfMemoryException) // directory not reachable: the other choices remain
            {
            }
        }
        return o.Take(25).ToList();
    }
}
