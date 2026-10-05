using System.Runtime.InteropServices;
using System.Security.Principal;
using Owlseye.Providers;

namespace Owlseye.Windows;

/// <summary>Windows: AD + file share, in the context of the logged-in admin.</summary>
public sealed class WindowsProvider(string share, int scanDepth = 0, int parallelism = 8)
    : AdProvider(new Win32Fs(), new AdsiDirectory(), share, scanDepth, parallelism)
{
    public override string Name => "windows";
}

/// <summary>Local path (E:\Share) stays local; UNC as with the Windows provider.</summary>
public sealed class LocalFs : Win32Fs
{
    public override string ToUnc(string path)
    {
        if (path.StartsWith(@"\\")) return base.ToUnc(path);
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length == 2 && full[1] == ':') full += "\\"; // a drive root keeps its backslash
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Share root does not exist: {full}");
        Share = full;
        return Share;
    }
}

/// <summary>A single Windows machine without a domain (development, tests). Same logic as in AD; the Directory port
/// reads the local user database. ACLs are real, the share root is a local path.</summary>
public sealed class LocalProvider(string share, int scanDepth = 0, int parallelism = 4)
    : AdProvider(new LocalFs(), new LocalDirectory(), share, scanDepth, parallelism)
{
    public override string Name => "local";
}

public static class Elevation
{
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public static partial class Machine
{
    /// <summary>The AD domain this machine is a member of, or null (workgroup, or joined to Entra ID only). Asks the
    /// local machine only, no network.</summary>
    public static string? Domain()
    {
        if (NetGetJoinInformation(null, out var name, out var status) != 0) return null;
        try
        {
            return status == NetSetupDomainName ? Marshal.PtrToStringUni(name) : null;
        }
        finally
        {
            NetApiBufferFree(name);
        }
    }

    const int NetSetupDomainName = 3;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out nint name, out int status);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buf);
}
