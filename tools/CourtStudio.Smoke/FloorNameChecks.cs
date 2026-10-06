using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckFloorNames(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "floor-names-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        JsonObject Item(string id, string name, string category = "NBA") => new()
        { ["id"] = id, ["name"] = name, ["category"] = category, ["path"] = id + ".png", ["previewPath"] = id + ".preview.png", ["extra"] = new JsonObject { ["retain"] = true } };
        var items = new[]
        {
            Item("cavs-2", "2006-07 Cleveland Cavaliers (628) Court Wood2", "Historic NBA"),
            Item("cavs-1", "2006-07 Cleveland Cavaliers (628) Court Wood1", "Historic NBA"),
            Item("rockets", "  Houston   Rockets (023) Court Wood 2  "),
            Item("lakers", "Los Angeles Lakers (015) Wood1"),
            Item("nba-032", "NBA Arena (032) Court Wood1"),
            Item("wnba-001", "WNBA Arena (001) Court Wood1", "WNBA"),
            Item("duplicate-b", "Duplicate Court Wood1"), Item("duplicate-a", "Duplicate Court Wood1"),
            Item("overflow-b", "Large Wood999999999999999999999999"), Item("overflow-a", "Large Wood0"),
            Item("literal", "Collision 1"), Item("collision-b", "Collision Court Wood2"), Item("collision-a", "Collision Court Wood1"),
            Item("custom", "My custom  Wood2 (015) court", "Custom"),
            Item("same-nba-1", "Shared Court Wood1"), Item("same-nba-2", "Shared Court Wood2"), Item("same-wnba", "Shared Court Wood1", "WNBA")
        }.Concat(Enumerable.Range(1, 4).Select(index => Item("allstar-" + index, "NBA All-Star 2022 Court Wood" + index, "Special"))).ToArray();
        var before = items.Select(item => item.ToJsonString()).ToArray();
        var floors = StockFloor.ReadMany(items, root); var named = floors.ToDictionary(floor => floor.Id);
        Assert(named["cavs-1"].Name == "2006-07 Cleveland Cavaliers (628) 1" && named["cavs-2"].Name == "2006-07 Cleveland Cavaliers (628) 2", "Historic variants lost their actual numbers.");
        Assert(named["rockets"].Name == "Houston Rockets" && named["lakers"].Name == "Los Angeles Lakers" && named["nba-032"].Name == "NBA Arena (032)" && named["wnba-001"].Name == "WNBA Arena (001)", "Singleton labels or NBA-only arena-number cleanup are wrong.");
        Assert(named["duplicate-a"].Name == "Duplicate 1" && named["duplicate-b"].Name == "Duplicate 2" && named["overflow-a"].Name == "Large 1" && named["overflow-b"].Name == "Large 2", "Duplicate/absent/zero/overflow material numbers were not assigned deterministic labels.");
        Assert(named["literal"].Name == "Collision 1" && named["collision-a"].Name == "Collision 2" && named["collision-b"].Name == "Collision 3", "Variant labels collided with an existing literal court name.");
        Assert(named["custom"].Name == "My custom  Wood2 (015) court" && named["same-wnba"].Name == "Shared" && named["same-nba-1"].Name == "Shared 1", "Custom names or category-scoped duplicate labels changed unexpectedly.");
        Assert(Enumerable.Range(1, 4).All(index => named["allstar-" + index].Name == "NBA All-Star 2022 " + index), "Four stock variants were not disambiguated.");
        Assert(floors.Select(floor => floor.Id).SequenceEqual(items.Select(item => item["id"]!.GetValue<string>())) && items.Select(item => item.ToJsonString()).SequenceEqual(before)
            && floors.Select(floor => floor.Source.ToJsonString()).SequenceEqual(before), "Naming changed IDs, input order or source metadata.");
        for (var rotation = 0; rotation < items.Length; rotation++)
        {
            var permuted = items.Skip(rotation).Concat(items.Take(rotation)).Reverse().ToArray();
            Assert(StockFloor.ReadMany(permuted, root).All(floor => floor.Name == named[floor.Id].Name), "Variant labels depend on metadata ordering.");
        }
        Assert(StockFloor.ReadMany(floors.Select(floor => floor.Source), root).All(floor => floor.Name == named[floor.Id].Name), "Reading the same metadata twice changes labels.");
        foreach (var number in Enumerable.Range(0, 32))
            Assert(StockFloor.Read(Item("nba-" + number, "Team (" + number.ToString("D3") + ") Court Wood1"), root).Name == "Team", "A standard NBA arena number leaked into its label.");
        var old = (JsonObject)items[0].DeepClone();
        Assert(StockFloor.Read(old, root, floors).Name == named["cavs-2"].Name && old.ToJsonString() == before[0], "Legacy saved stock names did not map by ID without mutation.");
        old["name"] = "Intentionally renamed court";
        Assert(StockFloor.Read(old, root, floors).Name == "Intentionally renamed court", "An intentional saved court name was replaced by its stock label.");

        var libraryIndex = Path.Combine(ProjectRoot(), "assets/court_floor_templates/nba2k27/nba2k27_floor_templates.json");
        var indexHash = SHA256.HashData(File.ReadAllBytes(libraryIndex)); var indexTime = File.GetLastWriteTimeUtc(libraryIndex);
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var pair = window.Floors.Where(floor => floor.Id.Contains("floor-628-court-")).OrderBy(floor => floor.Name).ToArray();
            Assert(pair.Length == 2 && pair[0].Name == "2006-07 Cleveland Cavaliers (628) 1" && pair[1].Name == "2006-07 Cleveland Cavaliers (628) 2", "Real native library retained duplicate Cavalier labels.");
            var textureHash = SHA256.HashData(File.ReadAllBytes(pair[1].Path)); var textureTime = File.GetLastWriteTimeUtc(pair[1].Path);
            await window.SelectFloorAsync(pair[1]);
            Assert(((TextBlock)window.FindName("SelectedCourtText")).Text == pair[1].Name && window.CreateProject()["floor"]!["name"]!.GetValue<string>() == pair[1].Name, "Selection and project snapshots disagree on the floor label.");
            var projectPath = Path.Combine(root, "variant.court.json"); await window.SaveProjectToAsync(projectPath);
            var portable = StudioProjectStore.Read(projectPath);
            Assert(portable["floor"]!["name"]!.GetValue<string>() == pair[1].Name, "Portable save lost the visible variant name.");
            await window.NewProjectAsync();
            Assert(!window.CreateProject()["floor"]!["name"]!.GetValue<string>().EndsWith(" 1"), "New added a suffix to the default singleton court.");
            await window.OpenProjectFromAsync(projectPath);
            var bundledBefore = window.CreateProject()["floor"]!.DeepClone().ToJsonString();
            window.SetLayerSettings("paint-left", color: "#123456"); await window.UndoAsync();
            File.WriteAllText(Path.Combine(root, "portable-undo-comparison.json"), new JsonObject
            { ["before"] = JsonNode.Parse(bundledBefore), ["after"] = window.CreateProject()["floor"]!.DeepClone(), ["label"] = ((TextBlock)window.FindName("SelectedCourtText")).Text }.ToJsonString());
            Assert(window.CreateProject()["floor"]!.ToJsonString() == bundledBefore && ((TextBlock)window.FindName("SelectedCourtText")).Text == pair[1].Name,
                "Undo changed the portable floor's source path, revision or variant label.");
            await window.UndoAsync(true);
            Assert(window.CreateProject()["floor"]!.ToJsonString() == bundledBefore, "Redo changed the bundled floor's identity.");
            var legacy = (JsonObject)portable.DeepClone(); legacy["floor"]!["name"] = pair[1].Source["name"]!.DeepClone();
            var legacyPath = Path.Combine(root, "legacy-variant.court.json"); StudioProjectStore.Write(legacyPath, legacy);
            await window.OpenProjectFromAsync(legacyPath);
            Assert(((TextBlock)window.FindName("SelectedCourtText")).Text == pair[1].Name && window.CreateProject()["floor"]!["name"]!.GetValue<string>() == pair[1].Name,
                "An older portable project lost its selected variant label.");
            var alternatePath = Path.Combine(root, "different-artwork-same-court-id.png");
            var pixels = Enumerable.Range(0, 32 * 16 * 4).Select(index => new byte[] { 203, 101, 17, 255 }[index % 4]).ToArray();
            var image = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(alternatePath)) png.Save(stream);
            var alternateRevision = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(alternatePath))).ToLowerInvariant();
            var alternate = window.CreateProject(); alternate["floor"]!["path"] = alternatePath; alternate["floor"]!["previewPath"] = alternatePath;
            alternate["floor"]!["sourceRevision"] = alternateRevision;
            await window.RestoreProjectAsync(alternate);
            Assert(window.CreateProject()["floor"]!["path"]!.GetValue<string>() == alternatePath && window.CreateProject()["floor"]!["sourceRevision"]!.GetValue<string>() == alternateRevision,
                "An explicit valid saved source was replaced by the matching library ID.");
            var alternateProjectPath = Path.Combine(root, "different-variant.court.json"); await window.SaveProjectToAsync(alternateProjectPath);
            await window.NewProjectAsync(); await window.OpenProjectFromAsync(alternateProjectPath);
            var alternateBefore = window.CreateProject()["floor"]!.DeepClone().ToJsonString();
            window.SetLayerSettings("paint-left", color: "#445566"); await window.UndoAsync(); await window.UndoAsync(true);
            Assert(window.CreateProject()["floor"]!.ToJsonString() == alternateBefore && window.CreateProject()["floor"]!["sourceRevision"]!.GetValue<string>() == alternateRevision,
                "Undo/redo substituted original library pixels for different bundled artwork with the same court ID.");
            var exportPath = Path.Combine(root, "different-artwork-export.png"); await window.ExportToAsync(exportPath, false);
            using (var stream = File.OpenRead(exportPath))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Assert(frame.PixelWidth == 8192 && frame.PixelHeight == 4096, "Portable identity export is not full resolution.");
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var geometry = (JsonObject)typeof(StudioWindow).GetField("_geometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var bounds = geometry["gameUv"]!["hardwoodBounds"]!.AsArray().Select(node => node!.GetValue<double>()).ToArray();
                var matchingPixels = 0; var pixel = new byte[4];
                foreach (var x in new[] { .25, .55, .75 }) foreach (var y in new[] { .25, .55, .75 })
                {
                    converted.CopyPixels(new Int32Rect((int)(bounds[0] + bounds[2] * x), (int)(bounds[1] + bounds[3] * y), 1, 1), pixel, 4, 0);
                    if (pixel[0] == 203 && pixel[1] == 101 && pixel[2] == 17 && pixel[3] == 255) matchingPixels++;
                }
                Assert(matchingPixels > 0, "Real native PNG export did not retain the alternate bundled floor pixels after undo/redo.");
            }
            var missing = window.CreateProject(); missing["floor"]!["path"] = Path.Combine(root, "missing-saved-artwork.png");
            await window.RestoreProjectAsync(missing);
            Assert(window.CreateProject()["floor"]!["path"]!.GetValue<string>() == pair[1].Path && ((TextBlock)window.FindName("StatusText")).Text.Contains("saved hardwood"),
                "Missing explicit artwork did not preserve the documented local-library fallback with a visible notice.");
            Assert(SHA256.HashData(File.ReadAllBytes(pair[1].Path)).SequenceEqual(textureHash) && File.GetLastWriteTimeUtc(pair[1].Path) == textureTime
                && SHA256.HashData(File.ReadAllBytes(libraryIndex)).SequenceEqual(indexHash) && File.GetLastWriteTimeUtc(libraryIndex) == indexTime, "Naming changed local stock textures or their index.");
            var catalog = new FloorCatalogWindow(null, window.Floors, pair[1], [], [], (floor, token) => Task.FromResult(StudioImages.Load(floor.PreviewPath, 208)), true);
            try
            {
                catalog.SetSearch("2006-07 Cleveland");
                Assert(catalog.Rows.Count == 2 && catalog.Rows.Select(row => row.Floor.Name).Distinct().Count() == 2, "Searching duplicate courts hid a variant or reused its label.");
            }
            finally { catalog.Close(); await catalog.WaitForThumbnailsAsync(); }
            File.WriteAllText(Path.Combine(root, "floor-name-audit.json"), new JsonObject
            { ["floors"] = window.Floors.Count, ["realVariants"] = new JsonArray(pair.Select(floor => (JsonNode?)JsonValue.Create(floor.Name)).ToArray()),
              ["libraryIndexUnchanged"] = true, ["selectedTextureUnchanged"] = true, ["portableUndoSourcePreserved"] = true,
              ["differentArtworkSameIdExportPixelsPreserved"] = true, ["missingArtworkFallbackReported"] = true, ["nativeWindowsOpened"] = false }.ToJsonString());
        }
        finally { window.Close(); }
        Console.WriteLine("PASS floor labels: singles unnumbered; stable metadata variants 1-4; collisions, duplicate/missing/zero/overflow numbers; categories/custom names; standard NBA ID cleanup; immutable metadata/IDs/order; real selection, portable save/open/undo/redo and legacy labels; source index/textures unchanged. No native windows opened.");
    }
}
