// Desired state of a share as owlseye last set it: the matrix cells (account x folder)
// and the folders with broken inheritance.
//
// One file per share (desired-<share>.json) in the admin's AppData folder or in the `baseline` folder from
// config.json. Written under file lock (read-modify-write) so two owlseye windows do not
// overwrite each other; replacement is atomic. Files from the earlier AGDLP model (without "cells")
// count as absent and are replaced by the current state on first start.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Owlseye;

public sealed class Desired
{
    public Dictionary<(string Sid, string Path), string> Cells { get; init; } = []; // (sid, path) -> R| | R | W| | W
    public Dictionary<string, string> Names { get; init; } = []; // sid -> DOMAIN\name, for display
    public HashSet<string> Protected { get; init; } = []; // folders with broken inheritance; absent = inherits
}

/// <summary>Desired-state file unreadable or broken. Never overwrite, admin must investigate.</summary>
public sealed class BaselineError(string message) : Exception(message);

public sealed record BaselineInfo(string Path, bool Exists, string Updated, string By, bool Legacy);

public sealed partial class BaselineStore
{
    readonly Dictionary<string, JsonObject> mem = [];

    /// <summary>directory = null: in memory only (demo).</summary>
    public BaselineStore(string? directory)
    {
        Dir = directory;
        if (directory is not null) Directory.CreateDirectory(directory);
    }

    public string? Dir { get; }

    [GeneratedRegex(@"[^\w.-]+")]
    private static partial Regex Unsafe();

    /// <summary>\\fs01\Data -> desired-fs01_data.json, E:\Share -> desired-e_share.json</summary>
    public static string FileName(string share)
    {
        var name = Unsafe().Replace(share.ToLowerInvariant(), "_").Trim('.', '_');
        return "desired-" + (name == "" ? "share" : name) + ".json";
    }

    public string? PathOf(string share) => Dir is null ? null : System.IO.Path.Combine(Dir, FileName(share));

    static JsonObject Validate(JsonNode? data)
    {
        var ok = data is JsonObject o && o.Str("share") is not null;
        if (ok)
        {
            var cells = data.Arr("cells");
            var names = data.Obj("names");
            var prot = data.Arr("protected");
            ok = cells is not null && names is not null && prot is not null
                && cells.All(c => c is JsonArray a && a.Count == 3 && a.All(IsStr) && M.Cells.Contains((string)a[2]!) && M.IsSid((string)a[0]!))
                && names.All(kv => IsStr(kv.Value) && M.IsSid(kv.Key))
                && prot.All(IsStr);
        }
        if (!ok) throw new BaselineError("unexpected format");
        return (JsonObject)data!;
    }

    static bool IsStr(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    JsonObject? ReadRaw(string share)
    {
        var path = PathOf(share);
        if (path is null) return mem.GetValueOrDefault(share.ToLowerInvariant());
        if (!File.Exists(path)) return null;
        JsonObject data;
        try
        {
            var node = JsonNode.Parse(FileLock.ReadAllText(path));
            if (node is JsonObject legacy && !legacy.ContainsKey("cells") && legacy.ContainsKey("groups"))
                return null; // desired state from the AGDLP model: start over
            data = Validate(node);
        }
        catch (Exception e) when (e is JsonException or BaselineError) // invalid UTF-8 decodes with U+FFFD in .NET
        {
            throw new BaselineError($"{path}: {e.Message}");
        }
        if (!M.SameShare(data.Str("share")!, share)) // two shares, same sanitized name
            throw new BaselineError($"{path}: belongs to {data.Str("share")}");
        return data;
    }

    void WriteRaw(string share, JsonObject entry)
    {
        var path = PathOf(share);
        if (path is null)
        {
            mem[share.ToLowerInvariant()] = entry;
            return;
        }
        Json.WriteAtomic(path, Json.Pretty(Json.Sorted(entry)!), ".desired-");
    }

    public Desired? Load(string share)
    {
        var data = ReadRaw(share);
        if (data is null) return null;
        var cells = new Dictionary<(string, string), string>();
        foreach (var c in data.Arr("cells")!)
            cells[((string)c![0]!, (string)c[1]!)] = (string)c[2]!;
        var names = data.Obj("names")!.ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
        var prot = data.Arr("protected")!.Select(p => (string)p!).ToHashSet();
        return new Desired { Cells = cells, Names = names, Protected = prot };
    }

    /// <summary>For display: location, whether present, last changed by/at. Never throws (even on a broken file).</summary>
    public BaselineInfo Info(string share)
    {
        var path = PathOf(share);
        JsonNode? raw = path is null ? mem.GetValueOrDefault(share.ToLowerInvariant()) : null;
        if (path is not null && File.Exists(path))
        {
            try
            {
                raw = JsonNode.Parse(FileLock.ReadAllText(path)) ?? new JsonObject();
            }
            catch (Exception)
            {
                raw = new JsonObject();
            }
        }
        var exists = raw is not null;
        var obj = raw as JsonObject ?? new JsonObject();
        return new BaselineInfo(path ?? "", exists, obj.Str("updated") ?? "", obj.Str("by") ?? "",
            obj.ContainsKey("groups") && !obj.ContainsKey("cells"));
    }

    FileStream OpenLock(string path) => FileLock.OpenShared(System.IO.Path.ChangeExtension(path, ".lock"));

    /// <summary>Discard the desired state (even a broken file). On the next scan the actual state becomes desired.</summary>
    public bool Delete(string share)
    {
        var path = PathOf(share);
        if (path is null) return mem.Remove(share.ToLowerInvariant());
        using var lf = OpenLock(path);
        using var _ = FileLock.Acquire(lf);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public void Save(string share, Desired desired, string actor) => Update(share, _ => desired, actor);

    /// <summary>change receives the current desired state (empty if none) and returns the new one.</summary>
    public Desired Update(string share, Func<Desired, Desired> change, string actor)
    {
        var path = PathOf(share);
        if (path is null) return Apply(share, change, actor);
        using var lf = OpenLock(path);
        using var _ = FileLock.Acquire(lf);
        return Apply(share, change, actor);
    }

    Desired Apply(string share, Func<Desired, Desired> change, string actor)
    {
        var neu = change(Load(share) ?? new Desired());
        var cells = neu.Cells.Select(kv => new[] { kv.Key.Sid, kv.Key.Path, kv.Value })
            .OrderBy(c => c[0], StringComparer.Ordinal).ThenBy(c => c[1], StringComparer.Ordinal).ThenBy(c => c[2], StringComparer.Ordinal);
        var withCells = neu.Cells.Keys.Select(k => k.Sid).ToHashSet();
        var entry = new JsonObject
        {
            ["share"] = share,
            ["updated"] = Clock.Now(),
            ["by"] = actor,
            ["cells"] = new JsonArray(cells.Select(c => (JsonNode)new JsonArray(c.Select(x => (JsonNode)x!).ToArray())).ToArray()),
            ["names"] = new JsonObject(neu.Names.Where(kv => withCells.Contains(kv.Key))
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["protected"] = new JsonArray(neu.Protected.OrderBy(M.Lower, M.Ci).Select(p => (JsonNode)p!).ToArray()),
        };
        WriteRaw(share, entry);
        return neu;
    }
}
