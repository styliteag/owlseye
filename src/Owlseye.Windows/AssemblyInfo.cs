using System.Runtime.InteropServices;

// Windows libraries called by P/Invoke (kernel32, advapi32, mpr, netapi32, …) are loaded from System32 only, never
// from the application folder or the current directory, where someone could put a library of the same name.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
