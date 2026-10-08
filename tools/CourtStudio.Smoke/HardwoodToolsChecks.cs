using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TextureStudio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckHardwoodTools(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var sourceRoot = Environment.CurrentDirectory;
        using var locator = new PythonServiceClient(); var installedRoot = locator.ProjectRoot;
        var fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        foreach (var folder in new[] { "court_creator", "tools" })
            foreach (var source in Directory.EnumerateFiles(Path.Combine(sourceRoot, folder), "*.py", SearchOption.TopDirectoryOnly))
            { var destination = Path.Combine(fixture, folder, Path.GetFileName(source)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
        File.Copy(Path.Combine(installedRoot, "tools/texconv.exe"), Path.Combine(fixture, "tools/texconv.exe"));
        Directory.CreateDirectory(Path.Combine(fixture, "data/generated"));
        File.Copy(Path.Combine(installedRoot, "data/generated/experimental-stock-lines.json"), Path.Combine(fixture, "data/generated/experimental-stock-lines.json"));
        File.Copy(Path.Combine(installedRoot, "data/team_palettes.json"), Path.Combine(fixture, "data/team_palettes.json"));
        var catalogName = "assets/court_floor_templates/nba2k27/nba2k27_floor_templates.json";
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(installedRoot, catalogName)))!.AsObject();
        foreach (var item in catalog["templates"]!.AsArray().OfType<JsonObject>())
            foreach (var field in new[] { "path", "thumbnailPath" }) item[field] = Path.Combine(installedRoot, "assets", item[field]!.GetValue<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture, catalogName))!); File.WriteAllText(Path.Combine(fixture, catalogName), catalog.ToJsonString());
        ProcessStartInfo Worker()
        {
            var start = new ProcessStartInfo(Path.Combine(installedRoot, "runtime/python/python.exe")) { WorkingDirectory = fixture, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-u", "-m", "court_creator.service" }) start.ArgumentList.Add(argument);
            return start;
        }
        var geometry = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, "data/generated/experimental-stock-lines.json")))!.AsObject();
        Point Interior(string id)
        {
            var polygons = geometry["paints"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id)["gameUvPolygons"]!.AsArray().OfType<JsonArray>();
            double Area(JsonArray polygon) => Math.Abs(polygon.Select((p, i) => p![0]!.GetValue<double>() * polygon[(i + 1) % polygon.Count]![1]!.GetValue<double>() - polygon[(i + 1) % polygon.Count]![0]!.GetValue<double>() * p[1]!.GetValue<double>()).Sum());
            var largest = polygons.MaxBy(Area)!;
            return new(largest.Average(p => p![0]!.GetValue<double>()), largest.Average(p => p![1]!.GetValue<double>()));
        }
        var left = Interior("two-point-left"); var right = Interior("two-point-right"); var key = Interior("paint-left");
        var centerAnchor = geometry["guides"]!["game-uv"]!["anchors"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == "court-center");
        var center = new Point(centerAnchor["x"]!.GetValue<double>(), centerAnchor["y"]!.GetValue<double>());
        var mainPath = Path.Combine(fixture, "main.bmp"); var secondaryPath = Path.Combine(fixture, "second.bmp");
        WriteRevisionBitmap(mainPath, Colors.Red); WriteRevisionBitmap(secondaryPath, Colors.Blue);
        StockFloor Floor(string id, string path) => StockFloor.Read(new JsonObject { ["id"] = id, ["name"] = id, ["category"] = "Custom", ["path"] = path }, fixture);
        var main = Floor("Main test hardwood", mainPath); var second = Floor("Two-point test hardwood", secondaryPath);
        var originals = (AssetHash(mainPath), AssetHash(secondaryPath));
        var window = new StudioWindow(true, new PythonServiceClient(fixture, Worker), new PythonServiceClient(fixture, Worker));
        try
        {
            await window.InitializeAsync(); await window.SelectFloorAsync(main);
            foreach (var layer in window.PaintLayers.Concat(window.LineLayers)) window.SetLayerSettings(layer.Id, visible: false);
            window.SetLayerSettings("paint-left", visible: true, color: "#008000");
            await window.SelectTwoPointFloorAsync(second);
            Color NativePixel(Point point)
            {
                var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) { drawing.PushTransform(new ScaleTransform(.25, .25)); drawing.DrawDrawing(window.Canvas.BackgroundDrawing); }
                var image = new RenderTargetBitmap(2048, 1024, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var pixels = new byte[4]; image.CopyPixels(new Int32Rect((int)(point.X / 4), (int)(point.Y / 4), 1, 1), pixels, 4, 0);
                return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
            }
            Assert(NativePixel(left) == Colors.Blue && NativePixel(right) == Colors.Blue, "Second hardwood is not clipped to both two-point areas.");
            Assert(NativePixel(center) == Colors.Red && NativePixel(key) == Colors.Green, "Second hardwood changed the center or key.");
            var mainDefault = window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString();
            await window.SetHardwoodTextureAsync(new(20, 10, -30, 150, 45), true);
            Assert(window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString() == mainDefault, "Secondary adjustments changed the main hardwood.");
            Assert(NativePixel(key) == Colors.Green, "Secondary adjustments escaped the clipping mask.");
            var adjustedSecond = window.CreateProject()["twoPointFloor"]!.ToJsonString();
            await window.SetHardwoodTextureAsync(new(-20, -10, 20, 80, -30));
            Assert(window.CreateProject()["twoPointFloor"]!.ToJsonString() == adjustedSecond, "Main adjustments changed the secondary hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString() == mainDefault, "Adjustment undo failed.");
            await window.UndoAsync(true); Assert(window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Adjustment redo failed.");
            var savedPath = Path.Combine(output, "two-hardwoods.court.json"); await window.SaveProjectToAsync(savedPath);
            var saved = StudioProjectStore.Read(savedPath);
            Assert(!Path.IsPathRooted(saved["floor"]!["path"]!.GetValue<string>()) && !Path.IsPathRooted(saved["twoPointFloor"]!["path"]!.GetValue<string>()), "Portable save did not bundle both hardwoods.");
            await window.OpenProjectFromAsync(savedPath);
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["rotation"]!.GetValue<int>() == 45, "Reopening lost secondary settings.");
            var reopened = window.CreateProject()["twoPointFloor"]!.ToJsonString();
            await window.SelectTwoPointFloorAsync(null); Assert(window.CreateProject()["twoPointFloor"] is null, "Use main did not clear secondary hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["twoPointFloor"]!.ToJsonString() == reopened, "Undo did not restore the bundled secondary hardwood.");
            await window.NewProjectAsync(); Assert(window.CreateProject()["twoPointFloor"] is null, "New project retained the secondary hardwood."); await window.UndoAsync();
            Layout(window, 1200, 800); window.ShowHardwoodTools(true);
            var content = (FrameworkElement)window.Content; content.Measure(new Size(1200, 800)); content.Arrange(new Rect(0, 0, 1200, 800)); content.UpdateLayout();
            var bar = (ContextualToolOptionsBar)window.FindName("HardwoodOptions");
            Assert(bar.Visibility == Visibility.Visible && bar.Height == 40 && bar.HasOverflow, "The real shared contextual bar/overflow is missing.");
            var brightness = Descendants<Slider>(bar).Single(slider => Equals(slider.Tag, "brightness"));
            brightness.Value = 15; await window.FlushHardwoodPreviewAsync();
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 15, "Slider did not edit the selected hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 20, "Slider undo did not restore the previous settings.");
            window.ShowHardwoodTools(true); content.UpdateLayout();
            var screenshot = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); screenshot.Render((Visual)window.Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "hardwood-controls.png"))) encoder.Save(stream);
            window.SelectCanvasTool(ArtworkTool.Hand); Assert(bar.Visibility == Visibility.Collapsed, "Hardwood bar remained active after choosing another tool.");
            await window.SetHardwoodTextureAsync(new()); await window.SetHardwoodTextureAsync(new(), true);
            await window.ExportToAsync(Path.Combine(output, "two-point-export.png"), false);
            await window.ExportToAsync(Path.Combine(output, "two-point-export.iff"), true);
            Assert(originals == (AssetHash(mainPath), AssetHash(secondaryPath)), "Hardwood controls modified an original source.");
            File.WriteAllText(Path.Combine(output, "samples.json"), new JsonObject { ["left"] = new JsonArray(left.X, left.Y), ["right"] = new JsonArray(right.X, right.Y), ["key"] = new JsonArray(key.X, key.Y), ["center"] = new JsonArray(center.X, center.Y), ["fixture"] = fixture }.ToJsonString());
            Console.WriteLine("PASS hardwood tools: both region masks, unchanged keys/center, independent sliders, shared Canvas 40px bar/overflow, reset, undo/redo, portable save/reopen, Use main/New undo, full PNG/IFF exports, original files unchanged. No native windows opened.");
        }
        finally { window.Close(); }
    }
}
