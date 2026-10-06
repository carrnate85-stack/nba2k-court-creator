using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NBA2KCourtCreator.Studio;

internal static class StudioArtworkPreview
{
    private static readonly StudioBitmapCache Cache = new(32L * 1024 * 1024, 20);
    internal static BitmapSource Load(string path, JsonObject metadata, int decodeWidth = 0)
    {
        if (metadata["artworkAlphaMode"]?.GetValue<string>() != "GameData") return StudioImages.Load(path, decodeWidth);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > StudioImages.MaximumAssetBytes) throw new NotSupportedException("Artwork exceeds the 512 MB size limit.");
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var bitmap = Cache.Get($"{hash}|{decodeWidth}", () =>
        {
            stream.Position = 0; var header = Image.Identify(stream);
            if (header.Width > 16384 || header.Height > 16384 || (long)header.Width * header.Height > 64L * 1024 * 1024)
                throw new NotSupportedException("Artwork exceeds the supported pixel dimensions.");
            stream.Position = 0; using var image = Image.Load<Rgba32>(stream);
            // Display RGB as opaque only for explicit game-data alpha. The source PNG/archive/DDS retain A and hidden RGB.
            image.ProcessPixelRows(accessor => { for (var y = 0; y < accessor.Height; y++)
                foreach (ref var pixel in accessor.GetRowSpan(y)) pixel.A = 255; });
            if (decodeWidth > 0 && decodeWidth < image.Width) image.Mutate(context => context.Resize(decodeWidth, 0));
            var pixels = new byte[checked(image.Width * image.Height * 4)]; image.CopyPixelDataTo(pixels);
            for (var offset = 0; offset < pixels.Length; offset += 4) (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
            var result = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);
            result.Freeze(); return result;
        });
        StudioImages.AttachRevision(bitmap, hash); return bitmap;
    }
}
