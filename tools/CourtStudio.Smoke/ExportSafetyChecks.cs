using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckExportSafety(string output)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "export-safety-fixture"));
        Directory.CreateDirectory(directory);
        var floorPath = Path.Combine(directory, "floor.png");
        var logoPath = Path.Combine(directory, "logo.png");
        var projectPath = Path.Combine(directory, "protected.court.json");
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var fixture = window.CreateProject();
            File.Copy(fixture["floor"]!["path"]!.GetValue<string>(), floorPath, true);
            File.Copy(Path.Combine(ProjectRoot(), "src", "NBA2KCourtCreator", "Assets", "app-icon.png"), logoPath, true);
            await File.WriteAllTextAsync(projectPath, "protected project fixture");
            fixture["floor"] = new JsonObject { ["id"] = "export-safety-floor", ["name"] = "Export safety fixture", ["path"] = floorPath };
            fixture["logoImages"] = new JsonArray(new JsonObject { ["id"] = "export-safety-logo", ["name"] = "Hidden source", ["path"] = logoPath, ["visible"] = false, ["width"] = 100, ["height"] = 100 });
            fixture["_projectPath"] = projectPath;
            await window.RestoreProjectAsync(fixture);
            var original = window.CreateProject().ToJsonString();
            var sources = new[] { floorPath, window.Canvas.Layers.Single().Path, projectPath };
            foreach (var source in sources)
            {
                var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
                if (source != projectPath)
                {
                    await Expect<InvalidDataException>(() => window.SaveProjectToAsync(source));
                    Assert(SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(hash), "Rejected project save altered its input asset.");
                    Assert(window.CreateProject().ToJsonString() == original && ((System.Windows.FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Rejected project save changed the document or left controls disabled.");
                }
                foreach (var iff in new[] { false, true })
                {
                    try { await window.ExportToAsync(source, iff); throw new Exception("Export accepted a source asset as its destination."); }
                    catch (InvalidOperationException error) { Assert(error.Message.Contains("source assets must stay unchanged", StringComparison.Ordinal), "Export failed for a reason other than protecting its input."); }
                    Assert(SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(hash), "Rejected export altered a source asset.");
                    Assert(window.CreateProject().ToJsonString() == original, "Rejected export changed the open document.");
                    Assert(((Button)window.FindName("BuildIffButton")).IsEnabled, "Rejected export left the export controls disabled.");
                }
            }
            Console.WriteLine("PASS export safety: native PNG/IFF requests cannot replace the selected floor, hidden logo or project; original hashes/document preserved and controls recover.");
        }
        finally
        {
            window.Close();
            foreach (var path in new[] { floorPath, logoPath, projectPath }) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
