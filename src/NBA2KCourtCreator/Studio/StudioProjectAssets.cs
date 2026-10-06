using System.IO;
using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

internal static class StudioProjectAssets
{
    internal static string[] SourcePaths(JsonObject snapshot, string applicationRoot)
    {
        var basis = snapshot["assetPathMode"]?.GetValue<string>() == "project-relative"
            && snapshot["_projectPath"]?.GetValue<string>() is { Length: > 0 } original
            ? Path.GetDirectoryName(Path.GetFullPath(original, applicationRoot))! : applicationRoot;
        var paths = new List<string>();
        void Add(JsonObject item)
        {
            foreach (var key in new[] { "path", "previewPath", "artworkProjectPath", "artworkDdsPath" })
                if (item[key]?.GetValue<string>() is { Length: > 0 } path) paths.Add(Path.GetFullPath(path, basis));
        }
        if (snapshot["floor"] is JsonObject floor) Add(floor);
        foreach (var key in new[] { "logoImages", "customFloorImages" })
            foreach (var item in (snapshot[key] as JsonArray ?? []).OfType<JsonObject>()) Add(item);
        if (snapshot["templatePath"]?.GetValue<string>() is { Length: > 0 } template) paths.Add(Path.GetFullPath(template, applicationRoot));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static void ValidateDestination(string path, JsonObject snapshot, string applicationRoot)
    {
        var sources = SourcePaths(snapshot, applicationRoot);
        StudioFileSafety.ProtectSources(path, sources);
        StudioFileSafety.ProtectSources(path + ".bak", sources);
    }
    internal static JsonObject Save(string path, JsonObject snapshot, string applicationRoot, Action<string, string>? preparedAsset = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        var assetDirectory = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".assets");
        var project = (JsonObject)snapshot.DeepClone();
        StudioProjectValidation.Validate(project);
        var sources = SourcePaths(project, applicationRoot);
        ValidateDestination(path, project, applicationRoot);
        StudioProjectStore.ValidateWriteDestination(path, keepBackup: true, sources);
        var sourceDirectory = project["assetPathMode"]?.GetValue<string>() == "project-relative"
            && project["_projectPath"]?.GetValue<string>() is { Length: > 0 } original
            ? Path.GetDirectoryName(Path.GetFullPath(original, applicationRoot))! : applicationRoot;
        void CheckAssetDirectory()
        {
            if (Directory.Exists(assetDirectory) && new DirectoryInfo(assetDirectory).LinkTarget is not null)
                throw new IOException("The portable asset folder is a link. Choose a project name with a regular asset folder.");
        }
        CheckAssetDirectory();
        var copied = new Dictionary<string, (string Relative, string Hash)>(StringComparer.OrdinalIgnoreCase);
        var receipts = new Dictionary<string, StudioPortableAssets.Receipt>(StringComparer.OrdinalIgnoreCase);
        string Bundle(string sourcePath, string? expectedRevision)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return sourcePath;
            sourcePath = Path.GetFullPath(sourcePath, sourceDirectory);
            if (copied.TryGetValue(sourcePath, out var bundled))
            {
                if (expectedRevision is not null && bundled.Hash != expectedRevision)
                    throw new IOException("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before saving.");
                return bundled.Relative;
            }
            if (!File.Exists(sourcePath))
            {
                if (expectedRevision is not null) throw new IOException("Artwork used in the preview is missing. Refresh the court or reimport the logo before saving.");
                return sourcePath;
            }
            CheckAssetDirectory();
            var receipt = StudioPortableAssets.Bundle(sourcePath, assetDirectory, expectedRevision, sources, preparedAsset);
            receipts[receipt.Path] = receipt;
            var relative = Path.GetRelativePath(directory, receipt.Path).Replace('\\', '/');
            copied[sourcePath] = (relative, receipt.Hash);
            return relative;
        }
        void BundlePath(JsonObject item, bool preview)
        {
            if (item["path"] is not JsonValue value || !value.TryGetValue<string>(out var source)) return;
            item["path"] = Bundle(source, item["sourceRevision"]?.GetValue<string>());
            foreach (var key in new[] { "artworkProjectPath", "artworkDdsPath" })
                if (item[key]?.GetValue<string>() is { Length: > 0 } artwork)
                    item[key] = Bundle(artwork, item[key.Replace("Path", "Revision")]?.GetValue<string>());
            if (preview && !string.IsNullOrWhiteSpace(source) && copied.ContainsKey(Path.GetFullPath(source, sourceDirectory))) item["previewPath"] = item["path"]!.DeepClone();
        }
        if (project["floor"] is JsonObject floor) BundlePath(floor, true);
        foreach (var logo in (project["logoImages"] as JsonArray ?? []).OfType<JsonObject>()) BundlePath(logo, false);
        foreach (var custom in (project["customFloorImages"] as JsonArray ?? []).OfType<JsonObject>()) BundlePath(custom, true);
        project["assetPathMode"] = "project-relative";
        project["_projectPath"] = path;
        StudioProjectStore.Write(path, project, keepBackup: true, protectedSources: sources,
            validateAssets: () => { CheckAssetDirectory(); foreach (var receipt in receipts.Values) receipt.Validate(); });
        return project;
    }
}
