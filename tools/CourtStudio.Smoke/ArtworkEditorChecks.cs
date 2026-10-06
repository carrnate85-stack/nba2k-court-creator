using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TextureStudio;
using TextureStudio.Models;
using TextureStudio.Services;
using TwoK.Canvas.Hosting;
using Raster = SixLabors.ImageSharp.Image;
using RgbaImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using Size = System.Windows.Size;

internal static partial class Program
{
    private static async Task CheckArtworkEditors(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "artwork-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var managed = Path.Combine(root, "managed");
        var png = Path.Combine(root, "source.png");
        using var source = NewDocumentService.Create(96, 64);
        source.AlphaCompositingMode = AlphaCompositingMode.GameData;
        source.ChannelLabels = ["Court R", "Court G", "Court B", "Material data"];
        source.Layers[0].Image.ProcessPixelRows(rows => { for (var y = 0; y < rows.Height; y++)
            for (var x = 0; x < rows.Width; x++) rows.GetRowSpan(y)[x] = new(90, 120, 180, (byte)(x % 2 == 0 ? 0 : 73)); });
        source.Layers[0].Image.SaveAsPng(png);
        var originalHash = StudioImages.FileRevision(png);
        var resources = Application.Current.Resources.Keys.Cast<object>().ToDictionary(key => key, key => Application.Current.Resources[key]);
        var first = new StudioArtworkEditorWindow(source, "Independent light", false, (_, _) => Task.CompletedTask);
        var second = new StudioArtworkEditorWindow(source, "Independent dark", true, (_, _) => Task.CompletedTask);
        CanvasEditResult accepted;
        try
        {
            Assert(!ReferenceEquals(first.Editor.ActiveDocument!.Layers[0].Image, second.Editor.ActiveDocument!.Layers[0].Image), "Editors share pixels.");
            Assert(!first.Editor.IsDark && second.Editor.IsDark, "Editor themes are not scoped.");
            Assert(resources.Count == Application.Current.Resources.Count && resources.All(pair => ReferenceEquals(pair.Value, Application.Current.Resources[pair.Key])), "Editor changed the host palette.");
            foreach (var tool in new[] { ToolMode.Move, ToolMode.RectSelect, ToolMode.Lasso, ToolMode.PolygonLasso,
                ToolMode.MagicWand, ToolMode.ColorRange, ToolMode.Brush, ToolMode.Eraser, ToolMode.Bucket,
                ToolMode.Eyedropper, ToolMode.Type, ToolMode.Rectangle, ToolMode.Hand, ToolMode.Zoom })
            {
                first.Editor.ActivateTool(tool);
                Assert((ToolMode)typeof(CanvasEditor).GetField("_activeTool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first.Editor)! == tool,
                    "Required shared tool unavailable: " + tool);
            }
            first.Editor.ActivateTool(ToolMode.Move);
            Assert(((MenuItem)first.Editor.FindName("FileMenuItem")!).IsEnabled == false && StudioArtworkEditorWindow.CourtPresets().Length == 3,
                "Host file ownership/court presets are not configured.");
            AddArtworkDraft(first.Editor);
            Assert(!second.Editor.ActiveDocument.CanUndo && second.Editor.ActiveDocument.Layers.Count == 1
                && source.Layers[0].Image[1, 1] == new Rgba32(90, 120, 180, 73) && !source.CanUndo, "Private editing changed the caller or another editor.");
            Assert(first.Editor.ActiveDocument!.Undo() && first.Editor.ActiveDocument.Redo(), "Shared editor undo/redo unavailable.");
            first.Editor.RefreshArtwork();
            foreach (var size in new[] { new Size(1080, 720), new Size(920, 560) })
            {
                var element = (FrameworkElement)first.Content; element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"artwork-editor-{size.Width}.png")); encoder.Save(stream);
                Assert(((Grid)first.Editor.FindName("EditorWorkspace")!).ActualHeight > 200, "Editor canvas collapsed at compact size.");
            }
            accepted = await first.Editor.AcceptAsync();
            using var acceptedPixels = accepted.CreateImage();
            Assert(acceptedPixels[0, 0] == new Rgba32(90, 120, 180, 0), "Accept lost hidden RGB/data alpha.");
            Assert(acceptedPixels[1, 1] == new Rgba32(220, 30, 40, 73), "Accept returned premultiplied pixels or altered independent alpha.");
        }
        finally { first.Close(); second.Close(); }
        Assert(first.Editor.IsDisposed && second.Editor.IsDisposed && first.Editor.ActiveDocument is null && second.Editor.ActiveDocument is null,
            "Closing the host popup retained editor resources.");
        using (var files = await StudioArtworkFiles.PrepareAsync(accepted, 96, 64, root: managed))
        {
            using var pixels = Raster.Load<Rgba32>(files.ImagePath);
            Assert(pixels[0, 0] == new Rgba32(90, 120, 180, 0), "Managed PNG lost alpha-zero RGB.");
            var preview = StudioArtworkPreview.Load(files.ImagePath, new() { ["artworkAlphaMode"] = "GameData" });
            var bytes = new byte[preview.PixelWidth * preview.PixelHeight * 4]; preview.CopyPixels(bytes, preview.PixelWidth * 4, 0);
            Assert(bytes[0] == 180 && bytes[1] == 120 && bytes[2] == 90 && bytes[3] == 255, "Game-data preview lost hidden RGB or interpreted data as transparency.");
        }
        Assert(!Directory.EnumerateFileSystemEntries(managed).Any(), "Uncommitted artwork files were not cleaned up.");
        await Expect<InvalidDataException>(() => StudioArtworkFiles.PrepareAsync(accepted, 97, 64, root: managed));
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); await Expect<OperationCanceledException>(() => StudioArtworkFiles.PrepareAsync(accepted, 96, 64, canceled.Token, managed)); }
        Assert(!Directory.EnumerateFileSystemEntries(managed).Any(), "Canceled artwork preparation left owned files.");
        var tries = 0;
        var retry = new StudioArtworkEditorWindow(source, "Retry host commit", false, (_, _) => ++tries == 1
            ? Task.FromException(new IOException("Injected host failure")) : Task.CompletedTask);
        try { await Expect<IOException>(() => retry.ApplyAsync()); Assert(!retry.Editor.IsDisposed && retry.Editor.IsEnabled, "Failed host Apply cannot retry."); await retry.ApplyAsync(); }
        finally { retry.Close(); }
        var closing = new StudioArtworkEditorWindow(source, "Close during Apply", false, (_, _) => throw new InvalidOperationException("Canceled result reached the host"));
        var pending = closing.ApplyAsync(); closing.Close(); await Expect<OperationCanceledException>(() => pending);
        Assert(closing.Editor.IsDisposed && source.Layers[0].Image[0, 0] == new Rgba32(90, 120, 180, 0), "Close during Apply changed source/retained editor.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = new StudioArtworkEditorWindow(source, "Close during host preparation", false, async (_, token) =>
        { started.SetResult(); await held.Task.WaitAsync(token); });
        pending = preparing.ApplyAsync(); await started.Task;
        Assert(!preparing.Editor.IsEnabled, "The editor accepts new input while its accepted artwork is being committed.");
        preparing.Close(); await Expect<OperationCanceledException>(() => pending);
        Assert(preparing.Editor.IsDisposed, "Host preparation cancellation retained the editor.");
        await CheckArtworkHost(root, managed, png, originalHash);
        await CheckArtworkDds(root, managed, source);
        Assert(StudioImages.FileRevision(png) == originalHash && !source.CanUndo && !source.IsModified, "Host integration overwrote the original source/history.");
        Console.WriteLine("PASS shared artwork integration: isolated scoped editors; compact tools/presets/renders; detached hidden RGB/data alpha; Apply/Cancel/Escape/close; retry/cancel cleanup; one host undo; placement; floor/preview; portable layers/text/masks; DDS metadata/export/reopen; stale source and failed operation safety. No native windows opened.");
    }

    private static void AddArtworkDraft(CanvasEditor editor)
    {
        var document = editor.ActiveDocument!;
        document.RecordHistory("Paint fixture"); document.Layers[0].Image[1, 1] = new(220, 30, 40, 73);
        using var text = new TextEditSession(document, null, new("Court", "Segoe UI", 14, "#FFFFFF", "#000000", 0, false), 12, 12);
        Assert(text.Finish(), "Shared text creation failed.");
        document.RecordHistory("Artwork mask");
        var layer = document.Layers.Last(); layer.Mask = new RgbaImage(layer.Image.Width, layer.Image.Height, new Rgba32(255, 255, 255, 255));
        layer.Mask[0, 0] = new(0, 0, 0, 255); layer.MaskAssetId = Guid.NewGuid(); document.RegisterAsset(layer);
        editor.RefreshArtwork();
    }

    private static async Task CheckArtworkHost(string root, string managed, string png, string originalHash)
    {
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync(); Layout(window, 1440, 900);
            var project = window.CreateProject(); project["floor"]!["path"] = png; project["floor"]!["previewPath"] = png;
            project["floor"]!.AsObject().Remove("sourceRevision"); await window.RestoreProjectAsync(project);
            await window.AddLogoAsync(png, "Editable graphic"); window.SwitchSection("logos");
            var logo = window.Canvas.SelectedLayer!; logo.X = 1200; logo.Y = 1800; logo.Width = 400; logo.Height = 250; logo.Rotation = 27; logo.FlipX = true;
            var pose = logo.Capture();
            int History() => ((List<JsonObject>)typeof(StudioWindow).GetField("_undo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Count;
            foreach (var command in new[] { "cancel", "escape", "close", "host-close" })
            {
                var before = window.CreateProject().ToJsonString(); var count = History();
                await window.EditArtworkAsync(true, editor =>
                {
                    Assert(window.ArtworkEditorOpen && !((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Host is editable during an artwork session.");
                    AddArtworkDraft(editor.Editor);
                    if (command == "cancel") Descendants<Button>((FrameworkElement)editor.Content).Single(button => Equals(button.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (command == "escape") editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenKeySource(),
                        Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    else if (command == "host-close") window.Close();
                    else editor.Close();
                    Assert(editor.Editor.IsDisposed, "Cancel/Escape/close retained a private editor."); return Task.CompletedTask;
                }, managed);
                Assert(before == window.CreateProject().ToJsonString() && count == History() && !window.ArtworkEditorOpen, command + " changed court state/history.");
            }
            var original = window.CreateProject().ToJsonString(); var history = History(); var originalPreview = window.Canvas.SelectedLayer!.Image;
            await window.EditArtworkAsync(true, async editor =>
            {
                AddArtworkDraft(editor.Editor);
                await Expect<InvalidOperationException>(() => window.EditArtworkAsync(true, _ => Task.CompletedTask, managed));
                await Expect<InvalidOperationException>(() => window.SaveProjectToAsync(Path.Combine(root, "blocked.json")));
                await Expect<InvalidOperationException>(() => window.SelectFloorAsync(window.Floors[0]));
                await editor.ApplyAsync();
            }, managed);
            Assert(History() == history + 1 && logo.Capture() == pose && logo.Path != png && !ReferenceEquals(logo.Image, originalPreview), "Apply changed placement, omitted preview refresh or produced multiple undo steps.");
            var applied = window.CreateProject().ToJsonString();
            await window.UndoAsync(); Assert(window.CreateProject().ToJsonString() == original, "Court undo did not restore original asset/project.");
            await window.UndoAsync(true); Assert(JsonNode.DeepEquals(window.CreateProject(), JsonNode.Parse(applied)), "Court redo lost editable artwork.");
            await window.EditArtworkAsync(true, editor =>
            { var doc = editor.Editor.ActiveDocument!; Assert(doc.Layers.Count == 2 && doc.Layers.Last().TextSettings?.Text == "Court" && doc.Layers.Last().Mask?[0, 0].R == 0, "Reopening flattened editable text/layers/mask."); return Task.CompletedTask; }, managed);
            var savedPath = Path.Combine(root, "saved", "artwork.court.json"); await window.SaveProjectToAsync(savedPath);
            var saved = StudioProjectStore.Read(savedPath); var reference = saved["logoImages"]![0]!["artworkProjectPath"]!.GetValue<string>();
            Assert(!Path.IsPathRooted(reference) && File.Exists(Path.GetFullPath(reference, Path.GetDirectoryName(savedPath)!)), "Save omitted portable editable archive.");
            await window.OpenProjectFromAsync(savedPath); window.SwitchSection("logos");
            await window.EditArtworkAsync(true, editor =>
            { Assert(editor.Editor.ActiveDocument!.Layers.Count == 2 && editor.Editor.ActiveDocument.Layers.Last().TextSettings is not null && editor.Editor.ActiveDocument.Layers.Last().Mask is not null, "Save/reopen lost editable data."); return Task.CompletedTask; }, managed);
            var floorBefore = window.CreateProject()["floor"]!.ToJsonString(); history = History(); var background = window.Canvas.BackgroundDrawing;
            await window.EditArtworkAsync(false, async editor => { AddArtworkDraft(editor.Editor); await editor.ApplyAsync(); }, managed);
            Assert(History() == history + 1 && !ReferenceEquals(window.Canvas.BackgroundDrawing, background) && window.CreateProject()["floor"]!.ToJsonString() != floorBefore, "Floor Apply did not update the court/undo.");
            await window.UndoAsync(); Assert(window.CreateProject()["floor"]!.ToJsonString() == floorBefore, "Floor undo lost original mapping/source.");
            await window.UndoAsync(true);
            await window.EditArtworkAsync(false, editor => { Assert(editor.Editor.ActiveDocument!.Layers.Count == 2, "Edited floor cannot reopen layers."); return Task.CompletedTask; }, managed);
            var unchanged = window.CreateProject().ToJsonString(); history = History();
            await window.EditArtworkAsync(false, async editor =>
            { editor.Editor.ActiveDocument!.CanvasWidth++; await Expect<InvalidDataException>(() => editor.ApplyAsync()); }, managed);
            Assert(window.CreateProject().ToJsonString() == unchanged && History() == history, "Dimension failure partially applied artwork.");
            var stale = window.Canvas.SelectedLayer!.Path; var bytes = await File.ReadAllBytesAsync(stale);
            await window.EditArtworkAsync(true, async editor =>
            { AddArtworkDraft(editor.Editor); await File.AppendAllTextAsync(stale, "changed"); await Expect<IOException>(() => editor.ApplyAsync()); }, managed);
            await File.WriteAllBytesAsync(stale, bytes);
            Assert(window.CreateProject().ToJsonString() == unchanged && History() == history, "Source revision failure partially applied artwork.");
            Assert(StudioImages.FileRevision(png) == originalHash && !window.IsVisible, "Integration overwrote source or opened native host.");
        }
        finally { window.Close(); }
    }

    private static async Task CheckArtworkDds(string root, string managed, TextureDocument source)
    {
        var path = Path.Combine(root, "source.dds");
        await TextureCodec.ExportDdsAsync(source, path, new(DdsExportFormat.Bc7, true, 3, DdsAlphaInterpretation.UnspecifiedData));
        var hash = StudioImages.FileRevision(path);
        using var document = await WpfTextureCodec.LoadAsync(path);
        var metadata = document.DdsMetadata!;
        CanvasEditResult result = null!;
        var editor = new StudioArtworkEditorWindow(document, "DDS court data", false, (accepted, _) => { result = accepted; return Task.CompletedTask; });
        try
        { editor.Editor.ActiveDocument!.RecordHistory("DDS edit"); editor.Editor.ActiveDocument.Layers[0].Image[1, 1] = new(200, 40, 60, 73); editor.Editor.RefreshArtwork(); await editor.ApplyAsync(); }
        finally { editor.Close(); }
        using var files = await StudioArtworkFiles.PrepareAsync(result, 96, 64, root: managed);
        Assert(files.DdsPath is not null && StudioImages.FileRevision(path) == hash, "Apply omitted DDS output or overwrote source.");
        using var reopened = await TextureCodec.LoadAsync(files.DdsPath!);
        Assert(reopened.DdsMetadata!.Width == metadata.Width && reopened.DdsMetadata.Height == metadata.Height
            && reopened.DdsMetadata.DxgiFormat == metadata.DxgiFormat && reopened.DdsMetadata.MipMapCount == metadata.MipMapCount
            && reopened.DdsMetadata.AlphaMode == metadata.AlphaMode && reopened.AlphaCompositingMode == document.AlphaCompositingMode,
            "DDS export/reopen changed metadata, sRGB, mips or data-alpha interpretation.");
        Assert(JsonSerializer.Serialize(reopened.DdsMetadata with { FileSize = metadata.FileSize }) == JsonSerializer.Serialize(metadata),
            "DDS export/reopen changed resource/array/format/capability metadata.");
        using var archive = File.OpenRead(files.ProjectPath); using var editable = ProjectCodec.Load(archive);
        Assert(JsonSerializer.Serialize(editable.DdsMetadata) == JsonSerializer.Serialize(metadata)
            && editable.Layers[0].Image[1, 1] == new Rgba32(200, 40, 60, 73), "Editable archive lost DDS metadata or independent alpha.");
        var decoded = reopened.Layers[0].Image[1, 1];
        Assert(Math.Abs(decoded.R - 200) < 60 && Math.Abs(decoded.G - 40) < 60 && Math.Abs(decoded.B - 60) < 60 && Math.Abs(decoded.A - 73) < 35,
            "DDS recompression differs beyond codec tolerance.");
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var project = window.CreateProject(); var floor = project["floor"]!.AsObject();
            floor["path"] = files.ImagePath; floor["previewPath"] = files.ImagePath; floor["sourceRevision"] = files.ImageRevision;
            floor["artworkProjectPath"] = files.ProjectPath; floor["artworkProjectRevision"] = files.ProjectRevision;
            floor["artworkDdsPath"] = files.DdsPath; floor["artworkDdsRevision"] = StudioImages.FileRevision(files.DdsPath!);
            floor["artworkAlphaMode"] = "GameData";
            await window.RestoreProjectAsync(project);
            var mapping = project["mappingMode"]?.ToJsonString();
            await window.EditArtworkAsync(false, async popup =>
            {
                var draft = popup.Editor.ActiveDocument!;
                Assert(JsonSerializer.Serialize(draft.DdsMetadata) == JsonSerializer.Serialize(metadata) && draft.AlphaCompositingMode == AlphaCompositingMode.GameData,
                    "Host discarded source DDS metadata/data alpha before editing.");
                draft.RecordHistory("Court DDS pixel"); draft.Layers[0].Image[0, 0] = new(33, 144, 77, 0); popup.Editor.RefreshArtwork();
                await popup.ApplyAsync();
            }, managed);
            var current = window.CreateProject();
            Assert(current["mappingMode"]?.ToJsonString() == mapping && current["floor"]!["artworkAlphaMode"]!.GetValue<string>() == "GameData",
                "Host artwork editing changed game UV coordinates or alpha interpretation.");
            using (var rgba = Raster.Load<Rgba32>(current["floor"]!["path"]!.GetValue<string>()))
                Assert(rgba[0, 0] == new Rgba32(33, 144, 77, 0), "Host DDS Apply destroyed independent alpha or hidden RGB.");
            var destination = Path.Combine(root, "dds-saved", "dds.court.json"); await window.SaveProjectToAsync(destination);
            await window.OpenProjectFromAsync(destination);
            await window.EditArtworkAsync(false, popup =>
            {
                var draft = popup.Editor.ActiveDocument!;
                Assert(draft.Layers[0].Image[0, 0] == new Rgba32(33, 144, 77, 0) && JsonSerializer.Serialize(draft.DdsMetadata) == JsonSerializer.Serialize(metadata),
                    "Portable court save/reopen lost DDS metadata/hidden RGB.");
                return Task.CompletedTask;
            }, managed);
            var saved = StudioProjectStore.Read(destination);
            Assert(!Path.IsPathRooted(saved["floor"]!["artworkDdsPath"]!.GetValue<string>()) && StudioImages.FileRevision(path) == hash,
                "DDS sidecar is not portable or the original was changed.");
        }
        finally { window.Close(); }
    }
}
