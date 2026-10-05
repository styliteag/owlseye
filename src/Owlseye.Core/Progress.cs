namespace Owlseye;

/// <summary>What a long scan or apply is doing right now: shown by the progress page and the busy overlay,
/// asked by the window before it closes.</summary>
/// <param name="Task">"scan", "apply" or "" (idle)</param>
/// <param name="Phase">what happens right now, in words for the admin</param>
/// <param name="Path">folder being read or written</param>
/// <param name="Done">folders read (scan) or operations finished (apply)</param>
/// <param name="Total">operations planned (apply); a scan does not know its total in advance</param>
public sealed record Status(string Task = "", string Phase = "", string Path = "", int Done = 0, int Total = 0);

/// <summary>Never takes the state lock: the UI has to read it while a scan or apply holds that.</summary>
public sealed class Progress
{
    readonly Lock gate = new();

    public Status Now { get; private set; } = new();

    /// <summary>Raised after every change (from the worker thread). Listeners throttle themselves.</summary>
    public event Action<Status>? Changed;

    public void Set(string? phase = null, string? path = null, int? done = null, int? total = null)
    {
        Status s;
        lock (gate)
        {
            Now = s = Now with
            {
                Phase = phase ?? Now.Phase,
                Path = path ?? Now.Path,
                Done = done ?? Now.Done,
                Total = total ?? Now.Total,
            };
        }
        Changed?.Invoke(s);
    }

    /// <summary>Not nested: an inner task (the rescan at the end of an apply) takes over the display and ends it.</summary>
    public IDisposable Task(string name, string phase = "", int total = 0)
    {
        var s = new Status(name, phase, Total: total);
        lock (gate) Now = s;
        Changed?.Invoke(s);
        return new End(this);
    }

    sealed class End(Progress p) : IDisposable
    {
        public void Dispose()
        {
            var s = new Status();
            lock (p.gate) p.Now = s;
            p.Changed?.Invoke(s);
        }
    }
}
