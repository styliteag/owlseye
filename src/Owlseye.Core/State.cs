// Session state: snapshot, open changeset, desired state, deviations.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Owlseye.Providers;

namespace Owlseye;

public sealed class State
{
    public static readonly IReadOnlyList<int> Depths = Enumerable.Range(1, 10).ToList(); // selectable matrix depths

    /// <summary>Reentrant (Monitor).</summary>
    public readonly object Lock = new();
    readonly object loadLock = new(); // not Lock: the scan holds that until it is done

    public State(Config cfg, IProvider provider)
    {
        var depth = Settings.Depth();
        Cfg = depth is { } d && Depths.Contains(d) ? cfg with { MaxLevel = d } : cfg;
        Provider = provider;
        Audit = new AuditLog(cfg.AuditPath);
        Baseline = new BaselineStore(cfg.BaselineDir);
        Actor = provider.WhoAmI();
    }

    public Config Cfg { get; private set; }
    public IProvider Provider { get; private set; }
    public AuditLog Audit { get; private set; }
    public BaselineStore Baseline { get; private set; }

    public OrderedDictionary<(string Sid, string Path), string?> Pending { get; } = []; // (sid, path) -> desired explicit entry
    public OrderedDictionary<string, bool> PendingFolders { get; } = []; // path -> inheritance broken?
    public OrderedDictionary<string, string> PendingNew { get; } = []; // new folder -> parent folder
    public HashSet<string> PendingClear { get; } = []; // reset folder to the default (inherits, no own entries)
    public HashSet<string> PendingReinherit { get; } = []; // write as it is: Windows takes the parent's inherited entries again
    public OrderedDictionary<string, Principal> Extra { get; private set; } = []; // columns for groups not yet in any ACL

    /// <summary>Builds a provider for another share path (local/windows); null = the share is fixed.</summary>
    public Func<string, IProvider>? OpenShare { get; set; }

    /// <summary>The config.json was given with --config: the settings page may only save there.</summary>
    public bool ConfigFromCommandLine { get; set; }

    /// <summary>Another state folder (settings page): the log and the desired state are read and written there from now on.</summary>
    public void UseState(Config cfg)
    {
        lock (Lock)
        {
            Cfg = cfg with { MaxLevel = Cfg.MaxLevel };
            Audit = new AuditLog(cfg.AuditPath);
            Baseline = new BaselineStore(cfg.BaselineDir);
        }
    }

    /// <summary>New settings from the settings page (M.Configure is done by the caller); the matrix depth stays.</summary>
    public void ApplySettings(Config cfg)
    {
        lock (Lock) Cfg = cfg with { MaxLevel = Cfg.MaxLevel };
    }

    public string Actor { get; }

    /// <summary>What the start did that the admin should know (e.g. "Sim created: …"); shown once by the UI.</summary>
    public string? StartNotice { get; set; }
    public Progress Progress { get; } = new();
    public bool Ready { get; private set; } // first scan done; until then the UI shows its progress instead of the pages
    public bool Loading { get; private set; }
    public string LoadError { get; private set; } = "";

    public Snapshot Snap { get; private set; } = new("", [], [], [], []);
    public Dictionary<(string Sid, string Path), Cell> Cells { get; private set; } = [];
    public List<Finding> Findings { get; private set; } = [];
    public string BaselineError { get; private set; } = "";
    public Desired Desired { get; private set; } = new();
    public List<DriftItem> Drift { get; private set; } = [];

    /// <summary>Raised after a scan, a share switch or a change of pending changes (for the UI).</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>First scan of the share.</summary>
    public void Load()
    {
        string error;
        try
        {
            Rescan();
            error = "";
            Ready = true;
        }
        catch (Exception e) // share gone, access denied, directory down: shown with a retry
        {
            error = e.Message != "" ? e.Message : e.GetType().Name;
        }
        Loading = false;
        LoadError = error; // last: the progress page reloads once it sees ready or an error
        NotifyChanged();
    }

    /// <summary>Load() in a thread, so the window opens at once and shows the progress. Again after a failure,
    /// possibly with another share; never while loading or once loaded.</summary>
    public void LoadInBackground(IProvider? provider = null)
    {
        lock (loadLock)
        {
            if (Ready || Loading) return;
            Loading = true;
            LoadError = "";
            Provider = provider ?? Provider;
        }
        NotifyChanged();
        new Thread(Load) { Name = "first-scan", IsBackground = true }.Start();
    }

    /// <summary>Open another share. Scanned first: if that fails, nothing changes. Pending changes are dropped.</summary>
    public void Switch(IProvider provider)
    {
        lock (Lock)
        {
            Snapshot snap;
            using (Progress.Task("scan", "Reading folders"))
                snap = provider.Scan(Progress);
            Provider = provider;
            Clear();
            Rescan(snap);
        }
    }

