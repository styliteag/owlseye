namespace Owlseye.Providers;

public interface IProvider
{
    string Name { get; }

    /// <summary>UNC (or local path) of the share root, as opened.</summary>
    string Share { get; }

    string WhoAmI();

    /// <summary>Reports what it reads to progress, for the progress display.</summary>
    Snapshot Scan(Progress? progress = null);

    /// <summary>(inheritance broken, all ACEs including inherited), read live.</summary>
    (bool Protected, List<Ace> Aces) FolderAcl(string path);

    bool FolderExists(string path);

    /// <summary>Create subfolder (parent exists; how deep is checked by the planner).</summary>
    void CreateFolder(string path);

    /// <summary>Writes the explicit DACL of a folder; the filesystem computes inherited ones.</summary>
    void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces);

    /// <summary>Groups in the directory and built-in groups whose name starts with q (for new columns).
    /// Without SYSTEM, Administrators and Creator Owner.</summary>
    List<Principal> FindGroups(string q);

    /// <summary>Write the explicit DACL only if the folder is still the one in the scan (`before`: same identity, same
    /// explicit entries, same protection); otherwise IOException "changed outside owlseye". null = new folder.</summary>
    void SetFolderAcl(string path, bool isProtected, IReadOnlyList<Ace> aces, Folder? before) => SetFolderAcl(path, isProtected, aces);

    /// <summary>The account behind a SID as the system resolves it now (kind "unknown" if it cannot), for SIDs that come
    /// from files (desired state, log) rather than from the scan; null if the provider cannot tell.</summary>
    Principal? ResolveAccount(string sid) => null;
}

/// <summary>Raw ACE as from the DACL: (AceType, AceFlags, AccessMask, SID as string).</summary>
public readonly record struct RawAce(int Type, int Flags, uint Mask, string Sid);

/// <summary>A directory row like ADODB returns it: multi-value attributes (member, objectClass) as string[],
/// empty ones as null, objectSid as byte[], numbers as long, everything else as string.</summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.OrdinalIgnoreCase) { }

    public string? S(string key) => TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public string[] Multi(string key) => TryGetValue(key, out var v) ? v switch
    {
        null => [],
        string s => [s],
        IEnumerable<string> l => l.ToArray(),
        System.Collections.IEnumerable e => e.Cast<object>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)!).ToArray(),
        _ => [Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!],
    } : [];

    public long L(string key) => TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : 0;

    public byte[]? Bytes(string key) => TryGetValue(key, out var v) ? v as byte[] : null;
}

/// <summary>Everything beneath the AD provider that needs directory APIs. On Windows ADSI, locally the SAM database,
/// in the sim an emulation from JSON.</summary>
public interface IDirectory
{
    string WhoAmI();

    string RootDn();

    /// <summary>LDAP search; scope is "subtree" or "base".</summary>
    List<Row> Query(string searchBase, string filter, IReadOnlyList<string> attrs, string scope = "subtree");
}

/// <summary>A directory that keeps what it read for a while; forgotten at the start of every scan.</summary>
public interface ICachingDirectory
{
    void Forget();
}

/// <summary>Everything beneath the AD provider that needs file system APIs.</summary>
public interface IFilesystem
{
    /// <summary>Junctions/symlinks seen by Subdirs() since the last scan (relative paths). Inheritance propagation walks
    /// the subtree by name, so owlseye does not write an ACL above a known link.</summary>
    HashSet<string> Links { get; }

    string ToUnc(string path);

    /// <summary>(inheritance protected, ACEs) of folder `rel` relative to the share root, separator '\'.</summary>
    (bool Protected, List<RawAce> Aces) ReadDacl(string rel);

    /// <summary>(name, domain, SID_NAME_USE). KeyNotFoundException if not resolvable.</summary>
    (string Name, string Domain, int Use) LookupSid(string sid);

    /// <summary>Set explicit DACL; the filesystem computes inherited ACEs itself.</summary>
    void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces);

    /// <summary>The DACL and an identity of the folder (volume and file id) where the file system has one, "" otherwise.</summary>
    (bool Protected, List<RawAce> Aces, string Id) ReadFolder(string rel)
    {
        var (p, a) = ReadDacl(rel);
        return (p, a, "");
    }

    /// <summary>WriteDacl, but only if the folder still matches `expected` (null: no check). Win32Fs checks on the
    /// same handle it writes through; this default checks just before writing.</summary>
    void WriteDacl(string rel, bool isProtected, IReadOnlyList<RawAce> aces, ExpectedAcl? expected)
    {
        if (expected is not null)
        {
            var (p, a, id) = ReadFolder(rel);
            expected.Check(rel, p, a, id);
        }
        WriteDacl(rel, isProtected, aces);
    }

    bool Exists(string rel);

    /// <summary>Create folder; throws IOException if it already exists.</summary>
    void Mkdir(string rel);

    /// <summary>Names of the subfolders (without symlinks/junctions).</summary>
    List<string> Subdirs(string rel);
}

/// <summary>What a folder looked like in the scan: written only if it still does.</summary>
public sealed record ExpectedAcl(bool Protected, IReadOnlyList<RawAce> Explicit, string Id)
{
    static (int, int, uint, string) Key(RawAce a) => (a.Type, a.Flags & ~M.InheritedAce, a.Mask, a.Sid);

    public static ExpectedAcl Of(Folder f) => new(f.Protected,
        f.Explicit.Select(a => new RawAce(a.Allow ? 0 : 1, a.Flags & ~M.InheritedAce, a.Mask, a.Sid)).ToList(), f.Id);

    /// <summary>Throws if the folder now has another identity, protection or explicit entries (a folder swapped in by a
    /// rename, or an ACL changed since the scan).</summary>
    public void Check(string rel, bool isProtected, IEnumerable<RawAce> current, string id)
    {
        var where = rel != "" ? rel : "the share root";
        if (Id != "" && id != "" && Id != id)
            throw new IOException($"{where} is no longer the folder that was scanned (renamed or replaced); owlseye did not write it");
        var now = current.Where(a => (a.Flags & M.InheritedAce) == 0).Select(Key).Order().ToList();
        if (isProtected != Protected || !now.SequenceEqual(Explicit.Select(Key).Order()))
            throw new IOException($"The ACL of {where} was changed outside owlseye; owlseye did not write it");
    }
}
