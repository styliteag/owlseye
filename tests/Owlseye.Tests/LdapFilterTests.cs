// LDAP filters as the sim directory parses and matches them.

using Owlseye.Providers;

namespace Owlseye.Tests;

public class LdapFilterTests : TestBase
{
    static readonly Dictionary<string, object?> Group = new()
    {
        ["distinguishedName"] = @"CN=Moore\, Mia,OU=X,DC=demo,DC=local",
        ["objectClass"] = new List<object> { "top", "group" },
        ["groupType"] = -2147483646L,
        ["info"] = "owlseye:v1;path=A;right=R",
    };

    public static TheoryData<string, bool> MatchCases => new()
    {
        { "(objectClass=group)", true },
        { "(objectclass=GROUP)", true },
        { "(objectClass=user)", false },
        { "(info=owlseye:*)", true },
        { "(info=other:*)", false },
        { "(info=*)", true },
        { "(adminCount=*)", false },
        { "(groupType:1.2.840.113556.1.4.803:=2)", true },
        { "(groupType:1.2.840.113556.1.4.803:=4)", false },
        { "(&(objectClass=group)(info=owlseye:*))", true },
        { "(|(objectClass=user)(objectClass=group))", true },
        { "(!(objectClass=group))", false },
        { $"(distinguishedName={AdProvider.LdapEscape((string)Group["distinguishedName"]!)})", true },
    };

    [Theory]
    [MemberData(nameof(MatchCases))]
    public void Matches(string flt, bool expected)
    {
        Assert.Equal(expected, LdapFilter.Matches(LdapFilter.Parse(flt), Group));
    }

    // Unsupported filters throw FormatException.
    [Theory]
    [InlineData("(groupType>=2)")]
    [InlineData("(cn~=x)")]
    [InlineData("(cn=a*b)")]
    [InlineData("(x:1.2.3:=1)")]
    [InlineData("objectClass=group")]
    [InlineData("(&(a=b)")]
    public void UnsupportedFiltersRaise(string flt)
    {
        Assert.Throws<FormatException>(() => LdapFilter.Parse(flt));
    }
}
