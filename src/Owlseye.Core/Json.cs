using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Owlseye;

/// <summary>owlseye's JSON files: snake_case keys, UTF-8 without escaping non-ASCII,
/// files with indent 1, written atomically.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>One line, for JSON Lines.</summary>
    public static string Line(JsonNode node) => node.ToJsonString(LineOptions);

    public static string Pretty(JsonNode node) => node.ToJsonString(FileOptions);

    /// <summary>Copy of an object with its keys sorted (recursively), like json.dump(sort_keys=True).</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>Write via a temp file in the same folder and replace, so readers never see half a file.</summary>
    public static void WriteAtomic(string path, string text, string prefix)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"{prefix}{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static string? Str(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : null;

    public static long? Long(this JsonNode? n, string key)
    {
        if (n is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.GetValueKind() != JsonValueKind.Number) return null;
        // works for parsed values and for in-memory ones of any CLR number type (int, uint, long, double)
        var text = jv.ToJsonString();
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d == Math.Floor(d)) return (long)d;
        return null;
    }

    public static JsonArray? Arr(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;

    public static JsonObject? Obj(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;

    public static bool Has(this JsonNode? n, string key) => n is JsonObject o && o.ContainsKey(key);

    /// <summary>Whether a JSON value counts as set: not null, false, 0, "" or an empty array or object.</summary>
    public static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
        JsonValue jv when jv.TryGetValue<string>(out var s) => s != "",
        JsonValue jv when jv.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };
}

#pragma warning disable CA1416 // FileStream.Lock: not on macOS; caught below (PlatformNotSupportedException)

/// <summary>Lock on byte 0 of an open file, so two owlseye processes do not write the same file at once.
/// Retries for about 10 seconds.</summary>
public sealed class FileLock : IDisposable
{
    readonly FileStream fs;

    FileLock(FileStream fs) => this.fs = fs;

    public static FileLock Acquire(FileStream fs, int timeoutMs = 10_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                fs.Lock(0, 1);
                return new FileLock(fs);
            }
            catch (IOException) when (Environment.TickCount64 < until)
            {
                Thread.Sleep(50);
            }
            catch (PlatformNotSupportedException)
            {
                return new FileLock(null!); // macOS: no byte-range locks; single process only
            }
        }
    }

    public void Dispose()
    {
        try
        {
            fs?.Unlock(0, 1);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Open (create) a file for appending under the lock; FileShare.ReadWrite like the C runtime's open().</summary>
    public static FileStream OpenShared(string path, FileMode mode = FileMode.OpenOrCreate) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Read a whole text file another process may hold a byte lock on: retry briefly.</summary>
    public static string ReadAllText(string path)
    {
        var until = Environment.TickCount64 + 10_000;
        while (true)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, new System.Text.UTF8Encoding(false));
                return r.ReadToEnd();
            }
            catch (IOException e) when (e is not FileNotFoundException and not DirectoryNotFoundException && Environment.TickCount64 < until)
            {
                Thread.Sleep(50);
            }
        }
    }
}

#pragma warning restore CA1416
