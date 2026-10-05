// Audit log as JSON Lines, append-only, with file lock (shared on an admin share).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Owlseye;

public sealed class AuditLog
{
    public AuditLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
    }

    public string Path { get; }

    public JsonObject Append(JsonObject fields)
    {
        var entry = new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["ts"] = Clock.Now() };
        foreach (var (k, v) in fields) entry[k] = v?.DeepClone();
        var line = Encoding.UTF8.GetBytes(Json.Line(entry) + "\n");
        using var fs = FileLock.OpenShared(Path);
        using (FileLock.Acquire(fs))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(line);
            fs.Flush(true);
        }
        return entry;
    }

    /// <summary>Newest first, each apply as one entry (see Fold).</summary>
    public List<JsonObject> Entries(int limit = 200)
    {
        if (!File.Exists(Path)) return [];
        var rows = FileLock.ReadAllText(Path).Split('\n').Select(Parse).OfType<JsonObject>().ToList();
        rows.Reverse();
        return Fold(rows).Take(limit).ToList();
    }

    public JsonObject? Get(string entryId) => Entries(10_000).FirstOrDefault(e => e.Str("id") == entryId);

    /// <summary>An apply logs a start, a step per write and an end; the end is the entry. The start stays only while
    /// there is no end (owlseye was stopped while writing, or is still writing): with its steps as its changes, and
    /// the planned writes that did not happen as `missing`. rows: newest first.</summary>
    public static List<JsonObject> Fold(List<JsonObject> rows)
    {
        var ended = rows.Where(e => e.Str("kind") == "change").Select(e => e.Str("run")).ToHashSet();
        var steps = new Dictionary<string, List<JsonObject>>();
        for (var i = rows.Count - 1; i >= 0; i--) // in the order they were written
        {
            var e = rows[i];
            if (e.Str("kind") != "change_step") continue;
            var run = e.Str("run") ?? "";
            if (!steps.TryGetValue(run, out var l)) steps[run] = l = [];
            l.Add(e);
        }
        var o = new List<JsonObject>();
        foreach (var row in rows)
        {
            var kind = row.Str("kind");
            if (kind == "change_step" || (kind == "change_start" && ended.Contains(row.Str("id")))) continue;
            var e = row;
            if (kind == "change_start")
            {
                var done = steps.GetValueOrDefault(row.Str("id")!) ?? [];
                var create = done.Where(s => s.ContainsKey("create")).Select(s => s["create"]?.DeepClone()).ToList();
                var acl = done.Select(s => s["acl_op"]).OfType<JsonObject>().Select(a => a.DeepClone()).ToList();
                var createdNames = create.Select(c => c?.ToJsonString()).ToHashSet();
                var written = acl.Select(a => a.Str("path")).ToHashSet();
                var missing = new List<JsonNode?>();
                // only paths (strings) count; anything else in a hand-edited line is left out
                foreach (var p in (row.Arr("planned_create") ?? []).OfType<JsonValue>())
                    if (p.TryGetValue<string>(out var c) && !createdNames.Contains(p.ToJsonString())) missing.Add(c);
                foreach (var p in (row.Arr("planned_acl") ?? []).OfType<JsonValue>())
                    if (p.TryGetValue<string>(out var s) && !written.Contains(s)) missing.Add(s);
                e = (JsonObject)row.DeepClone();
                e["create_ops"] = new JsonArray(create.ToArray());
                e["acl_ops"] = new JsonArray(acl.ToArray());
                e["missing"] = new JsonArray(missing.ToArray());
            }
            o.Add(e);
        }
        return o;
    }

    /// <summary>Skip broken or foreign lines instead of crashing the page.</summary>
    static JsonObject? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonNode.Parse(line) is JsonObject o && o.Str("id") is not null ? o : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
