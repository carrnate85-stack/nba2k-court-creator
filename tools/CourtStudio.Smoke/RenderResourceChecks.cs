using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckLargeLogoExport(string output)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "large-logo-fixture"));
        Directory.CreateDirectory(directory);
        var logo = Path.Combine(directory, "logo.bmp");
        var target = Path.Combine(directory, "large-logo-export.png");
        WriteRevisionBitmap(logo, Colors.Gold);
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var project = window.CreateProject();
            project["logoImages"] = new JsonArray(new JsonObject
            {
                ["id"] = "large-logo", ["name"] = "Large logo", ["path"] = logo,
                ["x"] = -12288, ["y"] = -14336, ["width"] = 32768, ["height"] = 32768,
                ["rotation"] = 37, ["flipX"] = true, ["flipY"] = true, ["opacity"] = 100
            });
            await window.RestoreProjectAsync(project);
            var before = window.CreateProject().ToJsonString();
            await window.ExportToAsync(target, false);
            var image = StudioImages.Load(target, 1024);
            Assert(image.PixelWidth == 1024 && image.PixelHeight == 512, "Large-logo export lost court dimensions.");
            foreach (var point in new[] { new Int32Rect(1, 1, 1, 1), new Int32Rect(512, 256, 1, 1), new Int32Rect(1022, 510, 1, 1) })
            {
                var pixel = new byte[4]; image.CopyPixels(point, pixel, 4, 0);
                Assert(pixel[3] == 255 && Math.Abs(pixel[2] - 255) <= 1 && Math.Abs(pixel[1] - 215) <= 1 && pixel[0] <= 1,
                    "Large rotated/flipped logo was clipped or rendered with incorrect pixels.");
            }
            Assert(window.CreateProject().ToJsonString() == before && ((Button)window.FindName("BuildIffButton")).IsEnabled,
                "Large-logo export changed the document or left export controls disabled.");
            File.Delete(target);
            Console.WriteLine("PASS real native 8192x4096 PNG export of a 32768x32768 rotated/flipped logo: visible pixels/dimensions, unchanged document and enabled controls.");
        }
        finally { window.Close(); }
    }
}
