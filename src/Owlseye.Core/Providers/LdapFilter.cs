// Small LDAP filter (RFC 4515) for the AD emulation.
//
// Only what owlseye actually sends: & | !, equality (case-insensitive), presence `=*`,
// prefix `=value*` and the AD bit rules 803 (AND) and 804 (OR). Everything else throws
// FormatException so a wrong filter does not silently pass as an empty result.

using System.Text;
using System.Text.RegularExpressions;

namespace Owlseye.Providers;

public sealed record LdapNode(string Op, IReadOnlyList<LdapNode> Children, string Attr = "", string Value = "", string Rule = "");

public static partial class LdapFilter
{
    public const string BitAnd = "1.2.840.113556.1.4.803", BitOr = "1.2.840.113556.1.4.804";

    [GeneratedRegex(@"^([A-Za-z][\w-]*)(?::([\d.]+))?(:?=)(.*)$", RegexOptions.Singleline)]
    private static partial Regex Item();

    public static string Unescape(string v)
    {
        var bytes = new List<byte>();
        var src = Encoding.UTF8.GetBytes(v);
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i] == '\\' && i + 2 < src.Length && IsHex(src[i + 1]) && IsHex(src[i + 2]))
            {
                bytes.Add(Convert.ToByte(Encoding.ASCII.GetString(src, i + 1, 2), 16));
                i += 2;
            }
            else bytes.Add(src[i]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static LdapNode Parse(string flt)
    {
        var (node, end) = ParseAt(flt, 0);
        if (end != flt.Length) throw new FormatException($"Trailing input after filter: {Msg.Quote(flt[end..])}");
        return node;
    }

    static (LdapNode, int) ParseAt(string s, int i)
    {
        if (i >= s.Length || s[i] != '(') throw new FormatException($"'(' expected at position {i}: {Msg.Quote(s)}");
        i++;
        if (i < s.Length && "&|!".Contains(s[i]))
        {
            var op = s[i].ToString();
            i++;
            var children = new List<LdapNode>();
            while (i < s.Length && s[i] == '(')
            {
                var (child, next) = ParseAt(s, i);
                children.Add(child);
                i = next;
            }
            if (i >= s.Length || s[i] != ')' || children.Count == 0 || (op == "!" && children.Count != 1))
                throw new FormatException($"Invalid expression {op} in {Msg.Quote(s)}");
            return (new LdapNode(op, children), i + 1);
        }
        var end = s.IndexOf(')', i);
        if (end < 0) throw new FormatException($"')' missing: {Msg.Quote(s)}");
        return (ItemOf(s[i..end]), end + 1);
    }

    static LdapNode ItemOf(string text)
    {
        var m = Item().Match(text);
        if (!m.Success) throw new FormatException($"Unsupported filter: ({text})");
        var attr = m.Groups[1].Value;
        var rule = m.Groups[2].Success ? m.Groups[2].Value : "";
        var eq = m.Groups[3].Value;
        var value = m.Groups[4].Value;
        if (rule != "" || eq == ":=")
        {
            if (!((rule == BitAnd || rule == BitOr) && eq == ":=")) throw new FormatException($"Unsupported matching rule: ({text})");
            return new LdapNode("bit", [], attr, value, rule);
        }
        if (value == "*") return new LdapNode("present", [], attr);
        if (value.Length > 0 && value[..^1].Contains('*')) throw new FormatException($"Only prefix wildcards supported: ({text})");
        if (value.EndsWith('*')) return new LdapNode("prefix", [], attr, Unescape(value[..^1]).ToLowerInvariant());
        return new LdapNode("eq", [], attr, Unescape(value).ToLowerInvariant());
    }

    static List<object> Values(IReadOnlyDictionary<string, object?> obj, string attr)
    {
        foreach (var (k, v) in obj)
        {
            if (!string.Equals(k, attr, StringComparison.OrdinalIgnoreCase)) continue;
            return v switch
            {
                null => [],
                string s when s == "" => [],
                string s => [s],
                System.Collections.IEnumerable e => e.Cast<object>().ToList(),
                _ => [v],
            };
        }
        return [];
    }

    static string Str(object v) => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!.ToLowerInvariant();

    public static bool Matches(LdapNode node, IReadOnlyDictionary<string, object?> obj)
    {
        switch (node.Op)
        {
            case "&": return node.Children.All(c => Matches(c, obj));
            case "|": return node.Children.Any(c => Matches(c, obj));
            case "!": return !Matches(node.Children[0], obj);
        }
        var vals = Values(obj, node.Attr);
        switch (node.Op)
        {
            case "present": return vals.Count > 0;
            case "bit":
                var bits = long.Parse(node.Value, System.Globalization.CultureInfo.InvariantCulture);
                return node.Rule == BitAnd
                    ? vals.Any(v => (Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) & bits) == bits)
                    : vals.Any(v => (Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) & bits) != 0);
            case "prefix": return vals.Any(v => Str(v).StartsWith(node.Value, StringComparison.Ordinal));
            default: return vals.Any(v => Str(v) == node.Value);
        }
    }
}
