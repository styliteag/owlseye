// Start: owlseye [--demo | --sim [DIR] | --local [PATH]] [--config FILE]
//        owlseye seed-local PATH      (admin: demo share as real local folders, groups, users, ACLs)
//
// Without a mode: "provider" from config.json (--config, else config.json next to owlseye.exe), else the Windows
// provider on a domain member and the local one elsewhere. The demo only with --demo.
//
// --local without PATH (and the windows provider without a share) opens the share opened last; the UI
// (click on the share in the header) switches shares at runtime and remembers the choice per admin.

using Owlseye.Providers;
using Owlseye.Windows;

namespace Owlseye.App;

public sealed record Options(string? Mode, string? Path, string? Config, bool SeedLocal, string? Error, bool Force = false)
{
    public static Options Parse(string[] args)
    {
        string? sim = null, local = null, seedPath = null, config = null; // null = flag not given, "" = flag without value
        bool demo = false, seed = false, force = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            switch (a)
            {
                case "--demo":
                    demo = true;
                    break;
                case "--sim":
                    sim = Value() ?? "";
                    break;
                case "--local":
                    local = Value() ?? "";
                    break;
                case "--config":
                    config = Value() ?? throw new ArgumentException("--config needs a file");
                    break;
                case "--force":
                    force = true;
                    break;
                case "seed-local":
                    seed = true;
                    seedPath = Value();
                    break;
                case "--help" or "-h" or "/?":
                    return new Options(null, null, null, false, Usage);
                default:
                    return new Options(null, null, null, false, $"Unknown argument: {a}\n\n{Usage}");
            }
        }
        if (seed) return string.IsNullOrEmpty(seedPath)
            ? new Options(null, null, null, false, "usage: owlseye seed-local E:\\Share")
            : new Options(null, seedPath, config, true, null, force);
        // demo > sim > local, whatever the order on the command line
        var mode = demo ? "demo" : sim is not null ? "sim" : local is not null ? "local" : null;
        var path = mode switch { "sim" => sim, "local" => local, _ => null };
        return new Options(mode, path, config, false, null);
    }

    public const string Usage =
        "owlseye [--demo | --sim [DIR] | --local [PATH]] [--config FILE]\n"
        + "owlseye seed-local PATH [--force]\n\n"
        + "  --demo          demo data instead of AD/file server\n"
        + "  --sim [DIR]     Windows logic against emulated AD/file system (default: data dir\\sim)\n"
        + "  --local [PATH]  this machine's local users/groups and a local folder (no domain);\n"
        + "                  without PATH the folder opened last\n"
        + "  --config FILE   config.json (e.g. on the admin share)\n"
        + "  seed-local PATH create the demo share as real local folders, groups, users and ACLs (admin);\n"
        + "                  only into an empty folder or an earlier demo share, unless --force\n\n"
        + "Without a mode: provider from config.json (--config, or config.json next to owlseye.exe),\n"
        + "else AD and file server on a domain member, this machine's users and folders elsewhere.";
}

public static class Boot
{
    /// <summary>Config + provider + state for the options (rules in Launch); the first scan runs in the background.</summary>
    public static State Build(Options o)
    {
        var st = Launch.Build(o.Mode, o.Path, Launch.ConfigFile(o.Config, AppContext.BaseDirectory),
            (cfg, share) => cfg.Provider == "local"
                ? new LocalProvider(share, cfg.ScanDepth)
                : new WindowsProvider(share, cfg.ScanDepth),
            defaultProvider: Machine.Domain() is null ? "local" : "windows");
        st.ConfigFromCommandLine = o.Config is not null;
        return st;
    }
}
