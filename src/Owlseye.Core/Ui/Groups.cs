// The Groups page: groups with rights on the share (or all groups owlseye read from the directory), their rights,
// members and nesting, and a membership matrix users x groups.

namespace Owlseye.Ui;

/// <param name="Users">users that are members, directly or through nested groups</param>
/// <param name="Folders">folders where the group has an entry of its own (all scanned levels)</param>
/// <param name="Everyone">every user is a member (Domain Users, Authenticated Users, an all-staff group): left out of the
/// membership matrix, where it would only be a full column</param>
public sealed record GroupRow(Principal P, int Users, int Folders, bool Everyone);

/// <param name="Via">"" for a direct member, otherwise the direct group of the user through which they are a member</param>
public sealed record GroupMember(User User, string Via);

/// <param name="Own">an entry on this folder (otherwise inherited)</param>
public sealed record GroupRight(Folder Folder, string Right, bool Own);

public sealed record GroupsView(IReadOnlyList<GroupRow> Groups, GroupRow? Selected, IReadOnlyList<GroupRight> Rights,
    IReadOnlyList<GroupMember> Members, IReadOnlyList<Principal> Nested, IReadOnlyList<Principal> MemberOf, string Q, bool All);

/// <param name="Marks">one character per column: 'D' direct member, 'N' member through a nested group, ' ' none</param>
public sealed record MembershipRow(User User, string Marks);

public sealed record MembershipView(IReadOnlyList<Principal> Columns, IReadOnlyList<MembershipRow> Rows,
    IReadOnlyList<Principal> Everyone, string Q, bool All);

public sealed partial class Session
{
    /// <summary>Who is in which group, worked out once per page view.</summary>
    sealed class Membership
    {
        public required Dictionary<string, Group> ByDn { get; init; }
        public required Dictionary<string, HashSet<string>> Transitive { get; init; } // member dn -> groups (all levels)
        public required Dictionary<string, List<string>> DirectOf { get; init; } // member dn -> groups it is listed in

        public bool In(string userDn, string groupDn) => Transitive.TryGetValue(userDn, out var gs) && gs.Contains(groupDn);
    }

    Membership MembershipOf(Snapshot snap)
    {
        var directOf = new Dictionary<string, List<string>>();
        foreach (var (gdn, g) in snap.Groups)
            foreach (var m in g.Members)
            {
                if (!directOf.TryGetValue(m, out var l)) directOf[m] = l = [];
                l.Add(gdn);
            }
        return new Membership
        {
            ByDn = snap.Groups,
            Transitive = Cache(snap)?.Transitive ?? Rights.Transitive(snap),
            DirectOf = directOf,
        };
    }

    /// <summary>Groups with rights (the matrix columns that are groups), or all groups read from the directory: those
    /// with rights and the groups nested in them. Hidden accounts are left out.</summary>
    List<(Principal P, Group? G)> KnownGroups(Snapshot snap, bool all)
    {
        var o = new Dictionary<string, (Principal, Group?)>();
        foreach (var p in St.Columns().Where(p => p.Kind is "group" or "wellknown"))
            o[p.Sid] = (p, p.Dn != "" ? snap.Groups.GetValueOrDefault(p.Dn) : null);
        if (all)
            foreach (var g in snap.Groups.Values.Where(g => g.Sid != "" && !M.IsHidden(g.Sid, g.Sam)))
                if (!o.ContainsKey(g.Sid))
                    o[g.Sid] = (snap.Principals.GetValueOrDefault(g.Sid) ?? new Principal(g.Sid, g.Sam, "group", g.Dn), g);
        return o.Values.OrderBy(x => M.Lower(x.Item1.Short), M.Ci).ToList();
    }

