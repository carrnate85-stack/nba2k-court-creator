using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private StockFloor? _twoPointFloor;
    private Drawing? _twoPointDrawing;
    private Geometry? _twoPointSurface;
    private string? _twoPointSourceRevision;
    private bool _twoPointHardwoodEnabled;

    public void SetTwoPointHardwoodEnabled(bool enabled)
    {
        CommitHardwoodNumberInputs();
        if (!CanChangeDocument || PendingLogoImports > 0) throw new InvalidOperationException("Wait for the current court operation before changing two-point hardwood.");
        Change(() => _twoPointHardwoodEnabled = enabled); RefreshHardwoodValues();
    }

    public async Task SelectTwoPointFloorAsync(StockFloor? floor)
    {
        CommitHardwoodGesture();
        if (!_ready || _restoring || _saving || PendingLogoImports > 0 || _closed || _closePending || _artworkEditorOpen)
            throw new InvalidOperationException("The court is not available for editing right now.");
        var revision = InvalidateFloorRequests();
        using var cancellation = new CancellationTokenSource();
        _floorSelectionCancellation = cancellation;
        try
        {
            var selected = floor;
            if (floor is not null && _twoPointFloor is not null && floor.Id == _twoPointFloor.Id && StringComparer.OrdinalIgnoreCase.Equals(floor.Path, _twoPointFloor.Path))
            {
                var source = (JsonObject)floor.Source.DeepClone(); source["textureSettings"] = _twoPointHardwoodSettings.ToJson(); selected = floor with { Source = source };
            }
            var prepared = selected is null ? null : await PrepareFloorAsync(selected, cancellation.Token, _twoPointSurface);
            if (revision != _floorRevision || cancellation.IsCancellationRequested || _closed || _closePending) return;
            var before = CreateProject();
            ApplyTwoPointFloor(prepared, selected is not null);
            if (before.ToJsonString() != CreateProject().ToJsonString()) { RecordUndo(before); Changed(); }
            else RebuildBackground();
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_floorSelectionCancellation, cancellation)) _floorSelectionCancellation = null; }
    }

    private void ApplyTwoPointFloor(PreparedFloor? prepared, bool enabled)
    {
        _twoPointFloor = prepared?.Floor; _twoPointDrawing = prepared?.Drawing;
        _preparedTwoPointFloor = prepared; _twoPointHardwoodSettings = prepared?.Settings ?? new();
        _twoPointSourceRevision = prepared?.SourceRevision;
        _twoPointHardwoodEnabled = enabled;
        RefreshHardwoodValues();
    }

    private JsonObject? TwoPointFloorSnapshot()
    {
        if (_twoPointFloor is null) return null;
        var result = (JsonObject)_twoPointFloor.Source.DeepClone();
        result["path"] = _twoPointFloor.Path; result["name"] = _twoPointFloor.Name;
        if (_twoPointSourceRevision is not null) result["sourceRevision"] = _twoPointSourceRevision;
        result["textureSettings"] = _twoPointHardwoodSettings.ToJson();
        return result;
    }

}
