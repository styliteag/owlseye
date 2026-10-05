using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Owlseye.App;

/// <summary>Shown at start when the WebView2 Runtime is missing (Windows Server 2019/2022): what to install, with links
/// that open in the browser and stay readable for typing them on another machine.</summary>
static class WebView2Missing
{
    // Microsoft's links for the Evergreen WebView2 Runtime
    const string Online = "https://go.microsoft.com/fwlink/?linkid=2124703";
    const string Offline = "https://go.microsoft.com/fwlink/?linkid=2124701";
    const string Page = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static void Show()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text(
            "owlseye needs the Microsoft Edge WebView2 Runtime, which is missing on this machine. Windows Server 2019 and "
            + "2022 do not include it; Windows 10/11 and Windows Server 2025 do.", bold: true));
        panel.Children.Add(Text("Install it once as administrator, then start owlseye again."));
        panel.Children.Add(Link("With internet access, the small online installer:", Online));
        panel.Children.Add(Link("Without internet access, the offline installer (x64); download it on another machine:", Offline));
        panel.Children.Add(Link("All options on Microsoft's download page:", Page));

        var copy = new Button { Content = "Copy links", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText($"WebView2 online installer: {Online}\r\nWebView2 offline installer (x64): {Offline}\r\nDownload page: {Page}\r\n");
                copy.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException) // clipboard held by another program
            {
                copy.Content = "Clipboard busy, try again";
            }
        };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsDefault = true, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "owlseye: WebView2 Runtime missing",
            Content = panel,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    static TextBlock Text(string text, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, 0, 0, 12),
    };

    /// <summary>A caption and the address itself as a link (the visible text is the full URL).</summary>
    static TextBlock Link(string caption, string url)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        block.Inlines.Add(new Run(caption));
        block.Inlines.Add(new LineBreak());
        var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url), ToolTip = "Open in the browser" };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) // no browser registered, e.g. a minimal server installation
            {
                Services.ErrorLog.Write("open WebView2 link", ex);
                MessageBox.Show($"No browser could be started. Use \"Copy links\" and open the address elsewhere:\n\n{e.Uri.AbsoluteUri}",
                    "owlseye", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            e.Handled = true;
        };
        block.Inlines.Add(link);
        return block;
    }
}
