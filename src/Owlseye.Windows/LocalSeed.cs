// Test data for the local provider: the demo share as real folders, local groups and users, ACLs.
//
// As administrator:  owlseye.exe seed-local E:\Share
//
// - Users owl.<name> (disabled: no logon, not on the lock screen), groups G-*/P-* as in the demo. Local groups
//   cannot contain groups; nested demo groups are resolved.
// - Folders and explicit ACLs as in the demo (including the legacy issues for the findings). Other folders under
//   the root get an empty explicit ACL (they only inherit).
// - Cleans up leftovers from the earlier AGDLP model: groups DL_FS_*, earlier owl.* test users, local-nesting.json.

using System.DirectoryServices.AccountManagement;
using System.Security.Cryptography;
using Owlseye.Providers;

namespace Owlseye.Windows;

public static class LocalSeed
{
    const string TestUser = "owlseye test user", TestGroup = "owlseye test group";

    static HashSet<string> FlatMembers(string group)
    {
        var o = new HashSet<string>();
        foreach (var m in Demo.Groups.First(g => g.Sam == group).Members)
        {
            if (Demo.IsGroup(m)) o.UnionWith(FlatMembers(m));
            else o.Add(m);
        }
        return o;
    }

    static void Cleanup(PrincipalContext ctx, HashSet<string> keepUsers, Action<string> log)
    {
        using (var gs = new PrincipalSearcher(new GroupPrincipal(ctx)))
            foreach (var g in gs.FindAll().OfType<GroupPrincipal>().ToList())
                if (g.SamAccountName.StartsWith("DL_FS_", StringComparison.OrdinalIgnoreCase) && g.Description == TestGroup)
                {
                    g.Delete();
                    log($"removed group {g.SamAccountName}");
                }
        using (var us = new PrincipalSearcher(new UserPrincipal(ctx)))
            foreach (var u in us.FindAll().OfType<UserPrincipal>().ToList())
                if (u.SamAccountName.StartsWith("owl.") && !keepUsers.Contains(u.SamAccountName) && u.Description == TestUser)
                {
                    u.Delete();
                    log($"removed user  {u.SamAccountName}");
                }
        var nesting = Path.Combine(Paths.DataDir(), "local-nesting.json");
        if (File.Exists(nesting)) File.Delete(nesting);
    }

    /// <summary>Marks a folder as demo share; seed-local refuses other non-empty folders (it resets their subfolders' ACLs).</summary>
    public const string Marker = ".owlseye-demo";

    public static void Seed(string share, Action<string> log, bool force = false)
    {
        var (snap, _) = Demo.Build();
        if (!force && Directory.Exists(share) && !File.Exists(Path.Combine(share, Marker))
            && Directory.EnumerateFileSystemEntries(share).Any())
            throw new InvalidOperationException(
                $"{share} is not empty and not a demo share made by seed-local. It would reset the ACLs of every folder in it. "
                + "Use an empty folder (or --force if you mean it).");
        var fs = new LocalFs();
        var dir = new LocalDirectory();
        Directory.CreateDirectory(share);
        var root = fs.ToUnc(share);
        var users = Demo.Users.ToDictionary(u => u.Sam, u => $"owl.{u.Sam}");
        using var ctx = new PrincipalContext(ContextType.Machine);
        Cleanup(ctx, users.Values.ToHashSet(), log);

        foreach (var (sam, full) in Demo.Users)
        {
            var name = users[sam];
            if (UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, name) is not null) continue;
            using var u = new UserPrincipal(ctx)
            {
                SamAccountName = name,
                Name = name,
                DisplayName = full,
                Description = TestUser,
                Enabled = false,
                PasswordNeverExpires = true,
            };
            u.SetPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)) + "aA1!");
            u.Save();
            log($"user  {name}");
        }
        foreach (var (g, _) in Demo.Groups)
        {
            var gp = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, g);
            if (gp is null)
            {
                gp = new GroupPrincipal(ctx) { SamAccountName = g, Name = g, Description = TestGroup };
                gp.Save();
                log($"group {g}");
            }
            using (gp)
            {
                var changed = false;
                foreach (var sam in FlatMembers(g).Order(StringComparer.Ordinal))
                {
                    var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, users[sam]);
                    if (up is not null && !gp.Members.Contains(up))
                    {
                        gp.Members.Add(up);
                        changed = true;
                    }
                }
                if (changed) gp.Save();
            }
        }

        var sidMap = new Dictionary<string, string>();
        foreach (var (g, _) in Demo.Groups) sidMap[Demo.Gsid(g)] = dir.Sid(g);
        foreach (var (s, n) in users) sidMap[Demo.Usid(s)] = dir.Sid(n);
        var wanted = snap.Folders.Keys.Select(M.Lower).ToHashSet();
        foreach (var f in snap.Folders.Values.OrderBy(f => f.Level))
        {
            Directory.CreateDirectory(f.Path != "" ? Path.Combine(root, f.Path) : root);
            var raw = f.Explicit.Select(a => new RawAce(a.Allow ? 0 : 1, a.Flags, a.Mask, sidMap.GetValueOrDefault(a.Sid, a.Sid)))
                .OrderBy(r => -r.Type).ToList();
            fs.WriteDacl(f.Path, f.Protected, raw);
        }
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, d);
            if (!wanted.Contains(M.Lower(rel)))
            {
                fs.WriteDacl(rel, false, []);
                log($"reset {rel}");
            }
        }
        File.WriteAllText(Path.Combine(root, Marker), "made by owlseye seed-local\n");
        log($"done: {root}");
    }
}
