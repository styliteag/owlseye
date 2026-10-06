// The pages' logic: builds the view models and carries out the actions.
// The UI (Blazor) only renders these and calls the actions, so all of it can be tested without a UI.

using System.Text.Json.Nodes;
using Owlseye.Providers;

namespace Owlseye.Ui;

public sealed partial class Session(State st)
{
    public State St { get; } = st;

    // --- Matrix ------------------------------------------------------------------------------

    /// <summary>The per-scan rights cache, if it fits this snapshot: the scan itself or a planned state derived from it
    /// (same groups and users). Only the parts that depend on groups and users are used for derived states.</summary>
    RightsCache? Cache(Snapshot snap) => St.Cache is { } c && c.SameMembers(snap) ? c : null;

    static CellInfo CellInfoOf(string sid, string path, Dictionary<(string Sid, string Path), Cell> cells,
        Dictionary<(string Sid, string Path), Change> changes, Dictionary<(string Sid, string Path), (string Right, string Parent)> blocked)
    {
        var key = (sid, path);
        cells.TryGetValue(key, out var c);
        if (changes.TryGetValue(key, out var ch))
        {
            var kind = ch.Auto ? "auto" : "pending";
            var what = ch.Auto && ch.After is not null ? "Added automatically" : ch.Auto ? "Removed automatically" : "Pending";
            return new CellInfo(c?.Effective, kind, $"{what}: {Labels.Short(ch.Before)} → {Labels.Short(ch.After)}", c?.Source, Before: ch.Before);
        }
        if (c is { Direct: not null } && c.Source == path)
        {
            var tip = c.Movable
                ? $"Entry here: {Labels.Label(c.Direct)}, the plain Modify entry: users can still delete, rename or move this folder, which is "
                    + "kept from moving. Choose the value again to convert it"
                : $"Entry here: {Labels.Label(c.Direct)}" + (c.Standard ? "" : " (non-standard entry)");
            return new CellInfo(c.Direct, "direct", tip, path, c.Standard, Full: c.Full, Movable: c.Movable);
        }
        if (c is { Effective: not null })
        {
            var src = string.IsNullOrEmpty(c.Source) ? "root" : c.Source;
            return new CellInfo(c.Effective, "inherited", $"Inherited from {src}: {Labels.Label(c.Effective)}", c.Source, c.Standard);
        }
        if (blocked.TryGetValue(key, out var b))
        {
            var tip = $"Blocked: {Labels.Label(b.Right)} on {(b.Parent != "" ? b.Parent : "root")} does not reach here (inheritance broken)";
            return new CellInfo(null, "blocked", tip, b.Parent, BlockedRight: b.Right);
        }
        return new CellInfo(null, "none", "No access");
    }

    /// <summary>(plan or null, error, snapshot after the pending changes, cells, changed cells).</summary>
    (Plan? Plan, string? Err, Snapshot Snap, Dictionary<(string Sid, string Path), Cell> Cells, Dictionary<(string Sid, string Path), Change> Changes) View()
    {
        Plan? plan = null;
        string? err = null;
        if (St.PendingCount > 0)
        {
            try
            {
                plan = St.Plan();
            }
            catch (PlanError e)
            {
                err = e.Message;
            }
        }
        if (plan is null) return (null, err, St.Snap, St.Cells, []);
        var changes = new Dictionary<(string, string), Change>();
        foreach (var o in plan.AclOps)
            foreach (var c in o.Changes)
                changes[(c.Sid, o.Path)] = c;
        return (plan, err, plan.SnapAfter, plan.CellsAfter, changes);
    }

