using System.Diagnostics;
using System.Windows;
using Microsoft.AspNetCore.Components;
using Microsoft.Win32;
using Owlseye.Ui;

namespace Owlseye.App.Services;

/// <summary>How the app was started, and restarting it elevated (replaces start-local.ps1).</summary>
public sealed class HostInfo(string[] args, bool isAdmin)
{
    public string[] Args { get; } = args;
    public bool IsAdmin { get; } = isAdmin;

    /// <summary>Set when owlseye runs elevated from a folder non-administrators can change (shown in the header).</summary>
    public string? UnsafeFolder { get; init; }

    /// <summary>WebView2 user data folder: per admin, a separate high-integrity one when elevated.</summary>
    public string WebViewProfile { get; init; } = Path.Combine(Paths.DataDir(), "WebView2");

    public bool RestartElevated() => Restart(Args, "runas");

    /// <summary>Starts owlseye anew with other arguments (e.g. --demo) and closes this window.</summary>
    public bool RestartWith(params string[] args) => Restart(args, "");

    static bool Restart(string[] args, string verb)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = verb };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) // UAC prompt cancelled
        {
            return false;
        }
        Application.Current.Dispatcher.Invoke(() => Application.Current.MainWindow?.Close());
        return true;
    }
}

/// <summary>Native dialogs, called from Blazor (any thread) and run on the WPF UI thread.</summary>
public sealed class Dialogs
{
    static T OnUi<T>(Func<T> f) => Application.Current.Dispatcher.Invoke(f);

    public string? PickFolder(string title = "Open share", string? start = null) => OnUi(() =>
    {
        var d = new OpenFolderDialog { Title = title };
        if (start is { Length: > 0 } && Directory.Exists(start)) d.InitialDirectory = start;
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null;
    });

    public bool Confirm(string text) => OnUi(() =>
        MessageBox.Show(Application.Current.MainWindow!, text, "owlseye", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK);

    /// <summary>Save dialog; null if cancelled. filter like "Excel workbook (*.xlsx)|*.xlsx".</summary>
    public string? SaveFile(string title, string filter, string fileName) => OnUi(() =>
    {
        var d = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    });

    /// <summary>Open a file with its program (Excel, the PDF viewer).</summary>
    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Explorer with the file selected.</summary>
    public void ShowInFolder(string path)
    {
        // full path: by bare name, CreateProcess would look in the current directory first
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path.Replace("\"", "")}\"") { UseShellExecute = false });
    }
}

/// <summary>The window's WebView2, for printing the page shown to PDF.</summary>
public sealed class WebViewHost
{
    public Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? View { get; set; }

    /// <summary>The current page as PDF (A4 landscape, backgrounds, title and page numbers in header/footer).
    /// Call on the UI thread (Blazor event handlers run there).</summary>
    public async Task<bool> PrintToPdf(string path, string title)
    {
        var core = View?.CoreWebView2 ?? throw new InvalidOperationException("The window is not ready");
        var s = core.Environment.CreatePrintSettings();
        s.Orientation = Microsoft.Web.WebView2.Core.CoreWebView2PrintOrientation.Landscape;
        s.PageWidth = 8.27; // A4 in inches
        s.PageHeight = 11.69;
        s.MarginTop = s.MarginBottom = 0.45;
        s.MarginLeft = s.MarginRight = 0.4;
        s.ShouldPrintBackgrounds = true;
        s.ShouldPrintHeaderAndFooter = true;
        s.HeaderTitle = title;
        s.FooterUri = ""; // no app address in the footer
        return await core.PrintToPdfAsync(path, s);
    }
}

/// <summary>Flash message, busy overlay and progress for the whole UI. Components subscribe to Changed.</summary>
public sealed class UiState : IDisposable
{
    readonly State st;
    readonly Timer timer;
    int dirty;

    public UiState(State st)
    {
        this.st = st;
        st.Progress.Changed += _ => Interlocked.Exchange(ref dirty, 1);
        // progress arrives per folder (thousands per second on a fast scan): repaint at most 5 times a second
        timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref dirty, 0) == 1) ProgressChanged?.Invoke();
        }, null, 200, 200);
        StartNotice(st.StartNotice);
    }

    /// <summary>Message at the top of the page. Set by an action (Go) it stays for the page the action leads to; set on
    /// the page itself (SetFlash) it goes away with the next navigation, like ?msg= in the web version.</summary>
    public (string Text, bool Error)? Flash { get; private set; }
    bool keepFlash;

    /// <summary>A long action is running (scan, apply, share switch): the overlay shows the progress and the page
    /// behind it is inert, since everything there would wait for the state lock.</summary>
    public bool Busy { get; private set; }

    public event Action? Changed;
    public event Action? ProgressChanged;

    /// <summary>Title of the page shown ("Preview", a folder name); the window puts it in its title bar.</summary>
    public string PageTitle { get; private set; } = "Matrix";

    public event Action? PageTitleChanged;

    public void SetPageTitle(string title)
    {
        if (title == PageTitle) return;
        PageTitle = title;
        PageTitleChanged?.Invoke();
    }

    /// <summary>Raised after Rescan: the web version reloaded the matrix then (placeholder panel, no filter).</summary>
    public event Action? MatrixReset;

    public void ResetMatrix() => MatrixReset?.Invoke();

    /// <summary>Shows what the start did (e.g. "Sim created: …") until the first navigation (the start page itself is
    /// not a navigation, so nothing has to keep it).</summary>
    public void StartNotice(string? text)
    {
        if (text is null) return;
        Flash = (text, false);
        keepFlash = false;
    }

    public void SetFlash(string? text, bool error = false)
    {
        Flash = text is null ? null : (text, error);
        keepFlash = false;
        Changed?.Invoke();
    }

    /// <summary>Called by the layout on every navigation.</summary>
    public void Navigated()
    {
        if (keepFlash) keepFlash = false;
        else Flash = null;
    }

    public void Go(NavigationManager nav, Outcome o)
    {
        Flash = o.Message is null ? null : (o.Message, o.Error);
        var target = o.Url;
        var same = nav.ToBaseRelativePath(nav.Uri).Split('?')[0] == target.TrimStart('/');
        keepFlash = !same; // to the same page there is no page change that would have to keep it
        Changed?.Invoke();
        if (!same) nav.NavigateTo(target);
    }

    /// <summary>Run a long action off the UI thread with the busy overlay, then go where it says.</summary>
    public async Task RunBusy(NavigationManager nav, Func<Outcome> action)
    {
        if (Busy) return;
        Busy = true;
        Changed?.Invoke();
        Outcome o;
        try
        {
            o = await Task.Run(action);
        }
        catch (UserError e)
        {
            o = new Outcome("/" + nav.ToBaseRelativePath(nav.Uri).Split('?')[0], e.Message, true);
        }
        catch (Exception e)
        {
            ErrorLog.Write("action", e);
            o = new Outcome("/matrix", $"{e.GetType().Name}: {e.Message}", true);
        }
        finally
        {
            Busy = false;
        }
        // pages skip their refresh while busy (the state is locked): recompute them all now
        st.NotifyChanged();
        Go(nav, o);
    }

    public Status Progress => st.Progress.Now;

    public void Dispose() => timer.Dispose();
}
