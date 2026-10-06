using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TextureStudio.Models;
using TextureStudio.Services;

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
            ImageFormatService.ValidateDimensions(header.Width, header.Height);
            StudioImageMemory.RequirePixels((long)header.Width * header.Height, 4);
            stream.Position = 0; using var image = Image.Load<Rgba32>(stream);
            // Data alpha is displayed through the shared RGB view, never written into source pixels.
            if (decodeWidth > 0 && decodeWidth < image.Width) image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(decodeWidth, Math.Max(1, (int)Math.Round((double)image.Height * decodeWidth / image.Width))),
                PremultiplyAlpha = false
            }));
            return PreviewRenderer.Create(image, ChannelView.Rgb);
        });
        StudioImages.AttachRevision(bitmap, hash); return bitmap;
    }
}