    public void Rescan(Snapshot? snap = null)
    {
        lock (Lock)
        {
            using (Progress.Task("scan", snap is null ? "Reading folders" : "Evaluating the rights"))
            {
                Snap = snap ?? Provider.Scan(Progress);
                lastPlan = null; // also for the same snapshot: settings such as W change the plan
                Progress.Set(phase: "Evaluating the rights", path: "");
                Cells = Rights.Matrix(Snap);
                Cache = new RightsCache(Snap, Cells);
                Findings = Rights.Findings(Snap, Cfg.MaxLevel, Cells);
                var used = Used();
                Extra = new OrderedDictionary<string, Principal>(Extra.Where(kv => !used.Contains(kv.Key)));
                BaselineError = "";
                Desired desired;
                try
                {
                    desired = LoadDesired();
                }
                catch (Exception e) when (e is Owlseye.BaselineError or IOException or UnauthorizedAccessException)
                {
                    // also a locked or unreachable baseline folder: the scan stays valid (a share switch must not be left
                    // half done), the desired-state page shows why the comparison is off
                    BaselineError = e is Owlseye.BaselineError ? e.Message : $"{Baseline.PathOf(Snap.Share)}: {e.Message}";
                    desired = Owlseye.Drift.DesiredOf(Snap); // no comparison possible, show nothing
                }
                Desired = desired;
                Drift = Owlseye.Drift.Diff(Snap, desired);
            }
        }
        NotifyChanged();
    }

    /// <summary>Change matrix depth and remember it for this admin. Findings depend on it, the scan does not.</summary>
    public void SetDepth(int depth)
    {
        lock (Lock)
        {
            Cfg = Cfg with { MaxLevel = depth };
            Settings.Save(new JsonObject { ["depth"] = depth });
            DropNewFolders(PendingNew.Keys.Where(p => M.LevelOf(p) > depth).ToList());
            Findings = Rights.Findings(Snap, depth, Cells);
        }
        NotifyChanged();
    }

    Desired LoadDesired()
    {
        var desired = Baseline.Load(Snap.Share);
        if (desired is not null) return desired;
        var created = false;
        desired = Baseline.Update(Snap.Share, d => // under lock: another window may have been faster
        {
            if (d.Cells.Count > 0 || d.Protected.Count > 0) return d;
            created = true;
            return Owlseye.Drift.DesiredOf(Snap);
        }, Actor);
        if (created)
            Audit.Append(new JsonObject
            {
                ["kind"] = "baseline_init", ["actor"] = Actor, ["provider"] = Provider.Name, ["share"] = Snap.Share, ["ops"] = new JsonArray(),
            });
        return desired;
    }

    /// <summary>Per-scan results the planner can reuse (see RightsCache).</summary>
    public RightsCache? Cache { get; private set; }

    (Snapshot Snap, string Key, Plan? Plan, PlanError? Error)? lastPlan;

    /// <summary>The plan for the pending changes. Remembered until the snapshot or the pending changes change: the matrix,
    /// the panels and the check after each click all ask for it.</summary>
    public Plan Plan()
    {
        lock (Lock)
        {
            var key = PendingKey();
            if (lastPlan is { } lp && ReferenceEquals(lp.Snap, Snap) && lp.Key == key)
                return lp.Plan ?? throw new PlanError(lp.Error!.Message);
            try
            {
                var plan = Planner.Build(Snap, Pending, PendingFolders, Cfg.MaxLevel, PendingNew, Extra, PendingClear, Cache, PendingReinherit);
                lastPlan = (Snap, key, plan, null);
                return plan;
            }
            catch (PlanError e)
            {
                lastPlan = (Snap, key, null, e);
                throw;
            }
        }
    }

    /// <summary>Everything the plan depends on besides the snapshot, as one string (pending changes are few).</summary>
    string PendingKey()
    {
        var sb = new StringBuilder().Append(Cfg.MaxLevel).Append('\u0001');
        foreach (var ((sid, path), v) in Pending) sb.Append(sid).Append('\u0002').Append(path).Append('\u0002').Append(v ?? "-").Append('\u0001');
        sb.Append('\u0003');
        foreach (var (p, v) in PendingFolders) sb.Append(p).Append('\u0002').Append(v).Append('\u0001');
        sb.Append('\u0003');
        foreach (var (p, v) in PendingNew) sb.Append(p).Append('\u0002').Append(v).Append('\u0001');
        sb.Append('\u0003');
        foreach (var p in PendingClear.Order(StringComparer.Ordinal)) sb.Append(p).Append('\u0001');
        sb.Append('\u0002');
        foreach (var p in PendingReinherit.Order(StringComparer.Ordinal)) sb.Append(p).Append('\u0001');
        sb.Append('\u0003');
        foreach (var (sid, p) in Extra) sb.Append(sid).Append('\u0002').Append(p.Name).Append('\u0002').Append(p.Kind).Append('\u0001');
        return sb.ToString();
    }

