using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private sealed record PreparedFloor(StockFloor Floor, BitmapSource Thumbnail, Drawing Drawing, string SourceRevision, BitmapSource Image, HardwoodTextureSettings Settings);
    private string? _floorSourceRevision;
    private readonly Func<string, int, BitmapSource> _loadFloorImage;
    private readonly bool _usePairedFloorLoader;
    private readonly SemaphoreSlim _floorDecoder = new(1, 1);
    private static bool HistoryArtworkMatches(JsonObject saved, string path, JsonObject retained, string retainedPath, string? revision)
    {
        if (revision is null || saved["sourceRevision"]?.GetValue<string>() != revision
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(retainedPath))) return false;
        foreach (var key in new[] { "artworkProjectPath", "artworkProjectRevision", "artworkDdsPath", "artworkDdsRevision", "artworkAlphaMode", "textSettings" })
            if (!JsonNode.DeepEquals(saved[key], retained[key])) return false;
        return true;
    }
    private PreparedFloor? HistoryFloor(JsonObject saved, string path) => new[] { _preparedMainFloor, _preparedTwoPointFloor }
        .FirstOrDefault(prepared => prepared is not null && HistoryArtworkMatches(saved, path, prepared.Floor.Source, prepared.Floor.Path, prepared.SourceRevision));
    private async Task<PreparedFloor> PrepareRestoredFloorAsync(StockFloor floor, bool history, CancellationToken cancellation)
    {
        var retained = history ? HistoryFloor(floor.Source, floor.Path) : null;
        if (retained is null) return await PrepareFloorAsync(floor, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var settings = HardwoodTextureSettings.Read(floor.Source);
        var currentSettings = ReferenceEquals(retained, _preparedMainFloor) ? _mainRenderedSettings : _twoPointRenderedSettings;
        var drawing = settings == currentSettings ? (ReferenceEquals(retained, _preparedMainFloor) ? _hardwoodDrawing : _twoPointDrawing)!
            : settings == retained.Settings ? retained.Drawing
            : await Task.Run(() => HardwoodTextureSettings.CreateDrawing(retained.Image, HardwoodRectangle(), _courtSurface!, settings), cancellation);
        return retained with { Floor = floor, Settings = settings, Drawing = drawing };
    }
    private CancellationTokenSource? _floorSelectionCancellation;
    private long InvalidateFloorRequests()
    {
        ++_floorRevision;
        _floorSelectionCancellation?.Cancel();
        _floorSelectionCancellation = null;
        return _floorRevision;
    }
    private async Task<PreparedFloor> PrepareFloorAsync(StockFloor floor, CancellationToken cancellation = default, Geometry? region = null)
    {
        var bounds = _geometry!["gameUv"]!["hardwoodBounds"]!.AsArray();
        var rectangle = new Rect(Number(bounds[0]), Number(bounds[1]), Number(bounds[2]), Number(bounds[3]));
        await _floorDecoder.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                BitmapSource image,thumbnail;
                if (floor.Source["artworkAlphaMode"]?.GetValue<string>() == "GameData")
                { image = StudioArtworkPreview.Load(floor.Path, floor.Source, 2048); thumbnail = StudioArtworkPreview.Load(floor.Path, floor.Source, 144); }
                else if(_usePairedFloorLoader && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(floor.Path),Path.GetFullPath(floor.PreviewPath)))
                {
                    var pair=StudioImages.LoadPair(floor.Path,2048,144,cancellation);image=pair.Image;thumbnail=pair.Thumbnail;
                }
                else
                {
                    image=_loadFloorImage(floor.Path,2048);cancellation.ThrowIfCancellationRequested();
                    try { thumbnail = _loadFloorImage(floor.PreviewPath, 144); }
                    catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
                    { cancellation.ThrowIfCancellationRequested(); thumbnail = _loadFloorImage(floor.Path, 144); }
                }
                cancellation.ThrowIfCancellationRequested();
                var settings = HardwoodTextureSettings.Read(floor.Source);
                var drawing = HardwoodTextureSettings.CreateDrawing(image, rectangle, region ?? _courtSurface!, settings);
                return new PreparedFloor(floor, thumbnail, drawing, StudioImages.SourceRevision(image)!, image, settings);
            }, cancellation);
        }
        finally { _floorDecoder.Release(); }
    }
    private void ApplyFloor(PreparedFloor prepared)
    {
        CancelImportRequest(); _importPreviewRequest = null;
        _floor = prepared.Floor; _hardwoodDrawing = prepared.Drawing;
        _preparedMainFloor = prepared; _mainHardwoodSettings = _mainRenderedSettings = prepared.Settings;
        _floorSourceRevision = prepared.SourceRevision;
        _recent.Remove(prepared.Floor.Id); _recent.Insert(0, prepared.Floor.Id); if (_recent.Count > 20) _recent.RemoveAt(20);
        SavePreferences();
        RefreshImportState();
        RefreshHardwoodValues();
    }
    private void RefreshMutationState()
    {
        HistoryInputShield.Visibility = _historyRestoring && _restoring ? Visibility.Visible : Visibility.Collapsed;
        if (_historyRestoring && _restoring) return;
        var available = _ready && !_restoring && !_saving && !_catalogBusy && !_closed && !_closePending && !_artworkEditorOpen;
        ApplicationMenu.IsEnabled = DocumentChrome.IsEnabled = ToolOptionsOverlay.IsEnabled = WorkspaceRoot.IsEnabled = available;
        SaveToolbarButton.IsEnabled = SaveMenuItem.IsEnabled = SaveAsMenuItem.IsEnabled = available && PendingLogoImports == 0;
        ExportTopButton.IsEnabled = available && !_exporting && PendingLogoImports == 0;
        ExportPanel.IsEnabled = available && !_exporting && PendingLogoImports == 0;
        RefreshImportState();
        HardwoodOptions.IsEnabled = CanChangeDocument;
        RefreshHardwoodValues();
    }
}
