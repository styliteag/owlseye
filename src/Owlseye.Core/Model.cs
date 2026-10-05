// Data model: a snapshot of folders (with ACEs), the accounts in these ACLs and their members.
//
// Rights model (as in a typical Excel list of folder rights): groups appear directly in the folder ACLs.
// A matrix cell is the explicit entry of an account on a folder:
//
//   R   Read, this folder + subfolders + files
//   W   Read + write, this folder + subfolders + files
//   R|  Read this folder only (list, to reach subfolders)
//   W|  Read + write this folder only

using System.Globalization;
using System.Text;

namespace Owlseye;

public static class M
{
    public static readonly string[] Cells = ["R|", "R", "W|", "W", "F"];

    public static double Rank(string? v) => v switch
    {
        null => 0,
        "R|" => 0.5,
        "W|" => 0.75,
        "R" => 1,
        "W" => 2,
        "F" => 3,
        _ => throw new ArgumentException($"Unknown right {v}"),
    };

    public const int ObjectInherit = 0x1, ContainerInherit = 0x2, InheritOnly = 0x8, InheritedAce = 0x10;
    public const int OiCi = ObjectInherit | ContainerInherit, ThisFolder = 0x0;
    public const uint Read = 0x1200A9; // Read, execute
    public const uint WriteNoDelete = Read | 0x116; // + write (create files, append data, attributes), no delete
    public const uint Modify = WriteNoDelete | Delete; // Windows "Modify": read, execute, write and delete
    public const uint Full = 0x1F01FF;
    public const uint Delete = 0x10000, DeleteChild = 0x40, WriteDac = 0x40000, WriteOwner = 0x80000;
    public const uint ReadControl = 0x20000, Synchronize = 0x100000;

    /// <summary>The mask of W: Modify (default, as Windows and most shares use it, also with "keep-folder") or write
    /// without delete (config "write": "no-delete"). Set once at start by <see cref="Configure"/>.</summary>
    public static uint Write { get; private set; } = Modify;

    /// <summary>config "write": "keep-folder": W is Modify on subfolders and files but only write without delete on the
    /// folder itself, so users cannot delete, rename or move the folder that has the W entry (W| likewise).</summary>
    public static bool KeepFolder { get; private set; }

    /// <summary>The ACEs (mask, flags) of a cell value: one, or two for W with "keep-folder".</summary>
    public static IReadOnlyList<(uint Mask, int Flags)> StandardAces(string value) =>
        KeepFolder && value == "W" ? [(WriteNoDelete, ThisFolder), (Modify, OiCi | InheritOnly)] : [Standard[value]];

    /// <summary>The main entry of each cell value (for W with "keep-folder" the one for subfolders and files, see
    /// StandardAces).</summary>
    public static IReadOnlyDictionary<string, (uint Mask, int Flags)> Standard { get; private set; } = StandardFor(Modify, false);

    static Dictionary<string, (uint Mask, int Flags)> StandardFor(uint write, bool keepFolder) => new()
    {
        ["R|"] = (Read, ThisFolder),
        ["R"] = (Read, OiCi),
        ["W|"] = (keepFolder ? WriteNoDelete : write, ThisFolder),
        ["W"] = (write, OiCi),
        ["F"] = (Full, OiCi), // full control: also change permissions and take ownership
    };

    public const string System = "S-1-5-18", Admins = "S-1-5-32-544", CreatorOwner = "S-1-3-0", OwnerRights = "S-1-3-4";

    static HashSet<string> hiddenAccounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default of config.json "full_control": the accounts that must have full control on the share root
    /// and on every folder with broken inheritance (Windows' default).</summary>
    public static readonly IReadOnlyList<string> DefaultFullControl = ["SYSTEM", "Administrators"];

    /// <summary>config.json "full_control" as given: SIDs, names ("DOMAIN\name", "name") or the words SYSTEM,
    /// Administrators, Domain Admins (resolved against the scan, see Rights.RequiredFullControl).</summary>
    public static IReadOnlyList<string> FullControl { get; private set; } = DefaultFullControl;

