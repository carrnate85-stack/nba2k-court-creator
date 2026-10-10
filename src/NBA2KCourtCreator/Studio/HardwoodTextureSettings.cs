using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using TextureStudio.Services;
using System.Runtime.CompilerServices;

namespace NBA2KCourtCreator.Studio;

public sealed record HardwoodTextureSettings(int Brightness = 0, int Contrast = 0, int Saturation = 0, int Scale = 100, int Rotation = 0)
{
    private sealed record SourceIdentity(long Id);
    private static readonly ConditionalWeakTable<BitmapSource, SourceIdentity> Sources = new();
    private static readonly StudioBitmapCache AdjustedImages = new(64L * 1024 * 1024, 8);
    private static long _nextSource, _colorPasses, _drawings;
    internal static (long ColorPasses, long Drawings) Statistics => (Interlocked.Read(ref _colorPasses), Interlocked.Read(ref _drawings));
    internal static (int Count, long Bytes, long Budget) AdjustmentCacheStatistics => AdjustedImages.Statistics;
    public JsonObject ToJson() => new() { ["brightness"] = Brightness, ["contrast"] = Contrast, ["saturation"] = Saturation, ["scale"] = Scale, ["rotation"] = Rotation };
    public static HardwoodTextureSettings Read(JsonObject floor)
    {
        if (floor["textureSettings"] is null) return new();
        if (floor["textureSettings"] is not JsonObject settings) throw new InvalidDataException("Invalid hardwood texture settings.");
        int Read(string key, int minimum, int maximum, int fallback = 0)
        {
            if (settings[key] is null) return fallback;
            if (settings[key] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < minimum || number > maximum)
                throw new InvalidDataException("Invalid hardwood " + key + ".");
            return number;
        }
        return new(Read("brightness", -100, 100), Read("contrast", -100, 100), Read("saturation", -100, 100), Read("scale", 50, 200, 100), Read("rotation", -180, 180));
    }

    internal static Drawing CreateDrawing(BitmapSource original, Rect rectangle, Geometry region, HardwoodTextureSettings settings, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        BitmapSource image = original;
        if (settings.Brightness != 0 || settings.Contrast != 0 || settings.Saturation != 0)
        {
            // Frozen source identity prevents changed files from sharing an old color result.
            // Transform-only edits reuse pixels; the bounded cache does not retain source bitmaps.
            var identity = Sources.GetValue(original, _ => new(Interlocked.Increment(ref _nextSource)));
            image = AdjustedImages.Get($"{identity.Id}|{settings.Brightness}|{settings.Contrast}|{settings.Saturation}", () =>
            {
            Interlocked.Increment(ref _colorPasses);
            var rgba = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[rgba.PixelWidth * rgba.PixelHeight * 4]; rgba.CopyPixels(pixels, rgba.PixelWidth * 4, 0);
            using var bgra = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, rgba.PixelWidth, rgba.PixelHeight);
            using var source = bgra.CloneAs<Rgba32>();
            using var adjusted = ColorAdjustmentService.Apply(source, settings.Brightness, settings.Contrast, settings.Saturation, 0);
            using var result = adjusted.CloneAs<Bgra32>(); result.CopyPixelDataTo(pixels);
            var bitmap = BitmapSource.Create(rgba.PixelWidth, rgba.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, rgba.PixelWidth * 4); bitmap.Freeze();
            return bitmap;
            });
        }
        // The shared color operation is atomic; canceled jobs stop before further drawings.
        cancellation.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _drawings);
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = rectangle, TileMode = TileMode.Tile };
        var transforms = new TransformGroup();
        var center = new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
        transforms.Children.Add(new ScaleTransform(settings.Scale / 100.0, settings.Scale / 100.0, center.X, center.Y));
        transforms.Children.Add(new RotateTransform(settings.Rotation, center.X, center.Y));
        brush.Transform = transforms; brush.Freeze();
        var drawing = new GeometryDrawing(brush, null, region); drawing.Freeze(); return drawing;
    }
}
