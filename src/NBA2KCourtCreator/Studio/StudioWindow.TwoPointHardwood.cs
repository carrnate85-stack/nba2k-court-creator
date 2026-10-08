using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private StockFloor? _twoPointFloor;
    private Drawing? _twoPointDrawing;
    private string? _twoPointSourceRevision;
    private bool _twoPointHardwoodEnabled;
    private readonly Dictionary<string, Geometry> _threePointSurfaces = [];
    private static readonly string[] ThreePointLineIds = ["NBA_line_three_point_lowShape", "college-three", "high-school-three"];

    private void PrepareThreePointSurfaces()
    {
        _threePointSurfaces.Clear();
        foreach (var id in ThreePointLineIds)
        {
            var layer = _geometry!["layers"]!.AsArray().OfType<JsonObject>().Single(item => String(item, "id") == id);
            var points = layer["gameUvPolygons"]!.AsArray().OfType<JsonArray>().SelectMany(polygon => polygon.OfType<JsonArray>())
                .Select(point => new Point(Number(point[0]), Number(point[1]))).ToArray();
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var left in new[] { true, false })
            {
                var sorted = points.Where(point => (point.X < 4096) == left).Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
                if (sorted.Length < 3) continue;
                static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                var hull = new List<Point>();
                foreach (var point in sorted) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
                var lower = hull.Count;
                foreach (var point in sorted.Reverse().Skip(1)) { while (hull.Count > lower && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
                hull.RemoveAt(hull.Count - 1);
                var shape = new StreamGeometry();
                using (var context = shape.Open()) { context.BeginFigure(hull[0], true, true); context.PolyLineTo(hull.Skip(1).ToArray(), true, false); }
                shape.Freeze(); group.Children.Add(shape);
            }
            var clipped = Geometry.Combine(group, _courtSurface!, GeometryCombineMode.Intersect, null);
            clipped.Freeze(); _threePointSurfaces[id] = clipped;
        }
    }
    private Geometry CurrentTwoPointSurface()
    {
        var id = ThreePointLineIds.FirstOrDefault(id => _lines.Any(line => line.Id == id && line.Visible));
        return id is null ? Geometry.Empty : _threePointSurfaces[id];
    }

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
            var prepared = selected is null ? null : await PrepareFloorAsync(selected, cancellation.Token, _courtSurface);
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
