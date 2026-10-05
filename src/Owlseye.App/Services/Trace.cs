using System.Diagnostics;

namespace Owlseye.App.Services;

/// <summary>Timings for performance work: set OWLSEYE_TRACE=1, lines go to trace.log in the data folder.</summary>
public static class Trace
{
    static readonly bool On = Environment.GetEnvironmentVariable("OWLSEYE_TRACE") == "1";
    static readonly Lock Gate = new();

    public static void Log(string what, Stopwatch sw) => Log($"{what}: {sw.Elapsed.TotalMilliseconds:F1} ms");

    public static void Log(string line)
    {
        if (!On) return;
        lock (Gate) File.AppendAllText(Path.Combine(Paths.DataDir(), "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
    }

    public static bool Enabled => On;
}
