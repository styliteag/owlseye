// Start rules, platform-neutral so tests can check them:
// config + mode -> provider -> state, first scan in the background.
//
// Provider: --demo/--sim/--local > "provider" in config.json (from --config, else next to the exe) > the default the
// app passes (windows on a domain member, local elsewhere). Nothing about the mode is remembered between starts.
// local/windows: explicit path > share opened last (UI) > share from config.json. Without any share the window opens
// anyway: the first scan fails with what to do, and the loading page offers to open a folder.

using Owlseye.Providers;

namespace Owlseye;

public static class Launch
{
    /// <summary>mode: demo | sim | local | windows | null (from config). path: --sim DIR or --local PATH.
    /// shareProvider builds the provider for a share path (local/windows; Win32 lives in another assembly).
    /// defaultProvider: when neither the command line nor config.json names one (null: demo).</summary>
    public static State Build(string? mode, string? path, string? configFile, Func<Config, string, IProvider>? shareProvider,
        bool scanInBackground = true, string? defaultProvider = null)
    {
        var cfg = Config.Load(configFile, mode, defaultProvider);
        try
        {
            cfg = cfg.WithShared(); // the state folder's shared settings win
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // state folder not reachable or file broken: the local settings apply, the settings page shows the folder
        }
        M.Configure(cfg.Write, cfg.Hidden, cfg.FullControl); // before any provider scans or builds ACEs
        M.ShowHidden = Settings.ShowHidden();
        if (cfg.Provider == "sim" && !string.IsNullOrEmpty(path)) cfg = cfg with { SimDir = path };
        IProvider provider;
        Func<string, IProvider>? openShare = null;
        string? notice = null;
        if (cfg.Provider is "local" or "windows")
        {
            if (shareProvider is null) throw new InvalidOperationException($"The {cfg.Provider} provider needs Windows");
            openShare = p => shareProvider(cfg, p);
            var share = ShareToOpen(cfg, path);
            if (share == "") provider = new NoShareProvider(cfg.Provider);
            else
            {
                try
                {
                    provider = openShare(share);
                    Settings.RememberShare(cfg.Provider, provider.Share);
                }
                catch (Exception e) // drive gone, not a folder: say why, offer another
                {
                    provider = new FailedShareProvider(cfg.Provider, share, e.Message);
                }
            }
        }
        else if (cfg.Provider == "sim")
        {
            if (!File.Exists(Path.Combine(cfg.SimPath, SimState.FileName)))
            {
                SimSeed.Seed(cfg.SimPath);
                notice = $"Sim created: {cfg.SimPath}";
            }
            provider = new SimProvider(cfg.SimPath, cfg.ScanDepth);
        }
        else provider = new DemoProvider();
        var st = new State(cfg, provider) { StartNotice = notice };
        if (openShare is not null) st.OpenShare = p => shareProvider!(st.Cfg, p); // scan depth as set now
        if (scanInBackground) st.LoadInBackground();
        else st.Load();
        return st;
    }

    /// <summary>The config.json to read: the one given with --config, else the admin's own one in the data folder
    /// (written by the settings page), else one next to the exe (put there for everyone, e.g. on the admin share), else
    /// none.</summary>
    public static string? ConfigFile(string? explicitFile, string exeDir)
    {
        if (explicitFile is not null) return explicitFile;
        var own = PersonalConfig;
        if (File.Exists(own)) return own;
        var next = Path.Combine(exeDir, "config.json");
        return File.Exists(next) ? next : null;
    }

    /// <summary>%LOCALAPPDATA%\owlseye\config.json: where the settings page saves when there is no writable config.json.</summary>
    public static string PersonalConfig => Path.Combine(Paths.DataDir(), "config.json");

    /// <summary>Explicit path > folder opened last (UI) > share from config.json; "" if none.</summary>
    public static string ShareToOpen(Config cfg, string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
        var last = Settings.LastShare(cfg.Provider);
        return last != "" ? last : cfg.Share;
    }
}

/// <summary>Stands in for the share while none is chosen yet: the first scan fails with a hint, and the loading
/// page offers to open a folder.</summary>
public sealed class NoShareProvider(string name) : IProvider
{
    public string Name => name;
    public string Share => "";
    public string WhoAmI() => Environment.UserDomainName + "\\" + Environment.UserName;

    public Snapshot Scan(Progress? progress = null) =>
        throw new InvalidOperationException(name == "local"
            ? "No share yet: enter a local folder (or a UNC path) below, or start once with --local PATH."
            : "No share yet: enter a share (UNC path or mapped drive) below, or set share in config.json.");

    public (bool Protected, List<Ace> Aces) FolderAcl(string path) => throw new InvalidOperationException("No share");
    public bool FolderExists(string path) => false;
    public void CreateFolder(string path) => throw new InvalidOperationException("No share");
    public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => throw new InvalidOperationException("No share");
    public List<Principal> FindGroups(string q) => [];
}

/// <summary>The share from the start could not even be opened (missing drive, not a folder): show why, offer another.</summary>
public sealed class FailedShareProvider(string name, string share, string error) : IProvider
{
    public string Name => name;
    public string Share => share;
    public string WhoAmI() => Environment.UserDomainName + "\\" + Environment.UserName;
    public Snapshot Scan(Progress? progress = null) => throw new InvalidOperationException(error);
    public (bool Protected, List<Ace> Aces) FolderAcl(string path) => throw new InvalidOperationException(error);
    public bool FolderExists(string path) => false;
    public void CreateFolder(string path) => throw new InvalidOperationException(error);
    public void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces) => throw new InvalidOperationException(error);
    public List<Principal> FindGroups(string q) => [];
}
