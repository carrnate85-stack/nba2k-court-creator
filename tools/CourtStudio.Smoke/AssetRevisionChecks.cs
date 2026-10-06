using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static void WriteRevisionBitmap(string path, Color color)
    {
        var pixels = new byte[64 * 32 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255; }
        var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new BmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static string AssetHash(string path)
    { using var source = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(); }
    private static async Task CheckAssetRevisions(string output)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "asset-revision-fixture"));
        Directory.CreateDirectory(directory);
        var floor = Path.Combine(directory, "floor.bmp"); var logo = Path.Combine(directory, "logo.bmp");
        WriteRevisionBitmap(floor, Colors.Firebrick); WriteRevisionBitmap(logo, Colors.Gold);
        var oldBitmap = StudioImages.Load(floor, 64); var oldHash = StudioImages.SourceRevision(oldBitmap);
        Assert(oldHash == AssetHash(floor), "Decoded bitmap was not tied to its encoded source bytes.");
        Assert(ReferenceEquals(oldBitmap, StudioImages.Load(floor, 64)), "Unchanged image missed the content cache.");
        var oversizedPreview = StudioImages.Load(floor, 512);
        Assert(oversizedPreview.PixelWidth == 64 && oversizedPreview.PixelHeight == 32, "Preview decoding unnecessarily upscaled a small image beyond its original pixel budget.");
        var stamp = File.GetLastWriteTimeUtc(floor); var size = new FileInfo(floor).Length;
        WriteRevisionBitmap(floor, Colors.SteelBlue); File.SetLastWriteTimeUtc(floor, stamp);
        Assert(new FileInfo(floor).Length == size, "Replacement fixture changed size.");
        var fresh = StudioImages.Load(floor, 64);
        Assert(!ReferenceEquals(oldBitmap, fresh) && StudioImages.SourceRevision(fresh) == AssetHash(floor)
               && StudioImages.SourceRevision(oldBitmap) == oldHash, "Same-size/timestamp replacement reused stale pixels or revised a retained bitmap.");
        var identical = Path.Combine(directory, "identical.bmp"); File.Copy(floor, identical, true);
        Assert(ReferenceEquals(fresh, StudioImages.Load(identical, 64)), "Identical artwork in a different file decoded duplicate pixels.");
        var projectPath = Path.Combine(directory, "protected.court.json");
        var outputPath = Path.Combine(directory, "protected-export");
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var project = window.CreateProject();
            project["floor"] = new JsonObject { ["id"] = "revision-floor", ["name"] = "Revision fixture", ["path"] = floor };
            project["logoImages"] = new JsonArray(new JsonObject { ["id"] = "revision-logo", ["name"] = "Revision logo", ["path"] = logo,
                ["x"] = 3000, ["y"] = 1500, ["width"] = 200, ["height"] = 200 });
            await window.RestoreProjectAsync(project);
            await window.SaveProjectToAsync(projectPath);
            var savedBytes = File.ReadAllBytes(projectPath);
            var snapshot = window.CreateProject();
            Assert(snapshot["floor"]!["sourceRevision"]!.GetValue<string>() == AssetHash(floor)
                   && snapshot["logoImages"]![0]!["sourceRevision"]!.GetValue<string>() == AssetHash(logo), "Native project omitted preview revisions.");
            foreach (var asset in new[] { floor, logo })
            {
                var before = window.CreateProject().ToJsonString(); var retainedLogo = window.Canvas.Layers[0].Image;
                stamp = File.GetLastWriteTimeUtc(asset); size = new FileInfo(asset).Length;
                WriteRevisionBitmap(asset, Colors.LimeGreen); File.SetLastWriteTimeUtc(asset, stamp);
                Assert(new FileInfo(asset).Length == size, "Export replacement fixture changed size.");
                await File.WriteAllTextAsync(outputPath, "previous valid export");
                foreach (var iff in new[] { false, true })
                {
                    try { await window.ExportToAsync(outputPath, iff); throw new Exception("Changed preview asset was exported."); }
                    catch (InvalidOperationException error) { Assert(error.Message.Contains("Artwork changed after its preview", StringComparison.Ordinal), "Export failed for a reason other than source revision mismatch."); }
                    Assert(File.ReadAllText(outputPath) == "previous valid export", "Stale artwork replaced an existing export.");
                    Assert(window.CreateProject().ToJsonString() == before && ReferenceEquals(window.Canvas.Layers[0].Image, retainedLogo), "Rejected export changed the open project or retained preview.");
                    Assert(((Button)window.FindName("BuildIffButton")).IsEnabled, "Revision rejection left export controls disabled.");
                }
                await Expect<IOException>(() => window.SaveProjectToAsync(projectPath));
                Assert(File.ReadAllBytes(projectPath).SequenceEqual(savedBytes) && window.CreateProject().ToJsonString() == before
                       && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Stale artwork changed a saved/open project or left controls disabled.");
                if (asset == floor)
                {
                    await window.SelectFloorAsync(window.Floors.Single(item => item.Id == "revision-floor"));
                    Assert(window.CreateProject()["floor"]!["sourceRevision"]!.GetValue<string>() == AssetHash(floor), "Hardwood refresh did not establish a new source revision.");
                }
                else
                {
                    await window.RestoreProjectAsync(window.CreateProject());
                    Assert(((TextBlock)window.FindName("StatusText")).Text.Contains("Updated artwork loaded", StringComparison.Ordinal), "Explicit reload silently accepted changed artwork without a notice.");
                }
            }
            Assert(window.CreateProject()["logoImages"]![0]!["sourceRevision"]!.GetValue<string>() == AssetHash(logo), "Reload did not refresh the logo's source revision.");
            await window.ExportToAsync(outputPath, false);
            Assert(new FileInfo(outputPath).Length > 1000, "Fresh artwork could not export after stale-source rejection.");
            await window.SaveProjectToAsync(projectPath);
            var saved = StudioProjectStore.Read(projectPath);
            Assert(saved["floor"]!["sourceRevision"]!.GetValue<string>() == AssetHash(floor)
                   && saved["logoImages"]![0]!["sourceRevision"]!.GetValue<string>() == AssetHash(logo), "Portable saving did not retain verified revisions.");
            var invalid = window.CreateProject(); invalid["floor"]!["sourceRevision"] = "not a hash";
            await Expect<InvalidDataException>(() => window.RestoreProjectAsync(invalid));
            Console.WriteLine("PASS artwork revisions: full-byte content cache with identical size/timestamp; retained bitmap fingerprints; native floor/logo pins; real PNG/IFF stale-source rejection preserves output/document/preview and restores controls; portable saves reject mismatches; refresh/reload/export/save recover.");
        }
        finally { window.Close(); }
    }
}