    static bool IsDomainUsers(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.Count(c => c == '-') == 7 && sid.EndsWith("-513", StringComparison.Ordinal);

    List<string> UsersIn(Snapshot snap, Principal p, Group? g, Membership m) =>
        M.Everyone.Contains(p.Sid) ? snap.Users.Keys.ToList()
        : g is null ? []
        : snap.Users.Keys.Where(u => m.In(u, g.Dn)).ToList();

    GroupRow RowOf(Snapshot snap, Principal p, Group? g, Membership m, Dictionary<string, int> folders)
    {
        var users = UsersIn(snap, p, g, m).Count;
        var everyone = M.Everyone.Contains(p.Sid) || IsDomainUsers(p.Sid) || (snap.Users.Count > 1 && users == snap.Users.Count);
        return new GroupRow(p, users, folders.GetValueOrDefault(p.Sid), everyone);
    }

    Dictionary<string, int> OwnFolders() =>
        St.Cells.Where(kv => kv.Value.Direct is not null && kv.Value.Source == kv.Key.Path)
            .GroupBy(kv => kv.Key.Sid).ToDictionary(g => g.Key, g => g.Count());

    public GroupsView GroupsPage(string q = "", string sid = "", bool all = false)
    {
        lock (St.Lock)
        {
            var snap = St.Snap;
            var m = MembershipOf(snap);
            var folders = OwnFolders();
            var known = KnownGroups(snap, all);
            var rows = known.Where(x => x.P.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => RowOf(snap, x.P, x.G, m, folders)).ToList();
            var pick = known.FirstOrDefault(x => x.P.Sid == sid);
            if (pick.P is null) return new GroupsView(rows, null, [], [], [], [], q, all);

            var (p, g) = pick;
            var rights = new List<GroupRight>();
            foreach (var f in St.Folders())
                if (St.Cells.TryGetValue((p.Sid, f.Path), out var c) && c.Effective is not null)
                    rights.Add(new GroupRight(f, c.Effective, c.Direct is not null && c.Source == f.Path));

            var members = new List<GroupMember>();
            if (g is not null)
                foreach (var u in UsersIn(snap, p, g, m))
                {
                    var direct = m.DirectOf.GetValueOrDefault(u) ?? [];
                    var via = direct.Contains(g.Dn) ? ""
                        : direct.Where(d => m.Transitive.GetValueOrDefault(d)?.Contains(g.Dn) == true)
                            .Select(d => m.ByDn[d].Sam).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
                    members.Add(new GroupMember(snap.Users[u], via));
                }
            members = members.OrderBy(x => x.Via != "").ThenBy(x => M.Lower(x.User.Display), M.Ci).ToList();

            Principal PrincipalOf(Group x) => snap.Principals.GetValueOrDefault(x.Sid) ?? new Principal(x.Sid, x.Sam, "group", x.Dn);
            var nested = g is null ? [] : g.Members.Where(m.ByDn.ContainsKey).Select(d => PrincipalOf(m.ByDn[d]))
                .OrderBy(x => M.Lower(x.Short), M.Ci).ToList();
            var memberOf = g is null ? [] : (m.DirectOf.GetValueOrDefault(g.Dn) ?? []).Select(d => PrincipalOf(m.ByDn[d]))
                .OrderBy(x => M.Lower(x.Short), M.Ci).ToList();
            return new GroupsView(rows, RowOf(snap, p, g, m, folders), rights, members, nested, memberOf, q, all);
        }
    }

    /// <summary>Users x groups: who is a member of which group, directly or through a nested group. Groups every user is
    /// in are listed apart instead of as full columns; only users in at least one column are rows.</summary>
    public MembershipView MembershipMatrix(string q = "", bool all = false)
    {
        lock (St.Lock)
        {
            var snap = St.Snap;
            var m = MembershipOf(snap);
            var folders = OwnFolders();
            var known = KnownGroups(snap, all).Select(x => (x.P, x.G, Row: RowOf(snap, x.P, x.G, m, folders))).ToList();
            var everyone = known.Where(x => x.Row.Everyone).Select(x => x.P).ToList();
            var cols = known.Where(x => !x.Row.Everyone && x.G is not null).ToList();
            var rows = new List<MembershipRow>();
            foreach (var u in snap.Users.Values.OrderBy(u => M.Lower(u.Display), M.Ci))
            {
                if (q != "" && !(u.Display + " " + u.Sam).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                var direct = m.DirectOf.GetValueOrDefault(u.Dn) ?? [];
                var marks = new string(cols.Select(c => direct.Contains(c.G!.Dn) ? 'D' : m.In(u.Dn, c.G.Dn) ? 'N' : ' ').ToArray());
                if (marks.Trim() != "") rows.Add(new MembershipRow(u, marks));
            }
            return new MembershipView(cols.Select(c => c.P).ToList(), rows, everyone, q, all);
        }
    }
}
