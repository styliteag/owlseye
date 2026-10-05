using System.Collections;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Owlseye.Windows;

/// <summary>What an elevated owlseye does against code running unelevated in the same session (same user, medium
/// integrity): no WebView2 overrides from the environment, a program folder only administrators can change. The
/// browser process of WebView2 itself runs de-elevated (WebView2 does that on purpose), so it is not protected.</summary>
public static class Hardening
{
    /// <summary>Removes WEBVIEW2_* variables from this process (the WebView2 loader honours e.g.
    /// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port and WEBVIEW2_BROWSER_EXECUTABLE_FOLDER, which any
    /// process of the user can set in HKCU\Environment). Returns the names removed.</summary>
    public static List<string> ClearWebView2Overrides()
    {
        var removed = new List<string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(k, null);
                removed.Add(k);
            }
        return removed;
    }

    /// <summary>Accounts allowed to change the program folder and still be trusted: SYSTEM, Administrators, TrustedInstaller,
    /// CREATOR OWNER (only for what an administrator created there).</summary>
    static readonly HashSet<string> Trusted =
    [
        "S-1-5-18", "S-1-5-32-544", "S-1-3-0", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    ];

    /// <summary>Rights that let someone replace or add a library: write or delete, change the ACL or take ownership
    /// (plus GENERIC_ALL and GENERIC_WRITE, as inheritable entries often carry them).</summary>
    const FileSystemRights Changing = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;

    /// <summary>The first account other than administrators that may change `dir` or the exe and libraries in it, or null.
    /// An elevated owlseye loads its native libraries from its own folder: that folder should be like Program Files.</summary>
    public static string? WritableByOthers(string dir)
    {
        var d = new DirectoryInfo(dir);
        if (UntrustedWriter(d.GetAccessControl()) is { } who) return who;
        foreach (var f in d.EnumerateFiles().Where(f => f.Extension is ".exe" or ".dll"))
            if (UntrustedWriter(f.GetAccessControl()) is { } w) return $"{w} ({f.Name})";
        return null;
    }

    /// <summary>The owner (who may always change the ACL) if not trusted, else the first other account allowed to change
    /// the object; null if only administrators can.</summary>
    public static string? UntrustedWriter(FileSystemSecurity sec)
    {
        if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !Trusted.Contains(owner.Value))
            return $"the owner {NameOf(owner)}";
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & Changing) != 0
                && !Trusted.Contains(r.IdentityReference.Value))
                return NameOf((SecurityIdentifier)r.IdentityReference);
        return null;
    }

    static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    /// <summary>Deletes `dir` (whatever was left or put there) and creates it anew. No integrity label: WebView2
    /// de-elevates its browser process when the host runs elevated, so a folder only high integrity may write would
    /// lock it out ("cannot read and write to its data directory").</summary>
    public static void FreshFolder(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }
}
