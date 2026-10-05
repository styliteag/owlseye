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
    public string Audit { get; init; } = ""; // path to audit.jsonl; empty = local
    public string SimDir { get; init; } = ""; // provider=sim only; empty = local
    public string Baseline { get; init; } = ""; // folder for desired-<share>.json; empty = AppData (demo: memory only)

    public string? BaselineDir => Baseline != "" ? Baseline : Provider == "demo" ? null : Paths.DataDir();

    public string SimPath => SimDir != "" ? SimDir : Path.Combine(Paths.DataDir(), "sim");

    public string AuditPath => Audit != "" ? Audit : Path.Combine(Paths.DataDir(), $"audit-{Provider}.jsonl");

    /// <summary>provider: from the command line, wins over the file. defaultProvider: if neither names one.</summary>
    public static Config Load(string? path, string? provider = null, string? defaultProvider = null)
    {
        var raw = path is not null ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        var c = new Config(); // unknown keys (dl_ou, gg_ous) are ignored
        c = c with
        {
            Provider = raw.Str("provider") ?? defaultProvider ?? c.Provider,
            Share = raw.Str("share") ?? c.Share,
            MaxLevel = (int?)raw.Long("max_level") ?? c.MaxLevel,
            ScanDepth = (int?)raw.Long("scan_depth") ?? c.ScanDepth,
            Audit = raw.Str("audit") ?? c.Audit,
            SimDir = raw.Str("sim_dir") ?? c.SimDir,
            Baseline = raw.Str("baseline") ?? c.Baseline,
        };
        return provider is not null ? c with { Provider = provider } : c;
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