    /// <summary>Forget planned new folders together with everything pending on them (cells, inheritance), or the plan
    /// would refer to folders that no longer exist.</summary>
    public void DropNewFolders(ICollection<string> gone)
    {
        lock (Lock)
        {
            foreach (var p in gone) PendingNew.Remove(p);
            foreach (var k in Pending.Keys.Where(k => gone.Contains(k.Path)).ToList()) Pending.Remove(k);
            foreach (var p in PendingFolders.Keys.Where(gone.Contains).ToList()) PendingFolders.Remove(p);
            PendingClear.ExceptWith(gone);
        }
    }

    public int PendingCount => Pending.Count + PendingFolders.Count + PendingNew.Count + PendingClear.Count + PendingReinherit.Count;

    public void Clear()
    {
        Pending.Clear();
        PendingFolders.Clear();
        PendingNew.Clear();
        PendingClear.Clear();
        PendingReinherit.Clear();
        Extra.Clear();
    }

    public Dictionary<string, Principal> Principals()
    {
        var o = new Dictionary<string, Principal>(Snap.Principals);
        foreach (var (k, v) in Extra) o[k] = v;
        return o;
    }

    /// <summary>Accounts with a right anywhere in the share (deeper than the matrix only as deviation, but visible).</summary>
    HashSet<string> Used() => Cells.Keys.Select(k => k.Sid).ToHashSet();

    /// <summary>Accounts with entries, added ones and ones with pending changes, alphabetical.</summary>
    public List<Principal> Columns(string q = "")
    {
        var show = Used();
        show.UnionWith(Extra.Keys);
        show.UnionWith(Pending.Keys.Select(k => k.Sid));
        return Principals().Where(kv => show.Contains(kv.Key)).Select(kv => kv.Value)
            .Where(p => p.Name.ToLowerInvariant().Contains(q.ToLowerInvariant(), StringComparison.Ordinal))
            .OrderBy(p => M.Lower(p.Short), M.Ci).ToList();
    }

    public void AddColumn(Principal p)
    {
        if (!Used().Contains(p.Sid)) Extra[p.Sid] = Snap.Principals.GetValueOrDefault(p.Sid, p);
    }

    /// <summary>Folders below the matrix depth: (with deviation, only as path to one).</summary>
    public (HashSet<string> Odd, HashSet<string> Way) Deep(Snapshot? snap = null)
    {
        snap ??= Snap;
        var depth = Cfg.MaxLevel;
        var odd = snap.Folders.Where(kv => kv.Value.Level > depth && Rights.Deviates(kv.Value)).Select(kv => kv.Key).ToHashSet();
        odd.UnionWith(PendingClear.Where(p => M.LevelOf(p) > depth)); // stay visible until applied
        odd.UnionWith(Rights.Stale(snap).Where(p => M.LevelOf(p) > depth)); // moved folders: wrong rights, so shown
        var way = odd.SelectMany(M.Ancestors).Where(a => M.LevelOf(a) > depth).ToHashSet();
        way.ExceptWith(odd);
        return (odd, way);
    }

    /// <summary>Root and folders down to the matrix depth in tree order, below that only deviations (and the path to them);
    /// with planned new folders, if snap has them.</summary>
    public List<Folder> Folders(Snapshot? snap = null)
    {
        snap ??= Snap;
        var (odd, way) = Deep(snap);
        var fs = snap.Folders.Values.Where(f => f.Level <= Cfg.MaxLevel || odd.Contains(f.Path) || way.Contains(f.Path)).ToList();
        fs.Sort((a, b) => M.TreeCompare(a.Path, b.Path));
        return fs;
    }

    public bool CanToggleInheritance(Folder f) => Snap.Folders.ContainsKey(f.Path) && Acl.CanToggle(f);

    /// <summary>Reset only makes sense where the folder has something of its own (entries or broken inheritance).</summary>
    public bool CanClear(string path) =>
        Snap.Folders.TryGetValue(path, out var f) && Acl.CanToggle(f) && !f.OtherAces && (f.Protected || f.Explicit.Count > 0);

    public bool CanAddFolder(string path) => M.LevelOf(path) < Cfg.MaxLevel;

    public static string PlanHash(Plan plan)
    {
        var body = new JsonArray([.. plan.AclOps.Select(o => Json.ToNode(o)), .. plan.CreateOps.Select(p => (JsonNode)p!)]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Line(body))))[..16];
    }
}
