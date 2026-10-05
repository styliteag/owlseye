using System.ComponentModel;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.FileProviders;
using Owlseye.App.Components;

namespace Owlseye.App;

/// <summary>Serves wwwroot from the resources embedded in the exe (single-file publish needs no folder next to it).</summary>
public sealed class OwlseyeWebView : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir) =>
        new ManifestEmbeddedFileProvider(typeof(OwlseyeWebView).Assembly, "wwwroot");
}

public partial class MainWindow : Window
{
    readonly State st;
    readonly Services.UiState ui;
    bool mayClose;

    public MainWindow(IServiceProvider services, State st)
    {
        this.st = st;
        ui = (Services.UiState)services.GetService(typeof(Services.UiState))!;
        InitializeComponent();
        var view = new OwlseyeWebView
        {
            HostPage = "wwwroot\\index.html",
            Services = services,
            StartPath = "/matrix",
        };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        view.BlazorWebViewInitializing += (_, e) =>
        {
            // next to the exe is often not writable (Program Files, a share): keep the browser profile per admin
            e.UserDataFolder = ((Services.HostInfo)services.GetService(typeof(Services.HostInfo))!).WebViewProfile;
        };
        view.BlazorWebViewInitialized += (_, e) =>
        {
            ((Services.WebViewHost)services.GetService(typeof(Services.WebViewHost))!).View = e.WebView;
            e.WebView.AllowExternalDrop = false; // a file dropped onto the window must not be opened
            var s = e.WebView.CoreWebView2.Settings;
            s.IsStatusBarEnabled = false;
#if !DEBUG
            s.AreDevToolsEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
#endif
        };
        view.UrlLoading += (_, e) =>
        {
            // The app itself stays in the window (BlazorWebView decides that). Anything else would be handed to the
            // shell; allow only web links there, never file: or other schemes (a dropped .lnk, a crafted URL).
            if (e.UrlLoadingStrategy == UrlLoadingStrategy.OpenExternally && e.Url.Scheme is not ("http" or "https"))
                e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
        };
        Root.Children.Add(view);
        UpdateTitle();
        st.Changed += () => Dispatcher.BeginInvoke(UpdateTitle);
        ui.PageTitleChanged += () => Dispatcher.BeginInvoke(UpdateTitle);
    }

    void UpdateTitle()
    {
        var share = st.Snap.Share != "" ? st.Snap.Share : st.Provider.Share;
        var page = st.Ready ? ui.PageTitle + " · " : "";
        Title = share != "" ? $"{page}owlseye · {share}" : $"{page}owlseye";
    }

    /// <summary>Closing stops the process. While it writes ACLs, ask first: the folder being written would stay half done.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!mayClose && st.Progress.Now.Task == "apply")
        {
            var r = MessageBox.Show(this,
                "owlseye is writing ACLs right now.\n\nClosing stops the change halfway: the folder being written may be left "
                + "with its subfolders only partly updated. Wait until the progress window is gone.\n\nClose anyway?",
                "owlseye", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        mayClose = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Environment.Exit(0); // background scan/apply threads must not keep the process alive
    }
}