    public MatrixView Matrix(string q = "")
    {
        lock (St.Lock)
        {
            var (plan, err, snap, cells, changes) = View();
            var blocked = Rights.BlockedCells(snap, cells);
            var cols = St.Columns(q);
            var folders = St.Folders(snap);
            var (odd, way) = St.Deep(snap);
            var moved = Rights.Stale(snap);
            var parents = folders.Select(f => f.Parent).OfType<string>().ToHashSet();
            var flagged = St.Findings.Select(f => M.Lower(f.Path)).ToHashSet();
            var newPaths = plan?.CreateOps.ToHashSet() ?? [];
            var cache = Cache(snap);
            var tg = cache?.Transitive ?? Rights.Transitive(snap);
            var usersIn = new Dictionary<string, string>();
            foreach (var p in cols)
                usersIn[p.Sid] = (cache is not null ? cache.MemberCount(snap, p.Sid) : Rights.MembersOf(snap, p.Sid, tg)?.Count) is { } n
                    ? n.ToString() : "?";
            // per folder: the cells, pending changes and blocked rights that concern it, for the row signature
            var byPath = new Dictionary<string, int>();
            void Mix(string path, int h) => byPath[path] = unchecked(byPath.GetValueOrDefault(path) + h); // order-independent
            foreach (var (k, c) in cells) Mix(k.Path, HashCode.Combine(k.Sid, c));
            foreach (var (k, c) in changes) Mix(k.Path, HashCode.Combine(k.Sid, c, 1));
            foreach (var (k, b) in blocked) Mix(k.Path, HashCode.Combine(k.Sid, b, 2));
            var colSig = new HashCode();
            foreach (var p in cols) colSig.Add(p.Sid);
            var colHash = colSig.ToHashCode();
            var rows = folders.Select(f => new MatrixRow(
                f,
                () => cols.Select(p => CellInfoOf(p.Sid, f.Path, cells, changes, blocked)).ToList(),
                HashCode.Combine(byPath.GetValueOrDefault(f.Path), colHash),
                St.PendingFolders.TryGetValue(f.Path, out var prot) ? prot : f.Protected,
                St.PendingFolders.ContainsKey(f.Path) || St.PendingClear.Contains(f.Path) || St.PendingReinherit.Contains(f.Path),
                newPaths.Contains(f.Path),
                odd.Contains(f.Path) ? "odd" : way.Contains(f.Path) ? "way" : "",
                parents.Contains(f.Path),
                flagged.Contains(M.Lower(f.Path)),
                f.Path.Split('\\')[0],
                St.PendingFolders.ContainsKey(f.Path),
                moved.Contains(f.Path),
                Rights.Kept(f))).ToList();
            return new MatrixView(cols, rows, usersIn, err, q, St.PendingCount, St.PendingNew.Keys.ToList(), St.Drift.Count,
                St.Cfg.MaxLevel, St.Extra.Keys.ToHashSet());
        }
    }

    bool ProtectedAfter(Folder f) => St.PendingFolders.TryGetValue(f.Path, out var v) ? v : f.Protected;

    public CellPanel CellPanel(string sid, string path)
    {
        lock (St.Lock)
        {
            var (_, _, snap, cells, changes) = View();
            if (!St.Principals().TryGetValue(sid, out var p) || !snap.Folders.TryGetValue(path, out var f))
                throw new UserError("Not found", 404);
            var info = CellInfoOf(sid, path, cells, changes, Rights.BlockedCells(snap, cells));
            var current = cells.GetValueOrDefault((sid, path))?.Direct;
            var members = Rights.MembersOf(snap, sid, Cache(snap)?.Transitive);
            var writable = !f.OtherAces;
            var own = f.Explicit.Where(a => a.Sid == sid).ToList(); // what is really there, for a special entry
            return new CellPanel(p, f, info, writable ? [null, .. M.Cells] : [], current,
                members?.Select(u => snap.Users[u]).ToList(),
                St.CanToggleInheritance(f) && !ProtectedAfter(f),
                St.CanToggleInheritance(f) && ProtectedAfter(f),
                St.Cfg.MaxLevel,
                own.Count > 0 && !info.Standard ? Labels.Describe(own) : null);
        }
    }

    public FolderPanel FolderPanel(string path)
    {
        lock (St.Lock)
        {
            var (_, _, snap, cells, _) = View();
            if (!snap.Folders.TryGetValue(path, out var f)) throw new UserError("Not found", 404);
            var principals = St.Principals();
            var grants = cells.Where(kv => kv.Key.Path == path && kv.Value.Effective is not null)
                .Select(kv => new Grant(principals.TryGetValue(kv.Key.Sid, out var p) ? p.Short : kv.Key.Sid, kv.Value.Effective!, kv.Value.Source))
                .OrderBy(g => -M.Rank(g.Right)).ThenBy(g => M.Lower(g.Name), M.Ci).ToList();
            return new FolderPanel(
                f, St.Snap.Share,
                St.CanAddFolder(path),
                St.PendingNew.Where(kv => kv.Value == path).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
                St.PendingNew.ContainsKey(path),
                ProtectedAfter(f),
                St.PendingFolders.ContainsKey(path),
                St.CanToggleInheritance(f) && !St.PendingClear.Contains(path),
                St.PendingClear.Contains(path),
                St.CanClear(path),
                grants,
                St.Findings.Where(x => M.Lower(x.Path) == M.Lower(path)).ToList(),
                HiddenEntries(f),
                [.. new[] { M.CreatorOwner, M.OwnerRights }.Select(sid => new OwnerEntryView(sid,
                    St.Pending.TryGetValue((sid, path), out var v) ? v : Rights.OwnerEntryOf(f, sid),
                    Rights.OwnerEntryOf(f, sid, inherited: true),
                    St.Pending.ContainsKey((sid, path))))],
                !f.OtherAces && !St.PendingClear.Contains(path) && St.Snap.Folders.ContainsKey(path),
                Rights.Stale(St.Snap).Contains(path),
                St.PendingReinherit.Contains(path),
                Rights.Kept(f),
                St.PendingKept.ContainsKey(path),
                Rights.StillMovable(f).Select(a => principals.TryGetValue(a.Sid, out var p) ? p.Short : a.Name).ToList(),
                !f.OtherAces && !St.PendingClear.Contains(path) && St.Snap.Folders.ContainsKey(path)
                    && (f.Explicit.Any(a => a.Allow && !M.IsOwnerSid(a.Sid) && Rights.Classify(f.Explicit.Where(b => b.Sid == a.Sid && b.Allow).ToList()).Value is "W" or "W|")
                        || St.PendingKept.ContainsKey(path)));
        }
    }

