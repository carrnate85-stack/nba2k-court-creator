using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TextureStudio.Models;
using TextureStudio.Services;
using StudioTheme = TwoK.Studio.StudioTheme;
using CourtToolIcon = TwoK.Studio.ToolIcon;
using CourtToolMode = TwoK.Studio.ToolMode;

internal static partial class Program
{
    private static async Task CheckSharedControls(string output)
    {
        CheckSharedChrome(output);
        await CheckSharedLogoDecoding(output);
        CheckSharedPreviewAndMemory(output);
        Console.WriteLine("PASS shared controls: central theme/token aliases and actual Canvas tool icons; shared RGBA/RGB preview parity; guarded raster/DDS/profile/orientation imports and normalized placement; undo/redo, portable save/reopen and real export; low-memory decode/cleanup/preview/reset rejection preserves source, draft and history. No native windows opened.");
    }

    private static byte[] BitmapPixels(BitmapSource bitmap)
    {
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }

    private static void CheckSharedChrome(string output)
    {
        var previous = StudioTheme.IsDark;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                StudioTheme.Apply(dark); var expected = new ResourceDictionary(); TextureStudio.StudioTheme.Apply(expected, dark);
                foreach (var key in expected.Keys.Cast<object>())
                {
                    if (expected[key] is SolidColorBrush brush)
                        Assert(Application.Current.Resources[key] is SolidColorBrush actual && actual.Color == brush.Color && actual.IsFrozen,
                            "Host theme differs from released Canvas token: " + key);
                }
                foreach (var alias in new[] { ("TopChromeBrush", "HeaderBrush"), ("DocumentChromeBrush", "DocumentBarBrush"), ("ChromeDividerBrush", "HeaderDividerBrush") })
                    Assert(ReferenceEquals(Application.Current.Resources[alias.Item1], Application.Current.Resources[alias.Item2]), "Court chrome alias duplicated the central palette.");
                Assert(StudioTheme.IsDark == TextureStudio.StudioTheme.IsDark, "Court and central theme states diverged.");
                var strip = new WrapPanel { Margin = new Thickness(12) };
                foreach (var mode in Enum.GetValues<CourtToolMode>())
                {
                    var adapter = new CourtToolIcon { Mode = mode, Margin = new Thickness(8) };
                    Assert(adapter.Child is TextureStudio.ToolIcon icon && icon.Mode.ToString() == mode.ToString()
                        && icon.GetType().Assembly.GetName().Name == "Canvas.Wpf", "Toolbar did not instantiate the released Canvas icon: " + mode);
                    var changed = mode == CourtToolMode.Hand ? CourtToolMode.Move : CourtToolMode.Hand;
                    adapter.Mode = changed; Assert(((TextureStudio.ToolIcon)adapter.Child).Mode.ToString() == changed.ToString(), "Icon mode update did not reach Canvas.");
                    adapter.Mode = mode; strip.Children.Add(adapter);
                }
                var sample = new Window { Content = strip, Background = (Brush)Application.Current.Resources["WindowBrush"] };
                try { RenderDpi(sample, Path.Combine(output, $"shared-icons-{(dark ? "dark" : "light")}.png"), 480, 150, 1); Assert(!sample.IsVisible, "Icon check opened a window."); }
                finally { sample.Close(); }
            }
        }
        finally { StudioTheme.Apply(previous); }
    }

    private static async Task CheckSharedLogoDecoding(string output)
    {
        using var source = new Image<Rgba32>(8, 6, new Rgba32(124, 65, 212, 255));
        source[0, 0] = new(27, 98, 213, 0); source[1, 0] = new(65, 114, 192, 73);
        var files = new List<(string Path, bool Normalize)>();
        var plain = Path.Combine(output, "shared-logo.png"); source.SaveAsPng(plain); files.Add((plain, false));
        var tga = Path.Combine(output, "shared-logo.tga"); source.SaveAsTga(tga); files.Add((tga, true));
        var webp = Path.Combine(output, "shared-logo.webp"); source.SaveAsWebp(webp); files.Add((webp, true));
        using (var oriented = source.Clone())
        {
            oriented.Metadata.ExifProfile = new ExifProfile(); oriented.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
            var path = Path.Combine(output, "shared-oriented.png"); oriented.SaveAsPng(path); files.Add((path, true));
        }
        var profiled = Path.Combine(output, "shared-profiled.png");
        using (var image = source.Clone())
        using (var profile = new ColorContext(PixelFormats.Bgra32).OpenProfileStream())
        using (var bytes = new MemoryStream())
        {
            profile.CopyTo(bytes); image.Metadata.IccProfile = new IccProfile(bytes.ToArray()); image.SaveAsPng(profiled); files.Add((profiled, true));
        }
        using (var document = NewDocumentService.Create(8, 8))
        {
            document.WorkingImage[3, 3] = new(140, 70, 210, 255);
            document.WorkingImage[2, 2] = new(80, 170, 40, 0);
            var path = Path.Combine(output, "shared-logo.dds");
            await TextureCodec.ExportDdsAsync(document, path, new(DdsExportFormat.Bc7, false, 1, DdsAlphaInterpretation.Straight)); files.Add((path, true));
        }
        foreach (var item in files)
        {
            var before = StudioImages.FileRevision(item.Path); var time = File.GetLastWriteTimeUtc(item.Path);
            using var expected = await WpfTextureCodec.LoadLayerImageAsync(item.Path);
            var bitmap = await StudioImages.LoadLogoAsync(item.Path);
            Assert(bitmap.IsFrozen && bitmap.PixelWidth == expected.Width && bitmap.PixelHeight == expected.Height,
                "Shared logo dimensions or frozen ownership failed: " + item.Path);
            Assert(BitmapPixels(bitmap).SequenceEqual(BitmapPixels(PreviewRenderer.Create(expected, ChannelView.Rgba))),
                "Guarded shared logo pixels differ from direct shared decoding: " + item.Path);
            Assert(StudioImages.SourceRevision(bitmap) == before && StudioImages.NeedsNormalization(bitmap) == item.Normalize,
                $"Shared logo revision/normalization mismatch: {item.Path}; normalize={StudioImages.NeedsNormalization(bitmap)}, expected={item.Normalize}, revision={StudioImages.SourceRevision(bitmap)}, expectedRevision={before}");
            var importer = new LogoImportWindow(testing: true);
            try
            {
                await importer.LoadImageAsync(item.Path); Assert(await importer.PreparePlacementAsync(), "Shared import failed placement.");
                Assert((importer.PreparedPath != Path.GetFullPath(item.Path)) == item.Normalize, "Normalized import retained unconverted original bytes.");
                using var prepared = await WpfTextureCodec.LoadLayerImageAsync(importer.PreparedPath!);
                Assert(BitmapPixels(PreviewRenderer.Create(prepared, ChannelView.Rgba)).SequenceEqual(BitmapPixels(bitmap)), "Placement did not retain the shared preview's exact pixels.");
                using var direct = PreparedLogoAsset.CopyAndLoad(item.Path, Path.Combine(output, "direct-logos"), CancellationToken.None);
                Assert(Path.GetExtension(direct.Path).Equals(item.Normalize ? ".png" : Path.GetExtension(item.Path), StringComparison.OrdinalIgnoreCase)
                    && BitmapPixels(direct.Image).SequenceEqual(BitmapPixels(bitmap)), "Direct logo placement bypassed shared normalization.");
            }
            finally { importer.ReleaseTemporaryOutput(); importer.Close(); }
            Assert(StudioImages.FileRevision(item.Path) == before && File.GetLastWriteTimeUtc(item.Path) == time, "Shared import modified the original.");
        }
        var transparent = await StudioImages.LoadLogoAsync(plain); var raw = BitmapPixels(transparent);
        Assert(raw[0] == 213 && raw[1] == 98 && raw[2] == 27 && raw[3] == 0 && raw[7] == 73, "Shared preview destroyed hidden RGB or independent alpha.");
        var rotated = await StudioImages.LoadLogoAsync(files.Single(item => item.Path.EndsWith("oriented.png")).Path);
        Assert(rotated.PixelWidth == 6 && rotated.PixelHeight == 8, "Shared orientation was not applied.");
        var retained = new LogoImportWindow(testing: true); var previousMemory = StudioImageMemory.AvailableBytes;
        try
        {
            await retained.LoadImageAsync(plain); var cleanup = retained.Cleanup; var originalPath = retained.SourcePath;
            StudioImageMemory.AvailableBytes = () => 1; var failed = false;
            try { await retained.LoadImageAsync(profiled); }
            catch (InvalidOperationException error) when (error.Message.Contains("temporary memory")) { failed = true; }
            Assert(failed && ReferenceEquals(retained.Cleanup, cleanup) && retained.SourcePath == originalPath,
                "Low-memory shared import replaced the preceding draft.");
            StudioImageMemory.AvailableBytes = previousMemory; await retained.LoadImageAsync(profiled);
            Assert(await retained.PreparePlacementAsync(), "Shared import did not retry after memory rejection.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var cancelled = false;
            try { await StudioImages.LoadLogoAsync(plain, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !retained.IsVisible, "Canceled shared decode proceeded or opened a window.");
        }
        finally { StudioImageMemory.AvailableBytes = previousMemory; retained.ReleaseTemporaryOutput(); retained.Close(); }
        await CheckNormalizedLogoWorkflow(output, profiled);
    }

    private static async Task CheckNormalizedLogoWorkflow(string output, string source)
    {
        var importer = new LogoImportWindow(testing: true);
        var window = new StudioWindow(true, new PythonServiceClient(), new PythonServiceClient(),
            prepareLogo: (path, token) => Task.Run(() => PreparedLogoAsset.CopyAndLoad(path, Path.Combine(output, "managed-logos"), token), token));
        try
        {
            await importer.LoadImageAsync(source); Assert(await importer.PreparePlacementAsync(), "Profiled import did not prepare.");
            await window.InitializeAsync(); await window.AddLogoAsync(importer.PreparedPath!, "Shared profile workflow");
            var layer = window.Canvas.Layers.Single(); var path = layer.Path; var hash = StudioImages.FileRevision(path);
            importer.ReleaseTemporaryOutput(); Assert(File.Exists(path), "Releasing import deleted the adopted logo.");
            await window.UndoAsync(); Assert(window.Canvas.Layers.Count == 0, "Shared logo import undo failed.");
            await window.UndoAsync(true); layer = window.Canvas.Layers.Single();
            await window.AddLogoAsync(source, "Direct shared profile");
            var direct = window.Canvas.SelectedLayer!; Assert(direct.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && StudioImages.FileRevision(direct.Path) == hash, "Direct placement did not retain the same normalized PNG as the importer.");
            await window.UndoAsync(); layer = window.Canvas.Layers.Single();
            layer.X = 4000; layer.Y = 2000; layer.Width = 8; layer.Height = 6;
            var project = Path.Combine(output, "shared-normalized.court.json"); window.SaveProjectTo(project); await window.OpenProjectFromAsync(project);
            var restored = window.Canvas.Layers.Single(); Assert(StudioImages.FileRevision(restored.Path) == hash, "Portable reopening changed normalized artwork.");
            var png = Path.Combine(output, "shared-normalized-export.png"); await window.ExportToAsync(png, false);
            using var actual = SixLabors.ImageSharp.Image.Load<Rgba32>(png); using var expected = await WpfTextureCodec.LoadLayerImageAsync(source);
            Assert(actual.Width == 8192 && actual.Height == 4096 && actual[4004, 2003] == expected[4, 3], "Court export lost normalized logo pixels.");
            Assert(!window.IsVisible && !importer.IsVisible, "Normalized workflow opened a window.");
        }
        finally { importer.ReleaseTemporaryOutput(); importer.Close(); window.Close(); }
    }

    private static void CheckSharedPreviewAndMemory(string output)
    {
        var path = Path.Combine(output, "shared-data-alpha.png");
        using var source = new Image<Rgba32>(12, 8, new Rgba32(130, 70, 210, 0)); source[6, 4] = new(20, 190, 40, 73); source.SaveAsPng(path);
        var hash = StudioImages.FileRevision(path); var metadata = new JsonObject { ["artworkAlphaMode"] = "GameData" };
        using var expected = source.Clone(); expected.Mutate(context => context.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(6, 4), PremultiplyAlpha = false }));
        var preview = StudioArtworkPreview.Load(path, metadata, 6);
        Assert(BitmapPixels(preview).SequenceEqual(BitmapPixels(PreviewRenderer.Create(expected, ChannelView.Rgb)))
            && BitmapPixels(preview).Where((_, index) => index % 4 == 3).All(value => value == 255)
            && BitmapPixels(preview)[2] == 130 && source[0, 0].A == 0 && StudioImages.FileRevision(path) == hash,
            "Shared game-data preview altered alpha/source or premultiplied hidden RGB.");
        var cleanup = new LogoCleanupImage(PreviewRenderer.Create(source, ChannelView.Rgba)); cleanup.RemoveRegion(6, 4, 0);
        var state = BitmapPixels(cleanup.Bitmap()); var canUndo = cleanup.CanUndo;
        var previous = StudioImageMemory.AvailableBytes;
        void Reject(Action action) { var rejected = false; try { action(); } catch (InvalidOperationException error) when (error.Message.Contains("temporary memory")) { rejected = true; } Assert(rejected, "Large allocation was not rejected by shared memory preflight."); }
        try
        {
            StudioImageMemory.AvailableBytes = () => 1;
            Reject(() => cleanup.Reset()); Reject(() => cleanup.RemoveBackground(Colors.Black, 0)); Reject(() => cleanup.Bitmap());
            Reject(() => new LogoCleanupImage(preview));
            var uncached = Path.Combine(output, "shared-low-memory.png"); source.SaveAsPng(uncached);
            Reject(() => StudioImages.Load(uncached)); Reject(() => StudioImages.LoadLogo(uncached));
            Reject(() => StudioArtworkPreview.Load(uncached, metadata, 3));
            Assert(cleanup.CanUndo == canUndo, "Memory rejection changed cleanup history.");
        }
        finally { StudioImageMemory.AvailableBytes = previous; }
        Assert(BitmapPixels(cleanup.Bitmap()).SequenceEqual(state), "Memory rejection changed cleanup pixels.");
        cleanup.Undo(); Assert(!cleanup.HasChanges, "Cleanup could not recover after memory rejection.");
    }
}
