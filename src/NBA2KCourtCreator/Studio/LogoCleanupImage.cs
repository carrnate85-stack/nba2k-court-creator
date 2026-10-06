using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NBA2KCourtCreator.Studio;

/// <summary>Local, reversible color removal. The source image is never overwritten.</summary>
public sealed class LogoCleanupImage
{
    private readonly byte[] _original;
    private byte[] _pixels;
    private readonly List<byte[]> _undo = [];
    public int Width { get; }
    public int Height { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool HasChanges => !_pixels.AsSpan().SequenceEqual(_original);
    public LogoCleanupImage(BitmapSource image)
    {
        Width = image.PixelWidth; Height = image.PixelHeight;
        if ((long)Width * Height > 24_000_000) throw new InvalidDataException("Logo cleanup supports images up to 24 million pixels. Resize this logo before importing.");
        StudioImageMemory.RequirePixels((long)Width * Height, 3);
        // BGRA conversion unpremultiplies semi-transparent pixels before color comparisons.
        var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        _original = new byte[checked(Width * Height * 4)]; converted.CopyPixels(_original, Width * 4, 0); _pixels = (byte[])_original.Clone();
    }
    public BitmapSource Bitmap()
    { StudioImageMemory.RequirePixels((long)Width * Height); var result = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, _pixels, Width * 4); result.Freeze(); return result; }
    public Color Pixel(int x, int y)
    { var index = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 4; return Color.FromArgb(_pixels[index + 3], _pixels[index + 2], _pixels[index + 1], _pixels[index]); }
    public static Point? MapPreview(Point point, Size viewport, Size image)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) return null;
        var scale = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height);
        var x = (point.X - (viewport.Width - image.Width * scale) / 2) / scale;
        var y = (point.Y - (viewport.Height - image.Height * scale) / 2) / scale;
        return x < 0 || y < 0 || x >= image.Width || y >= image.Height ? null : new Point(Math.Floor(x), Math.Floor(y));
    }
    public int RemoveRegion(int x, int y, int tolerance, bool allMatching = false)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        var color = Pixel(x, y); if (color.A == 0) return 0;
        return Remove(color, tolerance, allMatching ? null : [y * Width + x]);
    }
    public int RemoveBackground(Color color, int tolerance)
    {
        if (color.A == 0) return 0;
        var seeds = new List<int>(2 * (Width + Height));
        for (var x = 0; x < Width; x++) { seeds.Add(x); seeds.Add((Height - 1) * Width + x); }
        for (var y = 1; y < Height - 1; y++) { seeds.Add(y * Width); seeds.Add(y * Width + Width - 1); }
        return Remove(color, tolerance, seeds);
    }
    private int Remove(Color color, int tolerance, IEnumerable<int>? seeds)
    {
        tolerance = Math.Clamp(tolerance, 0, 255);
        StudioImageMemory.Require(checked((long)Width * Height * (seeds is null ? 4 : 13)));
        var next = (byte[])_pixels.Clone(); int removed = 0;
        bool Matches(int pixel) { var i = pixel * 4; return _pixels[i + 3] > 0 && Math.Abs(_pixels[i] - color.B) <= tolerance && Math.Abs(_pixels[i + 1] - color.G) <= tolerance && Math.Abs(_pixels[i + 2] - color.R) <= tolerance; }
        if (seeds is null)
        {
            for (var pixel = 0; pixel < Width * Height; pixel++) if (Matches(pixel)) { next[pixel * 4 + 3] = 0; removed++; }
        }
        else
        {
            // Four-connected flood fill never crosses a different-color boundary or wraps a scanline.
            var visited = new bool[Width * Height]; var queue = new Queue<int>();
            void Enqueue(int pixel) { if (!visited[pixel]) { visited[pixel] = true; if (Matches(pixel)) queue.Enqueue(pixel); } }
            foreach (var seed in seeds) Enqueue(seed);
            while (queue.TryDequeue(out var pixel))
            {
                next[pixel * 4 + 3] = 0; removed++;
                var x = pixel % Width; var y = pixel / Width;
                if (x > 0) Enqueue(pixel - 1); if (x + 1 < Width) Enqueue(pixel + 1);
                if (y > 0) Enqueue(pixel - Width); if (y + 1 < Height) Enqueue(pixel + Width);
            }
        }
        if (removed > 0) { RememberUndo(); _pixels = next; }
        return removed;
    }
    public void Undo() { if (!CanUndo) return; _pixels = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); }
    private void RememberUndo() { _undo.Add(_pixels); var limit = Math.Clamp(128 * 1024 * 1024 / _pixels.Length, 1, 8); while (_undo.Count > limit) _undo.RemoveAt(0); }
    public void Reset() { if (!HasChanges) return; StudioImageMemory.RequirePixels((long)Width * Height); var restored = (byte[])_original.Clone(); RememberUndo(); _pixels = restored; }
    public void WritePng(string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap())); using var output = File.Create(path); encoder.Save(output); }
}