    /// <summary>Entries of hidden accounts on the folder as the scan read them, per account and own/inherited; Creator
    /// Owner and Owner Rights have their own lines in the panel.</summary>
    static List<HiddenEntry> HiddenEntries(Folder f) =>
        f.Aces.Where(a => a.Allow && !M.IsOwnerSid(a.Sid) && M.IsHidden(a.Sid, a.Name))
            .GroupBy(a => (a.Sid, a.Inherited))
            .Select(g => new HiddenEntry(g.First().Name[(g.First().Name.LastIndexOf((char)92) + 1)..],
                Rights.Classify(g.ToList()).Value ?? "*", g.Key.Inherited))
            .OrderBy(h => h.Inherited).ThenBy(h => M.Lower(h.Name), M.Ci).ToList();

    /// <summary>An owner entry on a folder (M.OwnerEntryValues): Creator Owner, what whoever creates a file or folder below
    /// gets on it; Owner Rights, what the owner of each file and folder gets instead of reading and changing its
    /// permissions. null = no entry. Pending like a cell, with preview, log and undo.</summary>
    public void SetOwnerEntry(string sid, string path, string? value)
    {
        try
        {
            lock (St.Lock)
            {
                if (!M.IsOwnerSid(sid)) throw new UserError("Only Creator Owner and Owner Rights are set this way");
                if (!St.Snap.Folders.TryGetValue(path, out var f)) throw new UserError("Unknown folder");
                if (value is not null && !M.OwnerEntryValues(sid).Contains(value))
                    throw new UserError($"Value must be empty or one of {string.Join(", ", M.OwnerEntryValues(sid))}");
                var key = (sid, path);
                var had = St.Pending.TryGetValue(key, out var prev);
                if (value == Rights.OwnerEntryOf(f, sid)) St.Pending.Remove(key);
                else St.Pending[key] = value;
                CheckPlan(() =>
                {
                    if (had) St.Pending[key] = prev;
                    else St.Pending.Remove(key);
                });
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    /// <summary>Pending changes must stay plannable; otherwise revert the last one.</summary>
    void CheckPlan(Action undo)
    {
        try
        {
            St.Plan();
        }
        catch (PlanError e)
        {
            undo();
            throw new UserError(e.Message);
        }
    }

    /// <summary>right: null = cycle to the next value (click), "" = no entry, else R| R W| W.</summary>
    public void SetCell(string sid, string path, string? right)
    {
        try
        {
            lock (St.Lock)
            {
                if (!St.Principals().ContainsKey(sid)) throw new UserError("Unknown account");
                if (!St.Snap.Folders.ContainsKey(path) && !St.PendingNew.ContainsKey(path)) throw new UserError("Unknown folder");
                if (right is not null && right != "" && !M.Cells.Contains(right))
                    throw new UserError($"Right must be empty or one of {string.Join(", ", M.Cells)}");
                var key = (sid, path);
                St.Cells.TryGetValue(key, out var c);
                var orig = c?.Direct;
                var standard = c?.Standard ?? true;
                var had = St.Pending.TryGetValue(key, out var prev);
                var want = right is null ? Labels.Next(had ? prev : orig) : (right == "" ? null : right);
                if (want == orig && standard) St.Pending.Remove(key);
                else St.Pending[key] = want;
                CheckPlan(() =>
                {
                    if (had) St.Pending[key] = prev;
                    else St.Pending.Remove(key);
                });
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    /// <summary>hits: the result of an earlier search for the same q (a directory search per redraw would be too much).</summary>
    public GroupsPanel Groups(string q = "", IReadOnlyList<Principal>? hits = null)
    {
        hits ??= q.Trim() != "" ? St.Provider.FindGroups(q) : [];
        return new GroupsPanel(q, hits, St.Principals().Keys.ToHashSet(), Labels.HiddenHint(q));
    }

    public void AddColumn(string sid, string gq)
    {
        lock (St.Lock)
        {
            var hit = St.Provider.FindGroups(gq).FirstOrDefault(p => p.Sid == sid) ?? throw new UserError("Group not found");
            St.AddColumn(hit);
        }
        St.NotifyChanged();
    }

    public void SetInheritance(string path, bool inherit)
    {
        try
        {
            lock (St.Lock)
            {
                if (!St.Snap.Folders.TryGetValue(path, out var f) || !St.CanToggleInheritance(f))
                    throw new UserError("Inheritance can only be changed on existing folders below the root");
                var protect = !inherit;
                var had = St.PendingFolders.TryGetValue(path, out var prev);
                if (protect == f.Protected) St.PendingFolders.Remove(path);
                else St.PendingFolders[path] = protect;
                CheckPlan(() =>
                {
                    if (had) St.PendingFolders[path] = prev;
                    else St.PendingFolders.Remove(path);
                });
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    /// <summary>Re-apply inheritance on a moved folder (Rights.Stale): owlseye writes it as it is and Windows takes the
    /// parent's inherited entries again, also below it; again = take back.</summary>
    public void ReinheritFolder(string path)
    {
        try
        {
            lock (St.Lock)
            {
                if (St.PendingReinherit.Remove(path)) return;
                if (!Rights.Stale(St.Snap).Contains(path))
                    throw new UserError("This folder's inherited entries already are what its parent passes down");
                St.PendingReinherit.Add(path);
                CheckPlan(() => St.PendingReinherit.Remove(path));
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    /// <summary>Reset a folder to the default (inherits, no entries of its own); again = take back.</summary>
    public void ClearFolder(string path)
    {
        try
        {
            lock (St.Lock)
            {
                if (St.PendingClear.Contains(path)) St.PendingClear.Remove(path);
                else
                {
                    if (!St.CanClear(path)) throw new UserError("Only folders below the root with entries of their own can be cleared");
                    St.PendingClear.Add(path);
                    CheckPlan(() => St.PendingClear.Remove(path));
                }
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    public string NewFolder(string parent, string name)
    {
        name = name.Trim();
        var path = parent != "" ? $"{parent}\\{name}" : name;
        try
        {
            lock (St.Lock)
            {
                if (St.PendingNew.ContainsKey(path)) throw new UserError($"{path} is already pending");
                St.PendingNew[path] = parent;
                CheckPlan(() => St.PendingNew.Remove(path));
            }
        }
        finally
        {
            St.NotifyChanged();
        }
        return path;
    }

    /// <summary>Cancels the new folder and everything pending below it; returns its parent.</summary>
    public string CancelNewFolder(string path)
    {
        string parent;
        lock (St.Lock)
        {
            var gone = St.PendingNew.Keys.Where(p => p == path || M.Lower(p).StartsWith(M.Lower(path) + "\\", StringComparison.Ordinal)).ToHashSet();
            parent = St.PendingNew.GetValueOrDefault(path, "");
            St.DropNewFolders(gone);
        }
        St.NotifyChanged();
        return parent;
    }

    public void SetDepth(int depth)
    {
        if (!State.Depths.Contains(depth)) throw new UserError($"Depth must be {State.Depths[0]}–{State.Depths[^1]}");
        St.SetDepth(depth);
    }

    /// <summary>Shows the hidden accounts (SYSTEM, Administrators, Domain Admins, those hidden in the settings) as matrix
    /// columns that can be set, or hides them again; remembered for this admin. Worked out from the last scan.</summary>
    public void SetShowHidden(bool show)
    {
        lock (St.Lock)
        {
            if (M.ShowHidden == show) return;
            var principals = St.Principals();
            if (!show && St.Pending.Keys.Any(k => !M.IsOwnerSid(k.Sid) && M.IsHidden(k.Sid, principals.GetValueOrDefault(k.Sid)?.Name)))
                throw new UserError("Apply or discard the pending changes of hidden accounts first");
            M.ShowHidden = show;
            Settings.Save(new JsonObject { ["show_hidden"] = show });
            if (St.Ready) St.Rescan(St.Snap);
        }
    }

    public void Discard()
    {
        lock (St.Lock) St.Clear();
        St.NotifyChanged();
    }

    public Outcome Rescan()
    {
        St.Rescan();
        return new Outcome("/matrix", "Rescanned.");
    }

    // --- Preview, apply, undo ------------------------------------------------------------------

    public PreviewView Preview()
    {
        Plan? plan;
        string? err = null;
        List<Principal> required;
        lock (St.Lock)
        {
            try
            {
                plan = St.Plan();
            }
            catch (PlanError e)
            {
                plan = null;
                err = e.Message;
            }
            required = St.Ready ? Rights.RequiredFullControl(St.Snap) : [];
        }
        var gained = plan?.Impact.Where(Planner.Gained).ToList() ?? [];
        var lost = plan?.Impact.Where(i => !Planner.Gained(i)).ToList() ?? [];
        var cells = plan?.AclOps.Sum(o => o.Changes.Count) ?? 0;
        return new PreviewView(plan, err, gained, lost, cells, plan is not null ? State.PlanHash(plan) : "",
            plan is not null ? Planner.HiddenLosses(plan) : [], required);
    }

    /// <summary>Checks against the file system as it is now, not against the snapshot: the new ACL is computed from the
    /// snapshot and must not overwrite entries changed in the meantime.</summary>
    string? Conflict(Plan plan)
    {
        foreach (var path in plan.CreateOps)
            if (St.Provider.FolderExists(path)) return $"{path} already exists.";
        foreach (var o in plan.AclOps)
        {
            if (o.NewFolder) continue;
            var f = St.Snap.Folders[o.Path];
            var paths = new List<string> { o.Path };
            if (o.ProtectedBefore != o.ProtectedAfter && f.Parent is not null) paths.Add(f.Parent);
            foreach (var path in paths)
            {
                St.Progress.Set(path: path);
                var (prot, aces) = St.Provider.FolderAcl(path);
                var snapF = St.Snap.Folders[path];
                if (prot != snapF.Protected || !Acl.SameKeys(aces, snapF.Aces))
                    return $"The ACL of {(path != "" ? path : "the share root")} was changed outside owlseye.";
            }
        }
        return null;
    }

    JsonObject Who(string reason) => new()
    {
        ["actor"] = St.Actor, ["provider"] = St.Provider.Name, ["share"] = St.Snap.Share, ["reason"] = reason.Trim(),
    };

    static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var o = new JsonObject();
        foreach (var (k, v) in a) o[k] = v?.DeepClone();
        foreach (var (k, v) in b) o[k] = v?.DeepClone();
        return o;
    }

    static JsonArray Strs(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)x!).ToArray());

    /// <summary>New folders first, then the ACLs (parents first). Stops at the first failed write. Each write is logged
    /// (as a step of `run`) right away and carried into the desired state within seconds: if owlseye is stopped halfway
    /// (window closed, logoff), the log matches the file system and the desired state lacks at most the last writes.</summary>
    (List<string> Created, List<AclOp> Written, List<string> Errors) Execute(Plan plan, string run)
    {
        var doneCreate = new List<string>();
        var doneAcl = new List<AclOp>();
        var errors = new List<string>();
        foreach (var path in plan.CreateOps)
        {
            St.Progress.Set(phase: "Creating folders", path: path, done: doneCreate.Count);
            try
            {
                St.Provider.CreateFolder(path);
            }
            catch (Exception e) // log everything that goes wrong
            {
                return (doneCreate, doneAcl, [$"create {path}: {e.Message}"]);
            }
            doneCreate.Add(path);
            St.Audit.Append(new JsonObject { ["kind"] = "change_step", ["run"] = run, ["create"] = path });
        }
        // The desired state of a large share is a large file: it is saved after at most 25 written folders or 2 seconds,
        // and at the end or on an error, not after every folder. Stopped hard in between, the last few writes are in the
        // log but not in the desired state; the comparison then lists them, to keep.
        var unsaved = new List<AclOp>();
        var sinceSave = System.Diagnostics.Stopwatch.StartNew();
        void SaveDesired()
        {
            if (unsaved.Count > 0 && errors.Count == 0) // once it could not be saved, do not try again
            {
                var ops = unsaved.ToList();
                try
                {
                    St.Baseline.Update(St.Snap.Share, d => Owlseye.Drift.Applied(Owlseye.Drift.Upgrade(d, St.Snap), ops), St.Actor);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or BaselineError)
                {
                    errors.Add($"desired state not saved: {e.Message}");
                }
            }
            unsaved.Clear();
            sinceSave.Restart();
        }
        foreach (var o in plan.AclOps)
        {
            St.Progress.Set(phase: "Writing ACLs", path: o.Path, done: doneCreate.Count + doneAcl.Count);
            try
            {
                // checked against the scan on the very handle that writes: a folder swapped in by a rename since the
                // conflict check, or an ACL changed in the meantime, is not overwritten
                St.Provider.SetFolderAcl(o.Path, o.ProtectedAfter, o.After, o.NewFolder ? null : St.Snap.Folders.GetValueOrDefault(o.Path));
            }
            catch (Exception e)
            {
                SaveDesired(); // what was written before stays recorded
                return (doneCreate, doneAcl, [$"ACL of {(o.Path != "" ? o.Path : "the share root")}: {e.Message}", .. errors]);
            }
            doneAcl.Add(o);
            St.Audit.Append(new JsonObject { ["kind"] = "change_step", ["run"] = run, ["acl_op"] = Json.ToNode(o) }); // log first
            unsaved.Add(o);
            if (unsaved.Count >= 25 || sinceSave.ElapsedMilliseconds > 2000) SaveDesired();
        }
        SaveDesired();
        return (doneCreate, doneAcl, errors);
    }

    /// <summary>Apply the plan shown in the preview (phash). Runs long: call it off the UI thread.</summary>
    public Outcome Apply(string reason, string phash)
    {
        int count, total;
        string? error;
        JsonObject entry;
        lock (St.Lock)
        {
            var plan = St.Plan();
            if (State.PlanHash(plan) != phash) return new Outcome("/preview", "The plan has changed, please review it again.", true);
            total = plan.AclOps.Count + plan.CreateOps.Count;
            using (St.Progress.Task("apply", "Checking the ACLs for outside changes", total))
            {
                var why = Conflict(plan);
                if (why is not null)
                {
                    St.Rescan();
                    return new Outcome("/preview", $"{why} Rescanned, please review again.", true);
                }
                var who = Who(reason);
                var start = St.Audit.Append(Merge(new JsonObject { ["kind"] = "change_start" }, Merge(who, new JsonObject
                {
                    ["total"] = total,
                    ["planned_create"] = Strs(plan.CreateOps),
                    ["planned_acl"] = Strs(plan.AclOps.Select(o => o.Path)),
                })));
                var (doneCreate, doneAcl, errors) = Execute(plan, start.Str("id")!);
                error = errors.Count > 0 ? string.Join("; ", errors) : null;
                entry = St.Audit.Append(Merge(new JsonObject { ["kind"] = "change", ["run"] = start.Str("id") }, Merge(who, new JsonObject
                {
                    ["status"] = error is not null ? "error" : "ok",
                    ["error"] = error,
                    ["acl_ops"] = new JsonArray(doneAcl.Select(o => Json.ToNode(o)).ToArray()),
                    ["create_ops"] = Strs(doneCreate),
                    ["impact"] = new JsonArray(plan.Impact.Select(i => Json.ToNode(i)).ToArray()),
                })));
                St.Clear();
                St.Rescan();
                count = doneAcl.Count + doneCreate.Count;
            }
        }
        if (error is not null) return new Outcome("/matrix", $"Error after {count} of {total} operations: {error}", true);
        return new Outcome("/matrix", $"{count} changes applied (log {entry.Str("id")}). Users get them at their next access.");
    }

    public static AclOp AclOpFromJson(JsonObject d)
    {
        static T Need<T>(T? v, string key) where T : class => v ?? throw new KeyNotFoundException(key);

        static Ace AceOf(JsonNode? n)
        {
            var a = n as JsonObject ?? throw new InvalidCastException("ACE is not an object");
            return new Ace(Need(a.Str("sid"), "sid"), Need(a.Str("name"), "name"), Need(a.Str("kind"), "kind"),
                (uint)(a.Long("mask") ?? throw new KeyNotFoundException("mask")),
                a.Bool("allow") ?? true, a.Bool("inherited") ?? false, (int)(a.Long("flags") ?? 0));
        }

        var changes = (d.Arr("changes") ?? []).Select(n =>
        {
            var c = n as JsonObject ?? throw new InvalidCastException("change is not an object");
            var sid = Need(c.Str("sid"), "sid");
            if (!M.IsSid(sid)) throw new FormatException($"not a SID: {sid}");
            return new Change(sid, Need(c.Str("name"), "name"), Need(c.Str("path"), "path"),
                c.Str("before"), c.Str("after"), c.Bool("auto") ?? false);
        }).ToList();
        return new AclOp(
            Need(d.Str("path"), "path"),
            d.Bool("protected_before") ?? throw new KeyNotFoundException("protected_before"),
            d.Bool("protected_after") ?? throw new KeyNotFoundException("protected_after"),
            Need(d.Arr("before"), "before").Select(AceOf).ToList(),
            Need(d.Arr("after"), "after").Select(AceOf).ToList(),
            changes,
            d.Bool("new_folder") ?? false,
            d.Bool("cleared") ?? false,
            d.Bool("reinherited") ?? false,
            d.Bool("kept_before") ?? false,
            d.Bool("kept_after") ?? false);
    }

    public Outcome Undo(string entryId)
    {
        var e = St.Audit.Get(entryId) ?? throw new UserError("Not found", 404);
        if (!M.SameShare(e.Str("share") ?? "", St.Snap.Share)) throw new UserError("This log entry belongs to another share");
        List<AclOp> ops;
        try
        {
            ops = (e.Arr("acl_ops") ?? []).Select(a => AclOpFromJson(a as JsonObject ?? throw new InvalidCastException("not an object"))).ToList();
        }
        catch (Exception err) when (err is KeyNotFoundException or InvalidCastException or InvalidOperationException or FormatException)
        {
            throw new UserError($"Log entry cannot be read (older format?): {err.Message}");
        }
        lock (St.Lock)
        {
            St.Clear();
            foreach (var o in ops)
            {
                if (!St.Snap.Folders.TryGetValue(o.Path, out var f)) continue;
                foreach (var c in o.Changes)
                {
                    var now = M.IsOwnerSid(c.Sid) ? Rights.OwnerEntryOf(f, c.Sid) : St.Cells.GetValueOrDefault((c.Sid, o.Path))?.Direct;
                    if (now != c.Before) St.Pending[(c.Sid, o.Path)] = c.Before;
                    if (!M.IsOwnerSid(c.Sid) && !St.Snap.Principals.ContainsKey(c.Sid)) St.Extra[c.Sid] = Resolved(c.Sid, c.Name);
                }
                if (o.KeptBefore != o.KeptAfter && Rights.Kept(f) != o.KeptBefore) St.PendingKept[o.Path] = o.KeptBefore;
                if (f.Protected != o.ProtectedBefore && St.CanToggleInheritance(f)) St.PendingFolders[o.Path] = o.ProtectedBefore;
            }
        }
        St.NotifyChanged();
        return new Outcome("/preview");
    }

    // --- Desired/actual: outside changes ---------------------------------------------------

    public DriftView DriftPage()
    {
        lock (St.Lock)
        {
            var items = ResolvedNames(St.Drift);
            var imp = Owlseye.Drift.ImpactOf(St.Snap, St.Drift);
            return new DriftView(items, imp, St.Baseline.Info(St.Snap.Share), St.BaselineError, St.Desired, St.Audit.Path,
                St.Snap.TakenAt, St.Snap.Share);
        }
    }

    /// <summary>Delete the desired-state file; the rescan adopts the current state as desired (baseline_init).</summary>
    public Outcome DriftReset(string reason)
    {
        bool deleted;
        lock (St.Lock)
        {
            try
            {
                deleted = St.Baseline.Delete(St.Snap.Share);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Outcome("/drift", $"Desired state not deleted: {e.Message}", true);
            }
            St.Audit.Append(Merge(new JsonObject { ["kind"] = "baseline_reset" }, new JsonObject
            {
                ["actor"] = St.Actor,
                ["provider"] = St.Provider.Name,
                ["share"] = St.Snap.Share,
                ["reason"] = reason.Trim(),
                ["status"] = "ok",
                ["path"] = St.Baseline.PathOf(St.Snap.Share) ?? "",
            }));
            St.Rescan();
        }
        if (!deleted) return new Outcome("/drift", "There was no desired state file; the current state is now the desired state.");
        return new Outcome("/drift", "Desired state deleted. The current state is the new desired state.");
    }

    /// <summary>At most this many kept items go into the log entry (with the full count); a large share can have tens
    /// of thousands.</summary>
    public const int MaxLogged = 500;

    List<DriftItem> Selected(IEnumerable<string>? keys)
    {
        var want = (keys ?? []).ToHashSet();
        return St.Drift.Where(d => want.Contains(d.Key)).ToList();
    }

    public Outcome DriftAccept(IEnumerable<string>? keys, string reason)
    {
        JsonObject entry;
        int n;
        lock (St.Lock)
        {
            var items = Selected(keys);
            if (items.Count == 0) return new Outcome("/drift", "Nothing selected.");
            try
            {
                St.Baseline.Update(St.Snap.Share, d => Owlseye.Drift.Accept(Owlseye.Drift.Upgrade(d, St.Snap), items), St.Actor);
            }
            catch (BaselineError e)
            {
                return new Outcome("/drift", $"Desired state not saved: {e.Message}", true);
            }
            entry = St.Audit.Append(new JsonObject
            {
                ["kind"] = "drift_accept",
                ["actor"] = St.Actor,
                ["provider"] = St.Provider.Name,
                ["share"] = St.Snap.Share,
                ["reason"] = reason.Trim(),
                ["status"] = "ok",
                ["accepted"] = new JsonArray(items.Take(MaxLogged).Select(d => Json.ToNode(d)).ToArray()),
                ["accepted_count"] = items.Count, // the list is cut at MaxLogged items: one log line, not megabytes
            });
            n = items.Count;
            St.Rescan();
        }
        return new Outcome("/drift", $"{n} outside change(s) kept as the new desired state (log {entry.Str("id")}).");
    }

    public Outcome DriftRevert(IEnumerable<string>? keys)
    {
        lock (St.Lock)
        {
            var items = Selected(keys).Where(Owlseye.Drift.Revertible).ToList();
            if (items.Count == 0) return new Outcome("/drift", "Nothing selected that can be reverted.");
            var r = Owlseye.Drift.RevertOf(St.Snap, St.Desired, items);
            St.Clear();
            foreach (var (k, v) in r.NewFolders) St.PendingNew[k] = v;
            foreach (var (k, v) in r.Cells) St.Pending[k] = v;
            foreach (var (k, v) in r.Folders) St.PendingFolders[k] = v;
            foreach (var (k, v) in r.Extra) St.Extra[k] = Resolved(k, v.Name);
        }
        St.NotifyChanged();
        return new Outcome("/preview");
    }

    /// <summary>An account that comes from a file (desired state, log), not from the scan: shown and planned under the
    /// name the system resolves now, never under the name in the file (which anyone who can write the admin share could
    /// have set). Unresolvable SIDs get kind "unknown", and the planner gives those no rights.</summary>
    Principal Resolved(string sid, string fileName) =>
        St.Provider.ResolveAccount(sid) is { } p
            ? (p.Kind == "unknown" ? p with { Name = $"{sid} (unresolved; the file calls it {fileName})" } : p)
            : new Principal(sid, fileName, "group"); // provider cannot resolve (test doubles)

    /// <summary>Drift items name accounts that are no longer in the scan by the resolved name too.</summary>
    List<DriftItem> ResolvedNames(IEnumerable<DriftItem> items) => items.Select(d =>
        d.Sid == "" || St.Snap.Principals.ContainsKey(d.Sid) ? d : d with { Name = Resolved(d.Sid, d.Name).Name }).ToList();

    // --- Pages: users, folders, findings, log, share -----------------------------------------

    public ShareView SharePage()
    {
        var recent = Settings.RecentShares(St.Provider.Name).Where(p => M.Lower(p) != M.Lower(St.Snap.Share)).ToList();
        return new ShareView(St.Snap.Share, St.Provider.Name, St.Snap.TakenAt, recent, St.OpenShare is not null, St.PendingCount);
    }

    /// <summary>Open another folder/share. The scan runs first; if it fails, the current share stays open. Runs long.</summary>
    public Outcome OpenShare(string path)
    {
        path = path.Trim().Trim('"').Trim();
        if (St.OpenShare is null) throw new UserError("The share is fixed in this mode (demo/sim)");
        if (path == "") return new Outcome("/share", "Enter a folder.", true);
        if (!St.Ready) // the share from the start could not be read (loading page): this one instead
        {
            IProvider provider;
            try
            {
                provider = St.OpenShare(path);
            }
            catch (Exception e)
            {
                return new Outcome("/matrix", $"Cannot open {path}: {e.Message}", true);
            }
            if (!St.Loading)
            {
                Settings.RememberShare(provider.Name, provider.Share); // as at the start, before it is read
                St.LoadInBackground(provider);
            }
            return new Outcome("/matrix");
        }
        try
        {
            St.Switch(St.OpenShare(path));
        }
        catch (Exception e) // show any reason (missing, access denied, not a share) to the admin
        {
            return new Outcome("/share", $"Cannot open {path}: {e.Message}", true);
        }
        Settings.RememberShare(St.Provider.Name, St.Snap.Share);
        return new Outcome("/matrix", $"Opened {St.Snap.Share}.");
    }

    public UsersView Users(string q = "", string u = "")
    {
        lock (St.Lock)
        {
            var lst = St.Snap.Users.Values.Where(x => (x.Sam + x.Display).ToLowerInvariant().Contains(q.ToLowerInvariant(), StringComparison.Ordinal))
                .OrderBy(x => M.Lower(x.Display), M.Ci).ToList();
            var sel = St.Snap.Users.GetValueOrDefault(u);
            var detail = new List<UserDetail>();
            if (sel is not null)
            {
                var cache = Cache(St.Snap);
                var eff = (cache?.UserRights ?? Rights.UserRights(St.Snap, cells: St.Cells)).GetValueOrDefault(sel.Dn) ?? [];
                foreach (var f in St.Folders())
                    if (eff.TryGetValue(f.Path, out var r))
                        detail.Add(new UserDetail(f, r, Rights.Via(St.Snap, sel.Dn, f.Path, St.Cells, cache?.UserSids)));
            }
            return new UsersView(lst, sel, detail, q);
        }
    }

    public FolderView FolderPage(string path)
    {
        lock (St.Lock)
        {
            if (!St.Snap.Folders.TryGetValue(path, out var f)) throw new UserError("Not found", 404);
            var ur = Cache(St.Snap)?.UserRights ?? Rights.UserRights(St.Snap, cells: St.Cells);
            var who = ur.Where(kv => kv.Value.ContainsKey(path)).Select(kv => (St.Snap.Users[kv.Key], kv.Value[path]))
                .OrderBy(t => -M.Rank(t.Item2)).ThenBy(t => M.Lower(t.Item1.Display), M.Ci).ToList();
            var fnd = St.Findings.Where(x => M.Lower(x.Path) == M.Lower(path)).ToList();
            var entries = f.Explicit.Select(a => new FolderEntry(a, a.Allow ? Rights.Classify([a]).Value : null)).ToList();
            return new FolderView(f, St.Snap.Share, who, fnd, entries);
        }
    }

    public IReadOnlyList<Finding> FindingsPage() => St.Findings;

    public FindingChecks FindingsChecks()
    {
        lock (St.Lock)
        {
            var kept = St.Snap.Folders.Values.Where(Rights.Kept).ToList();
            return new FindingChecks(St.Snap.Folders.Count, Rights.Stale(St.Snap).Count, kept.Count, kept.Count(f => Rights.StillMovable(f).Count > 0));
        }
    }

    /// <summary>Users cannot delete, rename or move the folder (true), or can again (false): every W and W| on it is
    /// written in that form (Acl.SetKept), also W set there later. Again with the current state = take back.</summary>
    public void SetKept(string path, bool kept)
    {
        try
        {
            lock (St.Lock)
            {
                if (!St.Snap.Folders.TryGetValue(path, out var f)) throw new UserError("Unknown folder");
                var had = St.PendingKept.TryGetValue(path, out var prev);
                // a kept folder whose W are not all converted yet can be kept again: that converts the rest
                if (kept == Rights.Kept(f) && !(kept && Rights.StillMovable(f).Count > 0)) St.PendingKept.Remove(path);
                else St.PendingKept[path] = kept;
                CheckPlan(() =>
                {
                    if (had) St.PendingKept[path] = prev;
                    else St.PendingKept.Remove(path);
                });
            }
        }
        finally
        {
            St.NotifyChanged();
        }
    }

    public AuditView AuditPage() => new(St.Audit.Path, St.Audit.Entries());
}
