// Members through the primary group (Domain Users is not in its own "member" attribute).

using System.Text.Json.Nodes;
using Owlseye.Providers;

namespace Owlseye.Tests;

public sealed class PrimaryGroupTests : TestBase
{
    const string DomainUsers = "S-1-5-21-1-2-3-513";
    const string Dn = "CN=Domain Users,CN=Users,DC=demo,DC=local";

    string Sim()
    {
        var dir = SimSeed.Seed(Path.Combine(Tmp, "sim"));
        var p = Path.Combine(dir, "state.json");
        var s = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        var objects = s["objects"]!.AsObject();
        objects[Dn] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "group"), ["sAMAccountName"] = "Domain Users", ["groupType"] = -2147483646,
            ["member"] = new JsonArray(), ["objectSid"] = DomainUsers,
        };
        objects["CN=Paula Primary,OU=Staff,DC=demo,DC=local"] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "person", "organizationalPerson", "user"), ["sAMAccountName"] = "pprimary",
            ["displayName"] = "Paula Primary", ["userAccountControl"] = 512, ["objectSid"] = "S-1-5-21-1-2-3-1500", ["primaryGroupID"] = 513,
        };
        objects["CN=Otto Other,OU=Staff,DC=demo,DC=local"] = new JsonObject
        {
            ["objectClass"] = new JsonArray("top", "person", "organizationalPerson", "user"), ["sAMAccountName"] = "oother",
            ["displayName"] = "Otto Other", ["userAccountControl"] = 512, ["objectSid"] = "S-1-5-21-1-2-3-1501", ["primaryGroupID"] = 514,
        };
        s["sids"]![DomainUsers] = new JsonArray("Domain Users", "DEMO", 2);
        s["acls"]!["HR"]!["aces"]!.AsArray().Add(new JsonArray(0, 3, (long)M.Read, DomainUsers));
        File.WriteAllText(p, s.ToJsonString());
        return dir;
    }

    [Fact]
    public void DomainUsersGetsItsMembersThroughPrimaryGroupId()
    {
        var snap = new SimProvider(Sim()).Scan();
        var members = Rights.MembersOf(snap, DomainUsers);
        Assert.NotNull(members);
        Assert.Contains("CN=Paula Primary,OU=Staff,DC=demo,DC=local", members);
        Assert.DoesNotContain("CN=Otto Other,OU=Staff,DC=demo,DC=local", members);
        var ur = Rights.UserRights(snap);
        Assert.Equal("R", ur["CN=Paula Primary,OU=Staff,DC=demo,DC=local"]["HR"]);
    }
}
