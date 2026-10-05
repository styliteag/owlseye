// The Razor pages of the app are English (test_ui_is_english rendered the Jinja templates; here the .razor sources
// are checked, the texts built in Session/Labels are covered by AclAppTests.UiIsEnglish).

using System.Text.RegularExpressions;

namespace Owlseye.Tests;

public sealed class RazorTextsTests
{
    static readonly Regex German = new(@"\b(Ändern|Lesen|Vorschau|Verwerfen|Benutzer|Ordner|Abweichung|Protokoll|Gruppe|Rechte)\b");

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Owlseye.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Owlseye.slnx not found above the test output");
    }

    [Fact]
    public void RazorPagesAreEnglish()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Owlseye.App"), "*.razor", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = Regex.Replace(File.ReadAllText(f), "<[^>]+>", " ");
            var m = German.Match(text);
            Assert.False(m.Success, $"{Path.GetFileName(f)}: {m.Value}");
        }
    }

    [Fact]
    public void EveryPageOfTheWebVersionHasARazorPage()
    {
        var pages = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Owlseye.App", "Components", "Pages"), "*.razor")
            .Select(File.ReadAllText).ToList();
        foreach (var route in new[] { "/matrix", "/preview", "/drift", "/findings", "/users", "/folder", "/audit", "/share" })
            Assert.Contains(pages, p => p.Contains($"@page \"{route}\""));
    }
}
