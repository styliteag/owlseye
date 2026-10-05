using System.Runtime.InteropServices;
using System.Windows;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

// Windows libraries called by P/Invoke are loaded from System32 only (see Owlseye.Windows/AssemblyInfo.cs).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