    /// <summary>Applies config.json: what W means ("modify", "no-delete" or "keep-folder"), further accounts to hide like the
    /// administrators (SIDs or names, "DOMAIN\name" or "name") and the accounts that must have full control. Called at
    /// start before the provider scans, and when the settings are saved.</summary>
    public static void Configure(string write, IEnumerable<string> hidden, IEnumerable<string>? fullControl = null)
    {
        var fc = (fullControl ?? DefaultFullControl).Select(x => x.Trim()).Where(x => x != "").ToList();
        FullControl = fc.Count > 0 ? fc : DefaultFullControl;
        Write = write switch
        {
            "modify" or "keep-folder" => Modify,
            "no-delete" => WriteNoDelete,
            _ => throw new ArgumentException($"config.json: \"write\" must be \"modify\", \"no-delete\" or \"keep-folder\", not \"{write}\""),
        };
        KeepFolder = write == "keep-folder";
        Standard = StandardFor(Write, KeepFolder);
        hiddenAccounts = new HashSet<string>(hidden.Select(h => h.Trim()).Where(h => h != ""), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Accounts that are not matrix columns and whose entries owlseye leaves alone: SYSTEM, Administrators,
    /// Creator Owner, Owner Rights, the domain's Domain Admins (RID 512) and Enterprise Admins (RID 519), and the
    /// accounts listed under "hidden" in config.json (by SID, "DOMAIN\name" or "name").</summary>
    public static bool IsHidden(string sid, string? name = null)
    {
        if (Hidden.Contains(sid)) return true;
        if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.Count(c => c == '-') == 7
            && (sid.EndsWith("-512", StringComparison.Ordinal) || sid.EndsWith("-519", StringComparison.Ordinal)))
            return true;
        if (hiddenAccounts.Count == 0) return false;
        if (hiddenAccounts.Contains(sid)) return true;
        return name is not null && (hiddenAccounts.Contains(name) || hiddenAccounts.Contains(name[(name.LastIndexOf('\\') + 1)..]));
    }

    /// <summary>The matrix shows the hidden accounts too (switch "Hidden accounts" in the matrix, remembered per admin):
    /// they are columns and can be set like any other account. Findings and the desired-state comparison leave them
    /// out either way (IsHidden).</summary>
    public static bool ShowHidden { get; set; }

    /// <summary>Not a matrix column right now: a hidden account while the matrix does not show them, or Creator Owner and
    /// Owner Rights, which stand for whoever owns a file and are never columns.</summary>
    public static bool Hides(string sid, string? name = null) =>
        sid is CreatorOwner or OwnerRights || (!ShowHidden && IsHidden(sid, name));

    /// <summary>Not in the matrix. owlseye sets SYSTEM and Administrators itself on protected folders (full control).</summary>
    public static readonly IReadOnlySet<string> Hidden = new HashSet<string> { System, Admins, CreatorOwner, OwnerRights };

    /// <summary>Groups every logged-in user belongs to (Everyone, Authenticated Users, BUILTIN\Users).</summary>
    public static readonly IReadOnlyList<string> Everyone = ["S-1-1-0", "S-1-5-11", "S-1-5-32-545"];

    public static bool SameShare(string a, string b) =>
        a.TrimEnd('\\').ToLowerInvariant() == b.TrimEnd('\\').ToLowerInvariant();

    const string BadNameChars = "\\/:*?\"<>|"; // what Windows does not allow in folder names

    /// <summary>Folder names that Win32 rewrites or that can point to something else.</summary>
    public static bool BadComponent(string p) =>
        p is "" or "." or ".." || p != p.TrimEnd('.', ' ') || p.Any(c => BadNameChars.Contains(c) || c < 32);

    static readonly string[] Digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "¹", "²", "³"];

    static readonly HashSet<string> Reserved =
    [
        "con", "prn", "aux", "nul", "conin$", "conout$",
        .. Digits.Select(d => "com" + d), .. Digits.Select(d => "lpt" + d),
    ];

