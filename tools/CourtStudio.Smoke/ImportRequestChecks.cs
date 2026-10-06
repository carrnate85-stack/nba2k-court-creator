using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckImportRequests(string output)
    {
        JsonObject Metadata(string path, string? selected = null) => new()
        {
            ["path"] = path, ["selected"] = selected ?? "bigcourt.dds", ["sourceBounds"] = new JsonArray(0, 0, 128, 64),
            ["sourceRevision"] = new string('a', 64),
            ["textures"] = new JsonArray(new JsonObject { ["name"] = "bigcourt.dds", ["width"] = 128, ["height"] = 64, ["format"] = "DXT1" },
                new JsonObject { ["name"] = "other.dds", ["width"] = 128, ["height"] = 64, ["format"] = "DXT1" })
        };
        Drawing Preview()
        { var drawing = new GeometryDrawing(Brushes.IndianRed, null, new RectangleGeometry(new Rect(0, 0, 8192, 4096))); drawing.Freeze(); return drawing; }
        var holdInspect = false; var holdPreview = false; var failPreview = false;
        var inspections = new List<(TaskCompletionSource<JsonObject> Completion, CancellationToken Token)>();
        var previews = new List<(TaskCompletionSource<Drawing> Completion, CancellationToken Token, JsonObject Request)>();
        Task<JsonObject> Inspect(string path, string? texture, CancellationToken token)
        {
            if (holdInspect) { var task = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); inspections.Add((task, token)); return task.Task; }
            var metadata = Metadata(path, texture);
            if (path == "bad-bounds.iff") metadata["sourceBounds"] = new JsonArray(0, 0, 200, 64);
            if (path == "large-supported.iff") { metadata["textures"]![0]!["width"] = 16384; metadata["textures"]![0]!["height"] = 8192; metadata["sourceBounds"] = new JsonArray(0, 0, 16384, 8192); }
            return Task.FromResult(metadata);
        }
        Task<Drawing> Render(JsonObject request, CancellationToken token)
        {
            Assert(request["geometryRevision"]?.GetValue<string>() is { Length: 64 }, "Conversion request did not pin the preview geometry revision.");
            Assert(request["sourceRevision"]?.GetValue<string>() == new string('a', 64), "Conversion request did not pin the inspected source court revision.");
            Assert(request["backgroundRevision"]?.GetValue<string>() is { Length: 64 }, "Conversion request did not pin its background artwork.");
            if (failPreview || request["sourcePath"]!.GetValue<string>() == "preview-failure.iff") return Task.FromException<Drawing>(new IOException("preview failed"));
            if (holdPreview) { var task = new TaskCompletionSource<Drawing>(TaskCreationOptions.RunContinuationsAsynchronously); previews.Add((task, token, (JsonObject)request.DeepClone())); return task.Task; }
            return Task.FromResult(Preview());
        }
        var root = ProjectRoot();
        var window = new StudioWindow(true, new PythonServiceClient(root, null), new PythonServiceClient(root, null), imports: new StudioImportBackend(Inspect, Render));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        JsonObject? Source() => (JsonObject?)typeof(StudioWindow).GetField("_importSource", flags)!.GetValue(window);
        Drawing? Drawing() => (Drawing?)typeof(StudioWindow).GetField("_importDrawing", flags)!.GetValue(window);
        bool Convertible() => ((Button)window.FindName("ConvertIffButton")).IsEnabled && ((Button)window.FindName("ConvertPngButton")).IsEnabled;
        try
        {
            await window.InitializeAsync(); window.SwitchSection("import");
            await window.LoadImportSourceAsync("large-supported.iff"); Assert(Convertible(), "Native preview metadata imposed a smaller source limit than the existing DDS backend.");
            await window.LoadImportSourceAsync("source-a.iff"); Assert(Convertible(), "Aligned source did not enable conversion exports.");
            var sourceA = Source()!.ToJsonString(); var drawingA = Drawing();
            holdInspect = true; var superseded = window.LoadImportSourceAsync("old-source.iff");
            Assert(!Convertible() && Source()!.ToJsonString() == sourceA && ReferenceEquals(Drawing(), drawingA), "Pending inspection replaced the last valid source/preview too early.");
            holdInspect = false; await window.LoadImportSourceAsync("source-b.iff");
            var sourceB = Source()!.ToJsonString(); var drawingB = Drawing();
            Assert(inspections[0].Token.IsCancellationRequested, "Selecting another source did not signal cancellation.");
            inspections[0].Completion.SetResult(Metadata("old-source.iff")); await superseded;
            Assert(Source()!.ToJsonString() == sourceB && ReferenceEquals(Drawing(), drawingB) && Convertible(), "Late inspection replaced the newest source/preview.");

            await Expect<IOException>(() => window.LoadImportSourceAsync("preview-failure.iff"));
            await Expect<InvalidDataException>(() => window.LoadImportSourceAsync("bad-bounds.iff"));
            Assert(Source()!.ToJsonString() == sourceB && ReferenceEquals(Drawing(), drawingB) && Convertible() && ((TextBlock)window.FindName("ImportSourceText")).Text == "source-b.iff", "Failed source preparation left mismatched metadata, preview, or controls.");

            var boxes = (TextBox[])typeof(StudioWindow).GetField("_edgeBoxes", flags)!.GetValue(window)!;
            boxes[0].Text = "1"; holdPreview = true; var staleBounds = window.RefreshImportAsync(); boxes[0].Text = "2";
            previews[^1].Completion.SetResult(Preview()); await staleBounds;
            Assert(ReferenceEquals(Drawing(), drawingB) && !Convertible(), "A preview for older bounds was accepted as the current conversion.");
            holdPreview = false; await window.RefreshImportAsync(); Assert(Convertible(), "Corrected bounds did not recover conversion exports.");
            var validDrawing = Drawing(); failPreview = true;
            await Expect<IOException>(() => window.RefreshImportAsync());
            Assert(ReferenceEquals(Drawing(), validDrawing) && !Convertible(), "Failed current refresh discarded the preview or left a failed source exportable.");
            failPreview = false; await window.RefreshImportAsync(); Assert(Convertible(), "Successful refresh did not recover exports after failure.");
            await window.SelectFloorAsync(window.Floors[1]); Assert(!Convertible() && Source() is not null, "Changing hardwood did not invalidate conversion alignment while retaining its source.");
            await window.RefreshImportAsync(); Assert(Convertible(), "Conversion did not recover after hardwood alignment refresh.");

            holdPreview = true; var staleNew = window.LoadImportSourceAsync("late-new.iff");
            var heldNew = previews[^1]; await window.NewProjectAsync();
            Assert(heldNew.Token.IsCancellationRequested && Source() is null && Drawing() is null && !Convertible(), "New did not cancel and clear the conversion context.");
            heldNew.Completion.SetResult(Preview()); await staleNew;
            Assert(Source() is null && Drawing() is null && ((TextBlock)window.FindName("ImportSourceText")).Text == "No source court selected", "Late preview reappeared after New.");

            holdPreview = false; await window.LoadImportSourceAsync("source-a.iff");
            var openedPath = Path.GetFullPath(Path.Combine(output, "import-open.court.json")); StudioProjectStore.Write(openedPath, window.CreateProject());
            holdPreview = true; var staleOpen = window.LoadImportSourceAsync("late-open.iff"); var heldOpen = previews[^1];
            await window.OpenProjectFromAsync(openedPath); heldOpen.Completion.SetException(new IOException("obsolete preview failure")); await staleOpen;
            Assert(Source() is null && Drawing() is null && !Convertible(), "Old conversion response changed the opened project.");

            holdInspect = true; var closing = window.LoadImportSourceAsync("late-close.iff"); var heldClose = inspections[^1];
            window.Close(); heldClose.Completion.SetResult(Metadata("late-close.iff")); await closing;
            Assert(heldClose.Token.IsCancellationRequested && Source() is null && Drawing() is null, "Closed workspace accepted a late court inspection.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS conversion requests: latest source wins, preparation is transactional, failure preserves last valid pair, stale bounds/background cannot export, New/Open/Close cancel old work and clear context.");
    }
    private static async Task CheckNativeImportPreview(StudioWindow window, string iff, BitmapSource reference, string output)
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "court-import-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        window.SwitchSection("import"); await window.LoadImportSourceAsync(iff);
        var drawing = (ImageDrawing)typeof(StudioWindow).GetField("_importDrawing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var image = (BitmapSource)drawing.ImageSource;
        Assert(image.IsFrozen && image.PixelWidth == 1200 && image.PixelHeight == 600 && drawing.Rect == new Rect(0, 0, 8192, 4096), "Native import preview did not decode/project the actual IFF texture correctly.");
        Assert(((Button)window.FindName("ConvertIffButton")).IsEnabled && ((TextBlock)window.FindName("ImportSourceText")).Text == Path.GetFileName(iff), "Actual court metadata and preview did not enable conversion together.");
        var metadata = (JsonObject)typeof(StudioWindow).GetField("_importSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Assert(metadata["alignmentSource"]?.GetValue<string>() == "recorded-game-uv", "Native export did not record its full-court layout for safe reimport.");
        var projectBeforeSourceChange = window.CreateProject().ToJsonString();
        byte originalCommentByte;
        using (var source = new FileStream(iff, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        { source.Seek(-1, SeekOrigin.End); originalCommentByte = (byte)source.ReadByte(); source.Seek(-1, SeekOrigin.End); source.WriteByte(originalCommentByte == 32 ? (byte)9 : (byte)32); source.Flush(true); }
        try
        {
            try { await window.RefreshImportAsync(); throw new Exception("Changed IFF source was accepted by the native preview."); }
            catch (InvalidOperationException error) { Assert(error.Message.Contains("source court changed", StringComparison.Ordinal), "Changed-source preview failed for an unrelated reason."); }
            Assert(ReferenceEquals(drawing, typeof(StudioWindow).GetField("_importDrawing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window))
                && !((Button)window.FindName("ConvertIffButton")).IsEnabled && !((Button)window.FindName("ConvertPngButton")).IsEnabled
                && window.CreateProject().ToJsonString() == projectBeforeSourceChange, "Changed source discarded the last preview/project or left conversion exports enabled.");
        }
        finally
        {
            using var source = new FileStream(iff, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            source.Seek(-1, SeekOrigin.End); source.WriteByte(originalCommentByte); source.Flush(true);
        }
        await window.LoadImportSourceAsync(iff);
        Assert(((Button)window.FindName("ConvertIffButton")).IsEnabled, "Reimport did not recover conversion after a source change.");
        metadata = (JsonObject)typeof(StudioWindow).GetField("_importSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var corners = window.Canvas.Anchors.Where(anchor => anchor.Id.StartsWith("court-corner-")).ToArray();
        var expected = new[] { (int)Math.Round(corners.Min(anchor => anchor.Position.X)), (int)Math.Round(corners.Min(anchor => anchor.Position.Y)),
            (int)Math.Round(corners.Max(anchor => anchor.Position.X)), (int)Math.Round(corners.Max(anchor => anchor.Position.Y)) };
        var bounds = metadata["sourceBounds"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        Assert(bounds.SequenceEqual(expected), "Conversion bounds differ from the editor's stock inside corners.");
        AssertImportPixels(reference, image);
        Assert(!Directory.GetDirectories(Path.GetTempPath(), "court-import-*").Except(before, StringComparer.OrdinalIgnoreCase).Any(), "Native import preview left its isolated temporary directory behind.");
        RenderDpi(window, Path.Combine(output, "native-import-preview.png"), 1280, 760, 1);
        var converted = Path.GetFullPath(Path.Combine(output, "native-converted.iff"));
        using var client = new PythonServiceClient(ProjectRoot(), null);
        try
        {
            await client.RequestFileAsync("export-import-iff", new JsonObject
            {
                ["sourcePath"] = Path.GetFullPath(iff), ["textureName"] = metadata["selected"]!.DeepClone(),
                ["bounds"] = metadata["sourceBounds"]!.DeepClone(),
                ["sourceRevision"] = metadata["sourceRevision"]!.DeepClone(),
                ["backgroundRevision"] = window.CreateProject()["floor"]!["sourceRevision"]!.DeepClone(),
                ["backgroundPath"] = window.CreateProject()["floor"]!["path"]!.DeepClone(), ["outputPath"] = converted
            }, TimeSpan.FromMinutes(12));
            using (var archive = ZipFile.OpenRead(converted))
            {
                using var reader = new StreamReader(archive.GetEntry("level_floor.SCNE")!.Open());
                var text = reader.ReadToEnd().Trim(); var scene = JsonNode.Parse(text.StartsWith('{') ? text : "{" + text + "}")!.AsObject();
                var materials = scene.SelectMany(value => value.Value?["Material"]?.AsObject() ?? new JsonObject())
                    .Where(value => value.Value?["Resource"]?["Logo0Texture"] is not null && value.Value?["Resource"]?["Logo1Texture"] is not null).ToArray();
                Assert(materials.Length == 1 && materials[0].Value!["Parameter"]!["Logo1OffsetX"]!.GetValue<double>() == 100
                    && materials[0].Value!["Parameter"]!["Logo1OffsetY"]!.GetValue<double>() == 100, "Converted IFF reintroduced secondary-logo sampling artifacts.");
            }
            await window.LoadImportSourceAsync(converted);
            var second = (ImageDrawing)typeof(StudioWindow).GetField("_importDrawing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            AssertImportPixels(image, (BitmapSource)second.ImageSource);
            RenderDpi(window, Path.Combine(output, "native-converted-preview.png"), 1280, 760, 1);
        }
        finally { File.Delete(converted); }
        Console.WriteLine("PASS native conversion roundtrip: recorded stock bounds, pinned source bytes/comment; real changed-source rejection preserves preview/project and disables exports until reimport; preserved court/apron pixels, actual Python IFF conversion/reimport, secondary-logo sampling disabled, temporary files removed.");
    }

    private static void AssertImportPixels(BitmapSource reference, BitmapSource preview)
    {
        var first = new FormatConvertedBitmap(reference, PixelFormats.Bgra32, null, 0);
        var second = new FormatConvertedBitmap(preview, PixelFormats.Bgra32, null, 0);
        // Sample solid apron and hardwood areas, not thin media lines whose
        // coverage differs between the 1024- and 1200-pixel comparison images.
        foreach (var point in new[] { new Point(.04, .05), new Point(.22, .2), new Point(.48, .25), new Point(.75, .22), new Point(.78, .8), new Point(.04, .95) })
        {
            var a = new byte[4]; var b = new byte[4];
            first.CopyPixels(new Int32Rect((int)(point.X * first.PixelWidth), (int)(point.Y * first.PixelHeight), 1, 1), a, 4, 0);
            second.CopyPixels(new Int32Rect((int)(point.X * second.PixelWidth), (int)(point.Y * second.PixelHeight), 1, 1), b, 4, 0);
            Assert(Enumerable.Range(0, 3).All(index => Math.Abs(a[index] - b[index]) <= 40), $"Conversion moved or changed court artwork at {point}: reference BGRA={string.Join(',', a)}, preview BGRA={string.Join(',', b)}.");
        }
    }
}
