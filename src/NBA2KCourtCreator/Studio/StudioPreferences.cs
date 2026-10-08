using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace NBA2KCourtCreator.Studio;

internal sealed record StudioPreferences(string[] Favorites, string[] Recent, bool Dark, string PrimaryColor = "#19583F")
{
    internal const int MaximumBytes = 1024 * 1024;
    internal static StudioPreferences Read(string path)
    {
        JsonObject settings;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Preferences exceed the size limit.");
            var data = new byte[(int)stream.Length]; stream.ReadExactly(data);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Preferences changed while reading.");
            using var reader = new StreamReader(new MemoryStream(data, writable: false), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            settings = JsonNode.Parse(reader.ReadToEnd(), documentOptions: new JsonDocumentOptions { MaxDepth = 8 }) as JsonObject
                ?? throw new InvalidDataException("Preferences must be an object.");
        }
        catch (JsonException error) { throw new InvalidDataException("Preferences contain invalid JSON.", error); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Preferences contain invalid text encoding.", error); }
        return FromDocument(settings);
    }
    internal static StudioPreferences FromDocument(JsonObject settings)
    {
        if (settings.Any(pair => pair.Key is not ("favorites" or "recent" or "dark" or "primaryColor"))) throw new InvalidDataException("Preferences contain unrecognized settings.");
        string[] ReadIds(string key, int limit)
        {
            if (settings[key] is null) return [];
            if (settings[key] is not JsonArray items || items.Count > 10000) throw new InvalidDataException("Invalid " + key + " preferences.");
            var ids = new List<string>();
            foreach (var item in items)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.Contains('\0'))
                    throw new InvalidDataException("Invalid court ID in " + key + ".");
                ids.Add(id);
            }
            return ids.Distinct(StringComparer.Ordinal).Take(limit).ToArray();
        }
        var dark = false;
        if (settings["dark"] is not null && (settings["dark"] is not JsonValue value || !value.TryGetValue<bool>(out dark)))
            throw new InvalidDataException("Invalid theme preference.");
        var primary = "#19583F";
        if (settings["primaryColor"] is { } primaryNode)
        {
            if (primaryNode is not JsonValue primaryValue || !primaryValue.TryGetValue<string>(out var primaryText) || StudioImages.Hex(primaryText) is not { } hex)
                throw new InvalidDataException("Invalid primary color preference.");
            primary = hex;
        }
        return new StudioPreferences(ReadIds("favorites", 10000), ReadIds("recent", 20), dark, primary);
    }

    internal JsonObject ToDocument() => new()
    { ["favorites"] = new JsonArray(Favorites.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
      ["recent"] = new JsonArray(Recent.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["dark"] = Dark, ["primaryColor"] = PrimaryColor };

    internal static void Write(string path, JsonObject document) => Write(path, document, null);
    internal static void Write(string path, JsonObject document, Action<int>? wait)
    {
        var data = Encoding.UTF8.GetBytes(FromDocument(document).ToDocument().ToJsonString(new JsonSerializerOptions { MaxDepth = 8 }));
        if (data.Length > MaximumBytes) throw new InvalidDataException("Preferences exceed the size limit.");
        path = Path.GetFullPath(path);
        void ValidateDestination()
        {
            for (string? candidate = path; candidate is not null; candidate = Path.GetDirectoryName(candidate))
            {
                FileSystemInfo item = candidate == path ? new FileInfo(candidate) : new DirectoryInfo(candidate);
                if (item.LinkTarget is not null || item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Linked preferences paths are not supported.");
            }
            if (Directory.Exists(path)) throw new InvalidDataException("Preferences destination is not a file.");
            if (File.Exists(path))
            {
                StudioFileSafety.EnsureSingleLink(path);
                try { Read(path); }
                catch (InvalidDataException error) { throw new InvalidDataException("The existing preferences file is damaged or unrecognized; preserve and repair or rename it before saving settings: " + path, error); }
            }
        }
        StudioProjectStore.RetryFileAccess(ValidateDestination, wait);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = Path.Combine(Path.GetDirectoryName(path)!, ".preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        StudioFileSafety.FileIdentity? identity = null;
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { identity = StudioFileSafety.StagingIdentity(stream.SafeFileHandle); stream.Write(data); stream.Flush(true); }
            StudioProjectStore.RetryFileAccess(() =>
            {
                StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
                ValidateDestination();
                StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
                File.Move(staged, path, overwrite: true);
            }, wait, mayRetry: () => File.Exists(staged));
        }
        finally
        {
            StudioProjectStore.CleanupStaging(staged, identity);
        }
    }
}
