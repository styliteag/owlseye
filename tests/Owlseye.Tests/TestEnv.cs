// Shared test helpers.

[assembly: CollectionBehavior(DisableTestParallelization = true)] // Paths.DataDirOverride is process-wide

namespace Owlseye.Tests;

/// <summary>Every test gets its own temp folder; the data dir (desired state, settings, sim, audit) points into it,
/// never at the real AppData.</summary>
public abstract class TestBase : IDisposable
{
    protected TestBase()
    {
        Tmp = Path.Combine(Path.GetTempPath(), "owlseye-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Tmp);
        AppData = Path.Combine(Tmp, "owlseye");
        Paths.DataDirOverride = AppData;
    }

    /// <summary>This test's own temp folder.</summary>
    protected string Tmp { get; }

    /// <summary>The data dir: Tmp\owlseye.</summary>
    protected string AppData { get; }

    public void Dispose()
    {
        Paths.DataDirOverride = null;
        try
        {
            ForceDelete(Tmp);
        }
        catch (Exception)
        {
            // a locked file in a temp folder is not a test failure
        }
        GC.SuppressFinalize(this);
    }

    static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(@"\\?\" + Path.GetFullPath(dir), true);
    }

    /// <summary>Create a directory symlink; false if the OS does not allow it (Windows without developer mode/admin):
    /// the test then returns early (xunit 2 has no dynamic skip).</summary>
    protected static bool Symlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Create a folder with exactly this name. On Windows, Win32 strips trailing dots/spaces unless the
    /// \\?\ prefix is used (which is how users create such folders, too).</summary>
    protected static void MkdirRaw(string path)
    {
        var parent = Path.GetFullPath(Path.GetDirectoryName(path)!); // the name itself must not go through GetFullPath
        Directory.CreateDirectory(@"\\?\" + Path.Combine(parent, Path.GetFileName(path)));
    }
}
