using System.Text.Json.Nodes;

namespace Owlseye;

public static class Paths
{
    /// <summary>For tests: replaces %LOCALAPPDATA%\owlseye.</summary>
    public static string? DataDirOverride { get; set; }

    public static string DataDir()
    {
        if (DataDirOverride is not null) return DataDirOverride;
        var b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b))
            b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(b, "owlseye");
    }
}

/// <summary>Configuration from config.json (lives on the admin share, path via --config).</summary>
public sealed record Config
{
    public string Provider { get; init; } = "demo"; // demo | sim | windows | local
    public string Share { get; init; } = ""; // UNC or drive letter; for local a local folder
    public int MaxLevel { get; init; } = 3; // default matrix depth (changeable in the UI, remembered per admin)
    public int ScanDepth { get; init; } = 20; // deepest level read; 0 = whole tree
    public string State { get; init; } = ""; // folder for the desired state and the log; empty = the data folder
    public string Audit { get; init; } = ""; // path to audit.jsonl; empty = in the state folder
    public string SimDir { get; init; } = ""; // provider=sim only; empty = local
    public string Baseline { get; init; } = ""; // folder for desired-<share>.json; empty = AppData (demo: memory only)
    public IReadOnlyList<string> Hidden { get; init; } = []; // further accounts to hide like the administrators
    public IReadOnlyList<string> FullControl { get; init; } = M.DefaultFullControl; // must have full control (root, broken inheritance)

    /// <summary>The config.json this configuration was read from; null if none.</summary>
    public string? File { get; init; }

    /// <summary>Where the desired state and the log go unless audit/baseline name other places.</summary>
    public string StateDir => State != "" ? State : Paths.DataDir();

    /// <summary>The settings shared by everyone who uses a state folder: scan depth, what W means, full control, hidden
    /// accounts. Not "settings.json", which holds each admin's own UI settings in the data folder.</summary>
    public const string SharedFileName = "owlseye-settings.json";

    public string SharedFile => Path.Combine(StateDir, SharedFileName);

    /// <summary>With the shared settings of the state folder, if it has them: they win over the same keys in the local
    /// config.json, so all admins of one state folder work by the same rules.</summary>
    public Config WithShared()
    {
        var path = SharedFile;
        if (!System.IO.File.Exists(path)) return this;
        var raw = JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? [];
        return this with
        {
            ScanDepth = (int?)raw.Long("scan_depth") ?? ScanDepth,
            Hidden = Strings(raw["hidden"]) ?? Hidden,
            FullControl = Strings(raw["full_control"]) is { Count: > 0 } fc ? fc : FullControl,
        };
    }

    /// <summary>The shared settings into the state folder (other keys of the file stay).</summary>
    public static void SaveShared(string path, Config c)
    {
        var raw = System.IO.File.Exists(path) ? JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? [] : [];
        raw["scan_depth"] = c.ScanDepth;
        raw.Remove("write"); // W- and folders users cannot move are set in the matrix now
        raw["hidden"] = new JsonArray(c.Hidden.Select(h => (JsonNode)h!).ToArray());
        raw["full_control"] = new JsonArray(c.FullControl.Select(h => (JsonNode)h!).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Json.WriteAtomic(path, Json.Pretty(raw), ".shared-");
    }

    public string? BaselineDir => Baseline != "" ? Baseline : Provider == "demo" ? null : StateDir;

    public string SimPath => SimDir != "" ? SimDir : Path.Combine(Paths.DataDir(), "sim");

    public string AuditPath => Audit != "" ? Audit : Path.Combine(StateDir, $"audit-{Provider}.jsonl");

    /// <summary>provider: from the command line, wins over the file. defaultProvider: if neither names one.</summary>
    public static Config Load(string? path, string? provider = null, string? defaultProvider = null)
    {
        var raw = path is not null ? JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? [] : [];
        var c = new Config(); // unknown keys (dl_ou, gg_ous) are ignored
        c = c with
        {
            Provider = raw.Str("provider") ?? defaultProvider ?? c.Provider,
            Share = raw.Str("share") ?? c.Share,
            MaxLevel = (int?)raw.Long("max_level") ?? c.MaxLevel,
            ScanDepth = (int?)raw.Long("scan_depth") ?? c.ScanDepth,
            State = raw.Str("state") ?? c.State,
            Audit = raw.Str("audit") ?? c.Audit,
            SimDir = raw.Str("sim_dir") ?? c.SimDir,
            Baseline = raw.Str("baseline") ?? c.Baseline,
            Hidden = Strings(raw["hidden"]) ?? c.Hidden,
            FullControl = Strings(raw["full_control"]) is { Count: > 0 } fc ? fc : c.FullControl,
            File = path,
        };
        return provider is not null ? c with { Provider = provider } : c;
    }

    static List<string>? Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Select(s => s.Trim()).Where(s => s != "").ToList() : null;

    /// <summary>Where the state folder is into the local config.json (the shared settings go into the state folder, see
    /// SaveShared, and leave this file); its other keys stay as they are.</summary>
    public static void Save(string path, Config c)
    {
        var raw = System.IO.File.Exists(path) ? JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? [] : [];
        foreach (var key in new[] { "scan_depth", "write", "hidden", "full_control" }) raw.Remove(key);
        raw["state"] = c.State;
        raw["audit"] = c.Audit;
        raw["baseline"] = c.Baseline;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Json.WriteAtomic(path, Json.Pretty(raw), ".config-");
    }
}

/// <summary>Settings from the UI, per admin in %LOCALAPPDATA%\owlseye\settings.json (e.g. the matrix depth).
/// Missing or broken: defaults from config.json.</summary>
public static class Settings
{
    public const int Recent = 8;

    static string PathOf() => Path.Combine(Paths.DataDir(), "settings.json");

    public static JsonObject Load()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(PathOf())) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static int? Depth() => (int?)Load().Long("depth");

    /// <summary>The matrix showed the hidden accounts when this admin last used it (M.ShowHidden).</summary>
    public static bool ShowHidden() => Load().Bool("show_hidden") == true;

    /// <summary>Last share opened with this provider (local/windows); "" if none.</summary>
    public static string LastShare(string kind) => Load().Str($"share_{kind}") ?? "";

    public static List<string> RecentShares(string kind) =>
        Load().Arr($"recent_{kind}") is { } a ? a.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => (string)v!).ToList() : [];

    /// <summary>Opened share becomes the default for the next start and moves to the top of the recent list.</summary>
    public static void RememberShare(string kind, string path)
    {
        var rest = RecentShares(kind).Where(p => M.Lower(p) != M.Lower(path));
        var list = new[] { path }.Concat(rest).Take(Recent).Select(p => (JsonNode)p!).ToArray();
        Save(new() { [$"share_{kind}"] = path, [$"recent_{kind}"] = new JsonArray(list) });
    }

    public static void Save(JsonObject values)
    {
        var data = Load();
        foreach (var (k, v) in values) data[k] = v?.DeepClone();
        Json.WriteAtomic(PathOf(), Json.Pretty(Json.Sorted(data)!), ".settings-");
    }
}
