using System.IO;
using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

public static class StudioProjectValidation
{
    public static void Validate(JsonObject project)
    {
        var version = Number(project["version"], "version", required: true);
        if (!(version == 2 && Text(project["buildMode"], "buildMode") == "game-uv")
            && !(version == 1 && Text(project["templatePath"], "templatePath") is { Length: > 0 }))
            throw Invalid("version", "is not a supported Court Creator project");
        foreach (var key in new[] { "_projectPath", "projectName", "templatePath", "mappingMode" }) Text(project[key], key);
        if (Text(project["assetPathMode"], "assetPathMode") is { } pathMode && pathMode != "project-relative")
            throw Invalid("assetPathMode", "is not supported");
        Color(project["outsideColor"], "outsideColor"); Boolean(project["outsideVisible"], "outsideVisible");
        Boolean(project["twoPointHardwoodEnabled"], "twoPointHardwoodEnabled");
        foreach (var floorKey in new[] { "floor", "twoPointFloor" })
        if (project[floorKey] is not null)
        {
            var floor = Object(project[floorKey], floorKey);
            foreach (var key in new[] { "id", "name", "path", "previewPath", "category" }) Text(floor[key], floorKey + "." + key);
            Revision(floor["sourceRevision"], floorKey + ".sourceRevision");
            Artwork(floor);
            HardwoodTextureSettings.Read(floor);
        }
        foreach (var key in new[] { "paintSettings", "lineSettings" })
            foreach (var pair in Map(project[key], key))
            {
                var settings = Object(pair.Value, key + "." + pair.Key);
                Boolean(settings["visible"], key + "." + pair.Key + ".visible");
                Color(settings["color"], key + "." + pair.Key + ".color");
            }
        foreach (var pair in Map(project["visibility"], "visibility")) Boolean(pair.Value, "visibility." + pair.Key);
        foreach (var pair in Map(project["layerNames"], "layerNames")) Text(pair.Value, "layerNames." + pair.Key);
        foreach (var pair in Map(project["colorOverrides"], "colorOverrides"))
        {
            if (pair.Value is not JsonArray rgb || rgb.Count is not (3 or 4)) throw Invalid("colorOverrides." + pair.Key, "must contain three or four color channels");
            foreach (var channel in rgb)
            {
                var number = Number(channel, "colorOverrides." + pair.Key, required: true);
                if (number < 0 || number > 255 || number != Math.Truncate(number)) throw Invalid("colorOverrides." + pair.Key, "contains an invalid color channel");
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var logos = Array(project["logoImages"], "logoImages");
        if (logos.Count > 256) throw Invalid("logoImages", "contains too many layers");
        foreach (var node in logos)
        {
            var logo = Object(node, "logoImages layer");
            var id = Text(logo["id"], "logo.id");
            if (id is not null && !ids.Add(id)) throw Invalid("logo.id", "must be unique");
            Text(logo["name"], "logo.name"); Text(logo["path"], "logo.path");
            Revision(logo["sourceRevision"], "logo.sourceRevision");
            Artwork(logo);
            foreach (var key in new[] { "visible", "scaleLocked", "flipX", "flipY" }) Boolean(logo[key], "logo." + key);
            foreach (var key in new[] { "x", "y", "rotation" })
                if (logo[key] is not null && Math.Abs(Number(logo[key], "logo." + key)) > 1_000_000) throw Invalid("logo." + key, "is outside the supported range");
            foreach (var key in new[] { "width", "height" })
                if (logo[key] is not null && Number(logo[key], "logo." + key) is var size && (size < 1 || size > 32768)) throw Invalid("logo." + key, "must be between 1 and 32768");
            if (logo["opacity"] is not null && Number(logo["opacity"], "logo.opacity") is var opacity && (opacity < 0 || opacity > 100)) throw Invalid("logo.opacity", "must be between 0 and 100");
        }
        ids.Clear();
        var customFloors = Array(project["customFloorImages"], "customFloorImages");
        if (customFloors.Count > 1024) throw Invalid("customFloorImages", "contains too many floors");
        foreach (var node in customFloors)
        {
            var floor = Object(node, "customFloorImages layer");
            foreach (var key in new[] { "id", "name", "path", "previewPath", "category" }) Text(floor[key], "customFloor." + key);
            if (Text(floor["id"], "customFloor.id") is { } id && !ids.Add(id)) throw Invalid("customFloor.id", "must be unique");
            Boolean(floor["visible"], "customFloor.visible");
            Artwork(floor);
        }
    }
    private static JsonObject Object(JsonNode? node, string key) => node as JsonObject ?? throw Invalid(key, "must be an object");
    private static void Artwork(JsonObject item)
    {
        if (Text(item["artworkAlphaMode"], "artworkAlphaMode") is { } mode && mode is not ("GameData" or "Transparency"))
            throw Invalid("artworkAlphaMode", "is not supported");
        foreach (var key in new[] { "artworkProject", "artworkDds" })
        {
            var path = Text(item[key + "Path"], key + "Path");
            Revision(item[key + "Revision"], key + "Revision");
            if (path is not null && (path.Length == 0 || item[key + "Revision"] is null))
                throw Invalid(key, "must have a path and revision");
        }
    }
    private static JsonObject Map(JsonNode? node, string key) => node is null ? new() : Object(node, key);
    private static JsonArray Array(JsonNode? node, string key) => node is null ? new() : node as JsonArray ?? throw Invalid(key, "must be an array");
    private static string? Text(JsonNode? node, string key)
    {
        if (node is null) return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > 32768 || text.Contains('\0')) throw Invalid(key, "must be valid text");
        return text;
    }
    private static void Boolean(JsonNode? node, string key)
    { if (node is not null && (node is not JsonValue value || !value.TryGetValue<bool>(out _))) throw Invalid(key, "must be true or false"); }
    private static void Color(JsonNode? node, string key)
    { if (node is not null && StudioImages.Hex(Text(node, key)) is null) throw Invalid(key, "must be a valid hex color"); }
    private static void Revision(JsonNode? node, string key)
    {
        if (node is null) return;
        var value = Text(node, key)!;
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw Invalid(key, "must be a SHA-256 revision");
    }
    private static double Number(JsonNode? node, string key, bool required = false)
    {
        if (node is null && !required) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var largeInteger)) return largeInteger;
            if (value.TryGetValue<decimal>(out var decimalNumber)) return (double)decimalNumber;
        }
        throw Invalid(key, "must be a finite number");
    }
    private static InvalidDataException Invalid(string key, string message) => new("Invalid court project: " + key + " " + message + ".");
}