    /// <summary>Invisible, direction-changing or odd-space characters: a folder that looks like another one.
    /// Users can create such folders; the admin must not be tricked into granting rights on the lookalike.</summary>
    public static bool SuspiciousName(string name) =>
        name.EnumerateRunes().Any(OddChar) || !IsNfc(name) || MixedScripts(name);

    /// <summary>A name with an unpaired surrogate (possible on NTFS) cannot be normalized at all: that is suspicious too,
    /// and must not make the scan fail (string.IsNormalized throws on it).</summary>
    static bool IsNfc(string name)
    {
        try
        {
            return name.IsNormalized(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static bool OddChar(Rune c)
    {
        var cat = Rune.GetUnicodeCategory(c);
        return cat is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            || (cat == UnicodeCategory.SpaceSeparator && c.Value != ' ');
    }

    /// <summary>Latin letters next to Cyrillic or Greek ones: 'Dаten' with a Cyrillic а.</summary>
    static bool MixedScripts(string name)
    {
        var used = new HashSet<string>();
        foreach (var c in name.EnumerateRunes())
        {
            if (!Rune.IsLetter(c)) continue;
            var v = c.Value;
            if (v is >= 0x41 and < 0x250) used.Add("latin");
            else if (v is >= 0x370 and < 0x400) used.Add("greek");
            else if (v is >= 0x400 and < 0x530) used.Add("cyrillic");
        }
        return used.Count > 1;
    }

    /// <summary>Additionally for new folders: no reserved device names (CON, NUL, … also with extension, "CON .txt").</summary>
    /// <summary>Generic rights (GENERIC_ALL etc., as Windows writes them for Creator Owner) as the file rights they stand for.</summary>
    public static uint MapGeneric(uint mask)
    {
        var m = mask & 0x0FFFFFFF;
        if ((mask & 0x10000000) != 0) m |= Full; // GENERIC_ALL
        if ((mask & 0x80000000) != 0) m |= 0x120089; // GENERIC_READ
        if ((mask & 0x40000000) != 0) m |= 0x120116; // GENERIC_WRITE
        if ((mask & 0x20000000) != 0) m |= 0x1200A0; // GENERIC_EXECUTE
        return m;
    }

    /// <summary>Creator Owner entries owlseye writes: subfolders and files only (whoever creates them gets the right).</summary>
    public const int CreatorOwnerFlags = ObjectInherit | ContainerInherit | InheritOnly;

    /// <summary>Creator Owner and Owner Rights stand for whoever creates or owns a file or folder: no columns, set in the
    /// folder panel (owner entries).</summary>
    public static bool IsOwnerSid(string sid) => sid is CreatorOwner or OwnerRights;

    /// <summary>Values the folder panel offers: Creator Owner W (Modify) or F (full control), for subfolders and files;
    /// Owner Rights V (owners only read the permissions) or W (Modify), for this folder, subfolders and files.</summary>
    public static IReadOnlyList<string> OwnerEntryValues(string sid) => sid == CreatorOwner ? ["W", "F"] : ["V", "W"];

    public static uint OwnerEntryMask(string value) => value switch { "F" => Full, "W" => Modify, _ => ReadControl | Synchronize };

    public static int OwnerEntryFlags(string sid) => sid == CreatorOwner ? CreatorOwnerFlags : OiCi;

    /// <summary>Name of an owner entry in plans and the log.</summary>
    public static string OwnerEntryName(string sid) =>
        sid == CreatorOwner ? "Creator Owner (new files and folders)" : "Owner Rights (owners of files and folders)";

    /// <summary>Longest name owlseye accepts for a new folder.</summary>
    public const int MaxFolderName = 200;

    public static bool BadFolderName(string name) =>
        BadComponent(name) || Reserved.Contains(name.Split('.')[0].TrimEnd(' ').ToLowerInvariant());

    public static string? Stronger(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>Parent folders up to the root, nearest first: "A\B\C" -> ["A\B", "A", ""].</summary>
    public static List<string> Ancestors(string path)
    {
        var o = new List<string>();
        while (path != "")
        {
            var i = path.LastIndexOf('\\');
            path = i >= 0 ? path[..i] : "";
            o.Add(path);
        }
        return o;
    }

    public static int LevelOf(string path) => path == "" ? 0 : path.Count(c => c == '\\') + 1;

    public static string Lower(string s) => s.ToLowerInvariant();

    /// <summary>Ordinal comparison, used on lowercased strings (M.Lower).</summary>
    public static readonly StringComparer Ci = StringComparer.Ordinal;

    /// <summary>Tree order: compares the lowercased path components one by one; a parent comes before its children.</summary>
    public static int TreeCompare(string a, string b)
    {
        var pa = a.Split('\\');
        var pb = b.Split('\\');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var c = string.CompareOrdinal(Lower(pa[i]), Lower(pb[i]));
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    static readonly System.Text.RegularExpressions.Regex SidSyntax = new(@"^S-\d+(-\d+)*$");

    /// <summary>Looks like a SID (S-1-5-21-…). For SIDs that come from files: they are resolved before use anyway.</summary>
    public static bool IsSid(string s) => SidSyntax.IsMatch(s);

    /// <summary>Short display of a right: None -> "–".</summary>
    public static string Short(string? v) => v ?? "–";
}

public sealed record Ace(string Sid, string Name, string Kind, uint Mask, bool Allow = true, bool Inherited = false, int Flags = 0);

/// <param name="Id">identity on disk (volume and file id) where the file system tells it, "" otherwise: owlseye writes
/// only if the folder at this path is still the one it scanned</param>
public sealed record Folder(string Path, int Level, bool Protected = false, IReadOnlyList<Ace>? Aces = null,
    bool OtherAces = false, string Error = "", string Id = "")
{
    /// <summary>Explicit and inherited, as read.</summary>
    public IReadOnlyList<Ace> Aces { get; init; } = Aces ?? [];

    public string Name => Path == "" ? "(root)" : Path[(Path.LastIndexOf('\\') + 1)..];

    public string? Parent => Path == "" ? null : Path.Contains('\\') ? Path[..Path.LastIndexOf('\\')] : "";

    public IReadOnlyList<Ace> Explicit => Aces.Where(a => !a.Inherited).ToList();
}

/// <summary>An account that appears in an ACL: one column of the matrix.</summary>
public sealed record Principal(string Sid, string Name, string Kind, string Dn = "")
{
    public string Short => Name[(Name.LastIndexOf('\\') + 1)..];
}

public sealed record Group(string Dn, string Sam, HashSet<string> Members, string Sid = "");

public sealed record User(string Dn, string Sam, string Display, bool Enabled = true, string Sid = "");

public sealed record Snapshot(
    string Share,
    Dictionary<string, Folder> Folders,
    Dictionary<string, Principal> Principals, // key = SID; all accounts in the ACLs except Hidden
    Dictionary<string, Group> Groups, // key = DN
    Dictionary<string, User> Users, // key = DN
    string TakenAt = "")
{
    /// <summary>Folders whose inherited entries are not what their parent passes down (Rights.Stale); null = work them
    /// out from the ACEs. The planner sets it for the snapshot after a change, where Windows works inheritance out anew
    /// below every folder it writes.</summary>
    public IReadOnlySet<string>? Stale { get; init; }

    public string NameOf(string dn)
    {
        if (Groups.TryGetValue(dn, out var g)) return g.Sam;
        if (Users.TryGetValue(dn, out var u)) return u.Sam;
        var first = dn.Split(',', 2)[0];
        return first.StartsWith("CN=") ? first[3..] : first;
    }
}

public static class Clock
{
    /// <summary>Now in UTC, to the second, as in the log and the desired state: 2026-10-05T17:05:41+00:00.</summary>
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";
}
