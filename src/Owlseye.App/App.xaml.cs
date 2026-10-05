using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Owlseye.App.Services;
using Owlseye.Ui;
using Owlseye.Windows;

namespace Owlseye.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            ErrorLog.Write("UI thread", ev.Exception);
            MessageBox.Show($"Unexpected error: {ev.Exception.Message}\n\nDetails: {ErrorLog.PathOf}", "owlseye",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) ErrorLog.Write("background thread", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            ErrorLog.Write("task", ev.Exception);
            ev.SetObserved();
        };
        Options o;
        try
        {
            o = Options.Parse(e.Args);
        }
        catch (ArgumentException ex)
        {
            o = new Options(null, null, null, false, ex.Message);
        }
        if (o.Error is not null)
        {
            Console(o.Error);
            MessageBox.Show(o.Error, "owlseye", MessageBoxButton.OK, o.Error == Options.Usage ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Shutdown(o.Error == Options.Usage ? 0 : 2);
            return;
        }
        if (o.SeedLocal)
        {
            Shutdown(SeedLocal(o.Path!, o.Force));
            return;
        }

        // Elevated (security review, finding 1): no WebView2 overrides from the environment, and a browser profile of its
        // own, created fresh at each start (an elevated and an unelevated instance cannot share one). WebView2 runs its
        // browser process de-elevated, so the profile must stay writable at medium integrity (no integrity label).
        var admin = Elevation.IsAdmin();
        var profile = Path.Combine(Paths.DataDir(), "WebView2");
        if (admin)
        {
            var removed = Hardening.ClearWebView2Overrides();
            if (removed.Count > 0) ErrorLog.Write("startup", new InvalidOperationException($"ignored while elevated: {string.Join(", ", removed)}"));
            profile = Path.Combine(Paths.DataDir(), "WebView2-elevated");
            try
            {
                Hardening.FreshFolder(profile);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("elevated WebView2 profile", ex);
            }
        }

        if (!HasWebView2())
        {
            ErrorLog.Write("startup", new InvalidOperationException("WebView2 runtime not found"));
            MessageBox.Show(
                "owlseye needs the Microsoft Edge WebView2 Runtime, which is missing on this machine (often on Windows Server "
                + "2019/2022).\n\nInstall the Evergreen runtime from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start "
                + "owlseye again.", "owlseye", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(3);
            return;
        }

        State st;
        try
        {
            st = Boot.Build(o);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("startup", ex);
            MessageBox.Show(ex.Message, "owlseye", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton(st);
        services.AddSingleton(new Session(st));
        string? unsafeFolder = null;
        if (admin)
        {
            try
            {
                if (Hardening.WritableByOthers(AppContext.BaseDirectory) is { } who)
                    unsafeFolder = $"owlseye runs elevated from {AppContext.BaseDirectory}, which {who} can change; the libraries next to "
                        + "owlseye.exe are loaded from there. Install the folder where only administrators can write (e.g. C:\\Program Files\\owlseye).";
            }
            catch (Exception ex)
            {
                ErrorLog.Write("program folder check", ex);
            }
        }
        services.AddSingleton(new HostInfo(e.Args, admin) { WebViewProfile = profile, UnsafeFolder = unsafeFolder });
        services.AddSingleton<Dialogs>();
        services.AddSingleton<WebViewHost>();
        services.AddSingleton<UiState>();
        var sp = services.BuildServiceProvider();

        var w = new MainWindow(sp, st);
        MainWindow = w;
        w.Show();
    }

    static bool HasWebView2()
    {
        try
        {
            return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    static void Console(string text)
    {
        if (AttachConsole(-1)) System.Console.WriteLine("\n" + text);
    }

    static int SeedLocal(string share, bool force)
    {
        AttachConsole(-1);
        try
        {
            if (!Elevation.IsAdmin()) throw new UnauthorizedAccessException("seed-local needs an elevated (administrator) prompt.");
            LocalSeed.Seed(share, line => System.Console.WriteLine(line), force);
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"seed-local failed: {ex.Message}");
            return 1;
        }
    }
}
