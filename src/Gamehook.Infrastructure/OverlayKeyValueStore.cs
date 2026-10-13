using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Infrastructure;

/// Per-overlay key/value settings, one JSON file per overlay GUID in the profile's "kv\overlays" folder
/// (%LOCALAPPDATA%\Gamehook\kv\overlays\{guid}.json). Overlays are web pages, and the REST API allows any
/// origin, so every value here is untrusted input: ids are re-formatted from a parsed Guid (never
/// used as a path as given), only a JSON object is stored, and it must be plain data: nesting,
/// total size, node count, key names, string lengths and numbers are all bounded, and the store
/// holds a bounded number of overlays.
public sealed class OverlayKeyValueStore
{
    public const int MaxDocumentBytes = 64 * 1024;
    public const int MaxOverlays = 256;
    public const int MaxKeys = 256;
    public const int MaxKeyLength = 128;
    public const int MaxDepth = 16;
    public const int MaxNodes = 10_000;
    public const int MaxStringLength = 2048;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = MaxDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        AllowDuplicateProperties = false,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly FilesystemProvider filesystem;
    private readonly ILogger<OverlayKeyValueStore> logger;
    private readonly object gate = new();

    public OverlayKeyValueStore(FilesystemProvider filesystem, ILogger<OverlayKeyValueStore>? logger = null)
    {
        this.filesystem = filesystem;
        this.logger = logger ?? NullLogger<OverlayKeyValueStore>.Instance;
    }

    /// The canonical lowercase hyphenated form only: no braces, no bare 32-digit form, no padding.
    public static bool TryParseId(string? value, out Guid id)
    {
        id = default;
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out id);
    }

    /// Parses a request body into a top-level JSON object, enforcing the size, depth, and key limits.
    public static JsonObject ParseObject(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxDocumentBytes) throw Failure(KeyValueError.TooLarge, $"The JSON body is larger than {MaxDocumentBytes} bytes.");
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body, nodeOptions: null, ParseOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Failure(KeyValueError.InvalidJson, $"The body is not valid JSON (maximum depth {MaxDepth}, no duplicate keys, no comments): {ex.Message}");
        }

        if (node is not JsonObject document) throw Failure(KeyValueError.InvalidJson, "The body must be a JSON object of key/value pairs.");
        ValidateDocument(document, allowNull: true);
        return document;
    }

    public IReadOnlyList<Guid> List()
    {
        lock (gate)
        {
            var directory = filesystem.GetKeyValueDirectory();
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => TryParseId(name, out var id) ? (Guid?)id : null)
                .OfType<Guid>()
                .Order()
                .ToArray();
        }
    }

    /// The saved document, or null when this overlay has never saved anything.
    public JsonObject? Get(Guid id)
    {
        lock (gate) return Read(id, tolerateCorruption: false);
    }

    /// Shallow merge: each key in the patch replaces that key; a null value removes it. The merged
    /// document is validated before anything is written, so a rejected update changes nothing.
    public JsonObject Merge(Guid id, JsonObject patch)
    {
        ValidateDocument(patch, allowNull: true);
        lock (gate)
        {
            var existing = Read(id, tolerateCorruption: true);
            if (existing is null && List().Count >= MaxOverlays)
            {
                throw Failure(KeyValueError.TooManyOverlays, $"Gamehook stores settings for at most {MaxOverlays} overlays.");
            }

            var merged = existing ?? new JsonObject();
            foreach (var (key, value) in patch)
            {
                if (value is null) merged.Remove(key);
                else merged[key] = value.DeepClone();
            }

            ValidateDocument(merged);
            var json = merged.ToJsonString(WriteOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            {
                throw Failure(KeyValueError.TooLarge, $"The merged settings would be larger than {MaxDocumentBytes} bytes.");
            }

            try
            {
                Directory.CreateDirectory(filesystem.GetKeyValueDirectory());
                AtomicFile.WriteAllText(PathFor(id), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not save overlay settings for {Id}.", id);
                throw Failure(KeyValueError.StorageFailed, "The settings could not be written to disk.");
            }

            return merged;
        }
    }

    private JsonObject? Read(Guid id, bool tolerateCorruption)
    {
        var path = PathFor(id);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxDocumentBytes) throw new JsonException("The file is larger than the allowed size.");
            var document = JsonNode.Parse(File.ReadAllBytes(path), nodeOptions: null, ParseOptions) as JsonObject
                ?? throw new JsonException("The file is not a JSON object.");
            ValidateDocument(document);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException or KeyValueStoreException)
        {
            logger.LogWarning(ex, "Overlay settings file '{Path}' is unreadable.", path);
            if (tolerateCorruption) return null;
            throw Failure(KeyValueError.Unreadable, "The saved settings for this overlay could not be read.");
        }
    }

    private string PathFor(Guid id) => Path.Combine(filesystem.GetKeyValueDirectory(), id.ToString("D") + ".json");

    // Plain data only. Every key, string and number anywhere in the tree is checked, and the total
    // node count is capped so a document within the byte limit cannot still be expensive to walk.
    // A patch's top-level null removes that key; null inside lists and objects is ordinary data.
    private static void ValidateDocument(JsonObject document, bool allowNull = false)
    {
        if (document.Count > MaxKeys) throw Failure(KeyValueError.TooLarge, $"A settings document can have at most {MaxKeys} top-level keys.");
        var nodes = 0;
        foreach (var (key, value) in document)
        {
            if (value is null && !allowNull) throw Failure(KeyValueError.InvalidValue, $"The value of \"{key}\" cannot be null.");
            ValidateKey(key);
            ValidateNode(value, key, ref nodes);
        }
    }

    private static void ValidateNode(JsonNode? node, string name, ref int nodes)
    {
        if (++nodes > MaxNodes) throw Failure(KeyValueError.TooLarge, $"Settings can hold at most {MaxNodes} values in total.");
        switch (node)
        {
            case JsonObject members:
                foreach (var (key, member) in members)
                {
                    ValidateKey(key);
                    ValidateNode(member, key, ref nodes);
                }

                break;
            case JsonArray items:
                foreach (var item in items) ValidateNode(item, name, ref nodes);
                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String when value.GetValue<string>().Length > MaxStringLength:
                        throw Failure(KeyValueError.InvalidValue, $"String values can be at most {MaxStringLength} characters.");
                    case JsonValueKind.Number when !value.TryGetValue<double>(out var number) || !double.IsFinite(number):
                        throw Failure(KeyValueError.InvalidValue, $"The value of \"{name}\" is not a number that fits in a 64-bit float.");
                }

                break;
        }
    }

    private static void ValidateKey(string key)
    {
        if (key.Length is 0 or > MaxKeyLength || key == "__proto__")
        {
            throw Failure(KeyValueError.InvalidKey, $"Keys must be 1 to {MaxKeyLength} characters and not \"__proto__\".");
        }
    }

    private static KeyValueStoreException Failure(KeyValueError error, string message) => new(error, message);
}

public enum KeyValueError
{
    InvalidJson,
    InvalidKey,
    InvalidValue,
    TooLarge,
    TooManyOverlays,
    Unreadable,
    StorageFailed,
}

public sealed class KeyValueStoreException(KeyValueError error, string message) : Exception(message)
{
    public KeyValueError Error { get; } = error;
}
