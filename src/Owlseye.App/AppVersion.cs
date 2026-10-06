using System.Reflection;

namespace Owlseye.App;

/// <summary>The version of this exe, e.g. "1.0.0" (without the commit after "+"), shown in the header.</summary>
public static class AppVersion
{
    public static readonly string Text =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];
}
