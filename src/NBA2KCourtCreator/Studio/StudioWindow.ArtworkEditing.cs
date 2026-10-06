using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TextureStudio.Services;
using TwoK.Canvas.Hosting;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private bool _artworkEditorOpen;
    private StudioArtworkEditorWindow? _artworkEditor;
    private CancellationTokenSource? _artworkSessionCancellation;
    private readonly Dictionary<string, JsonObject> _logoArtwork = [];
    internal bool ArtworkEditorOpen => _artworkEditorOpen;
    private void ConfigureArtworkActions()
    {
        var edit = (MenuItem)ApplicationMenu.Items[1]; edit.Items.Add(new Separator());
        var action = new MenuItem { Header = "Edit Artwork...", ToolTip = "Edit the selected court texture, logo or graphic" };
        action.Click += EditArtworkClick; edit.Items.Add(action);
        var floor = new MenuItem { Header = "Edit Artwork..." }; floor.Click += EditFloorArtworkClick;
        SelectedCourtCard.ContextMenu = new ContextMenu(); SelectedCourtCard.ContextMenu.Items.Add(floor);
    }
    private async void EditArtworkClick(object sender, RoutedEventArgs e)
        => await Guard(() => EditArtworkAsync(_section == "logos" && CourtCanvas.SelectedLayer is not null));
    private async void EditFloorArtworkClick(object sender, RoutedEventArgs e) => await Guard(() => EditArtworkAsync(false));
    private async void EditLogoArtworkClick(object sender, RoutedEventArgs e)
    {
        if(!CanChangeDocument || _exporting || PendingLogoImports>0 || sender is not Button { IsEnabled: true } || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo))return;
        await Guard(() => EditArtworkAsync(true));
    }

    internal async Task EditArtworkAsync(bool logoTarget,
        Func<StudioArtworkEditorWindow, Task>? interact = null, string? artworkRoot = null)
    {
        CommitPendingDocumentInput();
        if (!CanChangeDocument || _artworkEditorOpen || _exporting || PendingLogoImports > 0)
            throw new InvalidOperationException("Finish the current court operation before editing artwork.");
        CourtCanvas.CancelGesture(); FinishLogoOpacity();
        if (!CanChangeDocument) throw new InvalidOperationException("The court changed while finishing its current edit.");
        InvalidateFloorRequests();
        var logo = logoTarget ? CourtCanvas.SelectedLayer ?? throw new InvalidOperationException("Select a logo or graphic first.") : null;
        var floor = logo is null ? _floor ?? throw new InvalidOperationException("Select a court texture first.") : null;
        var path = logo?.Path ?? floor!.Path;
        var name = logo?.Name ?? floor!.Name;
        var expected = logo is null ? _floorSourceRevision : StudioImages.SourceRevision(logo.Image);
        var editable = logo is null ? floor!.Source : _logoArtwork.GetValueOrDefault(logo.Id);
        var before = CreateProject(); var version = _documentVersion;
        using var sessionCancellation = new CancellationTokenSource(); _artworkSessionCancellation = sessionCancellation;
        _artworkEditorOpen = true; RefreshMutationState(); RefreshToolState();
        bool Current() => !sessionCancellation.IsCancellationRequested && !_closed && !_closePending && !_restoring && !_saving && version == _documentVersion
            && (logo is null ? ReferenceEquals(floor, _floor) : CourtCanvas.Layers.Contains(logo) && logo.Path == path);
        try
        {
            if (expected is not null && await Task.Run(() => StudioImages.FileRevision(path)) != expected)
                throw new IOException("Artwork changed since the court preview. Reload it before editing.");
            using var original = editable?["artworkProjectPath"]?.GetValue<string>() is { Length: > 0 } projectPath
                ? await Task.Run(() => LoadEditableArtwork(projectPath, editable["artworkProjectRevision"]?.GetValue<string>()))
                : await WpfTextureCodec.LoadAsync(path);
            if (!Current()) throw new OperationCanceledException("The selected court asset is no longer current.");
            var metadata = JsonSerializer.Serialize(original.DdsMetadata);
            var alpha = original.AlphaCompositingMode;
            var channels = original.ChannelLabels.ToArray();
            var width = original.CanvasWidth; var height = original.CanvasHeight;
            _artworkEditor = new StudioArtworkEditorWindow(original, name, StudioTheme.IsDark, async (result, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Current()) throw new InvalidOperationException("The selected court asset changed while editing.");
                using (var stream = new MemoryStream(result.ProjectData.ToArray()))
                using (var document = ProjectCodec.Load(stream))
                    if (JsonSerializer.Serialize(document.DdsMetadata) != metadata || document.AlphaCompositingMode != alpha
                        || !document.ChannelLabels.SequenceEqual(channels))
                        throw new InvalidDataException("Keep the source DDS metadata, alpha interpretation and channel labels when applying.");
                using var prepared = await StudioArtworkFiles.PrepareAsync(result, width, height, cancellation, artworkRoot);
                PreparedFloor? preparedFloor = null; BitmapSource? image = null;
                var data = new JsonObject { ["artworkProjectPath"] = prepared.ProjectPath, ["artworkProjectRevision"] = prepared.ProjectRevision,
                    ["artworkAlphaMode"] = alpha.ToString() };
                if (prepared.DdsPath is not null)
                { data["artworkDdsPath"] = prepared.DdsPath; data["artworkDdsRevision"] = StudioImages.FileRevision(prepared.DdsPath); }
                if (logo is null)
                {
                    var source = (JsonObject)floor!.Source.DeepClone();
                    foreach (var item in data) source[item.Key] = item.Value?.DeepClone();
                    source["path"] = prepared.ImagePath; source["previewPath"] = prepared.ImagePath;
                    preparedFloor = await PrepareFloorAsync(floor with { Path = prepared.ImagePath, PreviewPath = prepared.ImagePath, Source = source }, cancellation);
                }
                else image = await Task.Run(() => StudioArtworkPreview.Load(prepared.ImagePath, data), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!Current() || expected is not null && await Task.Run(() => StudioImages.FileRevision(path)) != expected)
                    throw new IOException("The source artwork changed; the court was kept unchanged.");
                cancellation.ThrowIfCancellationRequested(); prepared.Commit();
                if (logo is null) ApplyFloor(preparedFloor!);
                else { logo.Path = prepared.ImagePath; logo.Image = image; _logoArtwork[logo.Id] = data; }
                RecordUndo(before); Changed(); RefreshLogoInspector();
                SetStatus("Artwork applied: " + name);
            });
            if (!_testing) { _artworkEditor.Owner = this; StudioWindowBounds.Attach(_artworkEditor); }
            if (interact is null) _artworkEditor.ShowDialog(); else await interact(_artworkEditor);
        }
        finally
        {
            _artworkEditor?.Close(); _artworkEditor = null; _artworkEditorOpen = false; _artworkSessionCancellation = null;
            if (!_closed) { RefreshMutationState(); RefreshToolState(); }
        }
    }
    private static TextureStudio.Models.TextureDocument LoadEditableArtwork(string path, string? expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (expected is null || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant() != expected)
            throw new InvalidDataException("The editable artwork project is missing or changed; its layers were not discarded.");
        stream.Position = 0;
        return ProjectCodec.Load(stream);
    }
    private JsonObject? RestoreArtworkReference(JsonObject item, string? projectPath, bool relative)
    {
        if (item["artworkProjectPath"]?.GetValue<string>() is not { Length: > 0 } path) return null;
        var reference = new JsonObject();
        foreach (var key in new[] { "artworkProjectPath", "artworkDdsPath" })
            if (item[key]?.GetValue<string>() is { Length: > 0 } value) reference[key] = ResolvePath(value, projectPath, relative);
        foreach (var key in new[] { "artworkProjectRevision", "artworkDdsRevision", "artworkAlphaMode" })
            if (item[key] is { } value) reference[key] = value.DeepClone();
        using var document = LoadEditableArtwork(reference["artworkProjectPath"]!.GetValue<string>(), reference["artworkProjectRevision"]?.GetValue<string>());
        return reference;
    }
}
