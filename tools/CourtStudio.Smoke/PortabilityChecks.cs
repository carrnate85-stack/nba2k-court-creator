using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckStartupRetry(string output)
    {
        var root = ProjectRoot();
        using var oracle = new PythonServiceClient(root, null);
        var valid = await oracle.RequestAsync(["load-stock"]);
        var broken = (JsonObject)valid.DeepClone();
        var floors = broken["customFloorImages"]!.AsArray().OfType<JsonObject>().ToArray();
        var defaultFloor = floors.FirstOrDefault(item => broken["visibility"]?[item["id"]!.GetValue<string>()]?.GetValue<bool>() == true) ?? floors[0];
        var badImage = Path.GetFullPath(Path.Combine(output, "startup-invalid-floor.png"));
        await File.WriteAllTextAsync(badImage, "invalid image");
        defaultFloor["path"] = badImage; defaultFloor["previewPath"] = badImage;
        var responsePath = Path.GetFullPath(Path.Combine(output, "startup-response.json"));
        await File.WriteAllTextAsync(responsePath, broken.ToJsonString());
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(Path.Combine(root, "runtime/python/python.exe")) { WorkingDirectory = root };
            foreach (var arg in new[] { "-B", "-u", Path.Combine(root, "tests/worker_fault_fixture.py"), responsePath }) start.ArgumentList.Add(arg);
            return start;
        }
        var window = new StudioWindow(true, new PythonServiceClient(root, Start), new PythonServiceClient(root, null));
        try
        {
            await Expect<NotSupportedException>(() => window.InitializeAsync());
            var retry = (Button)window.FindName("StartupRetryButton");
            Assert(retry.Visibility == Visibility.Visible && retry.IsEnabled && !((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Startup failure did not expose retry while protecting the unloaded workspace.");
            var initialCounts = (window.Floors.Count, window.PaintLayers.Count, window.LineLayers.Count);
            Assert(initialCounts.Item1 > 200 && initialCounts.Item2 == 6 && initialCounts.Item3 == 16, "Startup fixture did not fail after partial initialization.");
            Layout(window, 1000, 680); RenderDpi(window, Path.Combine(output, "startup-retry.png"), 1000, 680, 1);
            await File.WriteAllTextAsync(responsePath, valid.ToJsonString());
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var recovering = (Task)typeof(StudioWindow).GetField("_initializationTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            await recovering;
            Assert(initialCounts == (window.Floors.Count, window.PaintLayers.Count, window.LineLayers.Count), "Startup retry duplicated the partially loaded catalog.");
            Assert(retry.Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && window.CreateProject()["floor"]?["id"] is not null, "Retry did not recover a usable court workspace.");
            Snapshot(window, Path.Combine(output, "startup-recovered.png"), 1000, 680);
        }
        finally { window.Close(); File.Delete(responsePath); File.Delete(badImage); }
        Console.WriteLine("PASS startup recovery: partial initialization failure, retry action, intact counts, enabled workspace, recovered nonblank court.");
    }

    private static void WriteSolidImage(string path, Color color)
    {
        var pixels = new byte[64 * 32 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255; }
        var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); png.Save(stream);
    }
    private static async Task CheckPortableProjects(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "portable-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var logoPath = Path.Combine(source, "logo.png"); WriteLogoExample(logoPath);
        var floorPath = Path.Combine(source, "floor.png"); WriteSolidImage(floorPath, Colors.Firebrick);
        var secondaryPath = Path.Combine(source, "secondary.png"); WriteSolidImage(secondaryPath, Colors.SteelBlue);
        var sources = new[] { logoPath, floorPath, secondaryPath };
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var project = window.CreateProject();
            project["assetPathMode"] = "project-relative";
            project["_projectPath"] = Path.Combine(source, "original.court.json");
            project["floor"]!["path"] = floorPath; project["floor"]!["name"] = "Portable hardwood";
            project["customFloorImages"] = new JsonArray(new JsonObject { ["id"] = "portable-secondary", ["name"] = "Portable secondary", ["path"] = secondaryPath });
            project["logoImages"] = new JsonArray(new JsonObject { ["id"] = "portable-a", ["name"] = "Portable A", ["path"] = logoPath, ["x"] = 2000, ["y"] = 1500, ["width"] = 500, ["height"] = 500 },
                new JsonObject { ["id"] = "portable-b", ["name"] = "Portable B", ["path"] = logoPath, ["x"] = 4000, ["y"] = 1500, ["width"] = 500, ["height"] = 500 });
            await window.RestoreProjectAsync(project);
            Assert(window.CreateProject()["floor"]!["path"]!.GetValue<string>() == floorPath && window.Floors.Any(item => item.Id == "portable-secondary"), "Bundled floor override or non-selected custom catalog entry was lost.");
            var savedPath = Path.Combine(root, "saved", "portable.court.json");
            var saving = window.SaveProjectToAsync(savedPath);
            Assert(!((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Portable save did not protect its document snapshot.");
            await saving;
            var saved = StudioProjectStore.Read(savedPath);
            var savedDirectory = Path.GetDirectoryName(savedPath)!;
            var storedLogo = saved["logoImages"]![0]!["path"]!.GetValue<string>();
            Assert(!Path.IsPathRooted(storedLogo) && storedLogo == saved["logoImages"]![1]!["path"]!.GetValue<string>(), "Portable logos were not relative or identical assets were duplicated.");
            var assetDirectory = Path.Combine(savedDirectory, "portable.court.assets");
            var count = Directory.GetFiles(assetDirectory).Length;
            var logoAsset = Path.GetFullPath(storedLogo, savedDirectory);
            await File.WriteAllTextAsync(logoAsset, "corrupt cached asset");
            await window.SaveProjectToAsync(savedPath);
            using (var original = File.OpenRead(logoPath))
            using (var repaired = File.OpenRead(logoAsset))
                Assert(SHA256.HashData(original).SequenceEqual(SHA256.HashData(repaired)), "Resaving did not repair a corrupted content-addressed asset.");
            Assert(Directory.GetFiles(assetDirectory).Length == count && File.Exists(savedPath + ".bak"), "Repeated save grew identical assets or omitted its previous-project backup.");
            var moved = Path.Combine(root, "other-pc"); Directory.CreateDirectory(moved);
            var movedPath = Path.Combine(moved, "portable.court.json"); File.Copy(savedPath, movedPath);
            var movedAssets = Path.Combine(moved, "portable.court.assets"); Directory.CreateDirectory(movedAssets);
            foreach (var asset in Directory.GetFiles(assetDirectory)) File.Copy(asset, Path.Combine(movedAssets, Path.GetFileName(asset)));
            foreach (var original in sources) File.Move(original, original + ".offline");
            await window.OpenProjectFromAsync(movedPath);
            Assert(window.Canvas.Layers.Count == 2 && window.Canvas.Layers.All(layer => layer.Image is not null && layer.Path.StartsWith(movedAssets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Moved project depended on original logo files or the old project directory.");
            Assert(window.CreateProject()["floor"]!["path"]!.GetValue<string>().StartsWith(movedAssets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Moved project silently used the local stock floor instead of its bundled hardwood.");
            var secondary = window.Floors.Single(item => item.Id == "portable-secondary");
            Assert(secondary.Path.StartsWith(movedAssets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(secondary.Path), "Non-selected custom hardwood was not portable.");
            var exported = Path.Combine(moved, "portable-export.png"); await window.ExportToAsync(exported, false);
            var geometry = (JsonObject)typeof(StudioWindow).GetField("_geometry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var bounds = geometry["gameUv"]!["hardwoodBounds"]!.AsArray();
            var image = StudioImages.Load(exported, 512); var pixel = new byte[4];
            var x = (int)((bounds[0]!.GetValue<double>() + bounds[2]!.GetValue<double>() * .62) / 8192 * image.PixelWidth);
            var y = (int)((bounds[1]!.GetValue<double>() + bounds[3]!.GetValue<double>() * .2) / 4096 * image.PixelHeight);
            image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            Assert(pixel[2] > 150 && pixel[1] < 70 && pixel[0] < 70, "Python export did not use the moved project's bundled hardwood pixels.");
            File.Delete(exported);
            await window.SelectFloorAsync(secondary);
            var resavedPath = Path.Combine(moved, "resaved.court.json"); await window.SaveProjectToAsync(resavedPath);
            var openBeforeFailure = window.CreateProject().ToJsonString(); var diskBeforeFailure = File.ReadAllText(resavedPath);
            using (var locked = new FileStream(window.Canvas.Layers[0].Path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Expect<IOException>(() => window.SaveProjectToAsync(resavedPath));
            Assert(File.ReadAllText(resavedPath) == diskBeforeFailure && window.CreateProject().ToJsonString() == openBeforeFailure && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Failed asset save damaged the saved/open court or left controls disabled.");
            var backup = StudioProjectStore.Read(savedPath + ".bak");
            backup["_projectPath"] = savedPath + ".bak"; await window.RestoreProjectAsync(backup);
            Assert(window.Canvas.Layers.All(layer => layer.Image is not null), "Prior project backup lost its immutable assets.");
        }
        finally
        {
            foreach (var original in sources) if (File.Exists(original + ".offline")) File.Move(original + ".offline", original);
            window.Close();
        }
        Console.WriteLine("PASS portable projects: relative hashed assets, deduplicated logos, corruption repair, custom catalog restoration, moved folder with unavailable originals, real full-resolution Python export, Save As, intact backups, atomic failed save.");
    }
}
