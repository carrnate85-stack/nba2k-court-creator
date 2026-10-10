using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckPerformance(string output)
    {
        Directory.CreateDirectory(output);
        var pixels = new byte[1024 * 512 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = (byte)(i / 4 % 251); pixels[i + 1] = 120; pixels[i + 2] = 190; pixels[i + 3] = 255; }
        var source = BitmapSource.Create(1024, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 4096); source.Freeze();
        var region = new RectangleGeometry(new Rect(0, 0, 8192, 4096)); region.Freeze();
        var settings = new HardwoodTextureSettings(12, 7, 18);
        var timer = Stopwatch.StartNew();
        var first = (GeometryDrawing)HardwoodTextureSettings.CreateDrawing(source, region.Rect, region, settings);
        var firstColorMs = timer.Elapsed.TotalMilliseconds;
        var image = (BitmapSource)((ImageBrush)first.Brush).ImageSource;
        var passes = HardwoodTextureSettings.Statistics.ColorPasses;
        var samples = new List<double>();
        for (var i = 1; i <= 20; ++i)
        {
            timer.Restart();
            var drawing = (GeometryDrawing)HardwoodTextureSettings.CreateDrawing(source, region.Rect, region, settings with { Rotation = i, Scale = 100 + i });
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert(drawing.IsFrozen && ReferenceEquals(((ImageBrush)drawing.Brush).ImageSource, image), "Transform-only adjustment did not reuse the exact color pixels.");
        }
        Assert(HardwoodTextureSettings.Statistics.ColorPasses == passes, "Rotation/scale ran another color pass.");
        using (var bgra = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, 1024, 512))
        using (var rgba = bgra.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgba32>())
        using (var expected = TextureStudio.Services.ColorAdjustmentService.Apply(rgba, 12, 7, 18, 0))
        using (var expectedBgra = expected.CloneAs<SixLabors.ImageSharp.PixelFormats.Bgra32>())
        {
            var expectedPixels = new byte[pixels.Length]; expectedBgra.CopyPixelDataTo(expectedPixels);
            var actualPixels = new byte[pixels.Length]; image.CopyPixels(actualPixels, 4096, 0);
            Assert(expectedPixels.SequenceEqual(actualPixels), "Cached hardwood adjustment changed the shared color math.");
        }
        // Equal dimensions/settings do not alias different frozen sources.
        pixels[0] = 255;
        var replacement = BitmapSource.Create(1024, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 4096); replacement.Freeze();
        var different = (GeometryDrawing)HardwoodTextureSettings.CreateDrawing(replacement, region.Rect, region, settings);
        Assert(!ReferenceEquals(((ImageBrush)different.Brush).ImageSource, image), "A changed source reused stale adjusted pixels.");
        for (var i = 0; i < 10; ++i) HardwoodTextureSettings.CreateDrawing(source, region.Rect, region, new(i + 1));
        var cache = HardwoodTextureSettings.AdjustmentCacheStatistics;
        Assert(cache.Count <= 8 && cache.Bytes <= cache.Budget, "Hardwood adjustment cache exceeded its bounds.");

        var window = new StudioWindow(true);
        using var release = new ManualResetEventSlim();
        try
        {
            await window.InitializeAsync(); Layout(window, 1440, 900);
            await window.SelectTwoPointFloorAsync(window.Floors.First(f => f.Id != window.CreateProject()["floor"]!["id"]!.GetValue<string>()));
            await window.SetHardwoodTextureAsync(new(Brightness: 18), true);
            window.ShowHardwoodTools();
            var brightness = Descendants<Slider>((Grid)window.FindName("HardwoodOptions")).Single(slider => AutomationProperties.GetName(slider) == "Hardwood brightness");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0; var active = 0; var peak = 0; var canceled = 0;
            window.RenderHardwoodPreview = (bitmap, rect, geometry, adjustments, token) =>
            {
                var concurrent = Interlocked.Increment(ref active); peak = Math.Max(peak, concurrent);
                try
                {
                    if (Interlocked.Increment(ref requests) == 1)
                    { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held preview did not release."); }
                    return HardwoodTextureSettings.CreateDrawing(bitmap, rect, geometry, adjustments, token);
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
                finally { Interlocked.Decrement(ref active); }
            };
            brightness.Value = 9; var pending = window.FlushHardwoodPreviewAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 10; i <= 27; ++i)
            {
                brightness.Value = i;
                Assert(ReferenceEquals(pending, window.FlushHardwoodPreviewAsync()), "Slider updates started another preview worker.");
            }
            release.Set(); await pending;
            Assert(requests == 2 && peak == 1 && canceled == 1, "Preview requests were not coalesced to one active and one latest job.");
            Assert(window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 27, "Latest slider value was lost.");
            var requestsAfter = requests; await window.FlushHardwoodPreviewAsync();
            Assert(requests == requestsAfter, "An unchanged preview rebuilt the hardwoods.");
            window.ShowHardwoodTools(true); brightness.Value = 23; await window.FlushHardwoodPreviewAsync();
            Assert(requests == requestsAfter + 1, "Changing the second hardwood also rebuilt the main hardwood.");
            await window.UndoAsync();
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 18, "Secondary hardwood undo changed its target.");
            await window.UndoAsync(true);
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 23, "Secondary hardwood redo lost its settings.");
            brightness.Value = 37;
            await window.UndoAsync();
            Assert(brightness.Value == 23, "Undo before the preview timer left the numeric controls at the canceled value.");
            await window.FlushHardwoodPreviewAsync();
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 23, "A canceled timer republished an undone adjustment.");

            var logoPath = Path.Combine(Path.GetFullPath(output), "performance-logo.bmp"); WriteRevisionBitmap(logoPath, Colors.Gold);
            await window.AddLogoAsync(logoPath); var logo = window.Canvas.SelectedLayer!;
            logo.X += 200;
            var before = window.CreateProject(); var background = window.Canvas.BackgroundDrawing;
            var fastBefore = window.FastHistoryRestores;
            window.CenterSelectedLogo(); var centered = window.CreateProject(); background = window.Canvas.BackgroundDrawing;
            await window.UndoAsync();
            Assert(JsonNode.DeepEquals(window.CreateProject(), before) && ReferenceEquals(logo, window.Canvas.SelectedLayer), "Placement undo replaced or changed the selected logo.");
            Assert(ReferenceEquals(background, window.Canvas.BackgroundDrawing), "Placement undo rebuilt the court background.");
            await window.UndoAsync(true);
            Assert(JsonNode.DeepEquals(window.CreateProject(), centered) && window.FastHistoryRestores == fastBefore + 2, "Placement redo missed the targeted path.");
            window.DeleteSelectedLogo(); var fastDelete = window.FastHistoryRestores;
            await window.UndoAsync();
            Assert(window.Canvas.Layers.Count == 1 && window.FastHistoryRestores == fastDelete, "Asset restoration bypassed the full restore safeguards.");

            File.WriteAllText(Path.Combine(output, "performance-audit.json"), JsonSerializer.Serialize(new
            {
                firstColorMs, cachedTransformMedianMs = samples.Order().ElementAt(samples.Count / 2),
                transformColorPasses = 0, latestPreviewRequests = 2, peakPreviewWorkers = peak,
                canceledPreviewRequests = canceled, cacheEntries = cache.Count, cacheBytes = cache.Bytes,
                cacheBudgetBytes = cache.Budget, nativeWindowsOpened = false,
                measurementScope = "Off-screen 1024x512 color/transform CPU work; not visible frame or application startup timing."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS performance: exact cached color pixels; transform median {samples.Order().ElementAt(samples.Count / 2):F3} ms; bounded cache; one active/latest preview; independent hardwoods; targeted placement history; asset fallback. No native windows opened.");
        }
        finally { release.Set(); window.Close(); }
    }
}
