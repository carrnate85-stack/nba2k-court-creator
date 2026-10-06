using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

internal sealed record StudioImportBackend(
    Func<string, string?, CancellationToken, Task<JsonObject>> Inspect,
    Func<JsonObject, CancellationToken, Task<Drawing>> Preview);

public partial class StudioWindow
{
    private readonly StudioImportBackend _imports;
    private CancellationTokenSource? _currentImport;
    private JsonObject? _importPreviewRequest;
    private void CancelImportRequest()
    { ++_importRevision; _currentImport?.Cancel(); _currentImport = null; }
    private CancellationTokenSource BeginImportRequest()
    {
        CancelImportRequest();
        var cancellation = new CancellationTokenSource(); _currentImport = cancellation;
        RefreshImportState(); return cancellation;
    }
    private bool CurrentImport(CancellationTokenSource cancellation, long version)
        => !_closed && !_restoring && version == _documentVersion && ReferenceEquals(_currentImport, cancellation);
    private void FinishImportRequest(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_currentImport, cancellation)) { _currentImport = null; if (!_closed) RefreshImportState(); }
    }
    private Task<JsonObject> InspectImportAsync(string path, string? texture, CancellationToken cancellation)
        => _engine.RequestAsync(["inspect-import", path, false, texture ?? ""], TimeSpan.FromMinutes(4), cancellation);
    private async Task<Drawing> RenderImportPreviewAsync(JsonObject request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var preview = new StudioImportPreviewFiles();
        var output = preview.OutputPath;
        try
        {
            var isolated = (JsonObject)request.DeepClone(); isolated["outputPath"] = output;
            var result = await _engine.RequestFileAsync("preview-import", isolated, TimeSpan.FromMinutes(4), cancellation);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(String(result, "previewPath")), output))
                throw new InvalidDataException("The import engine returned an unexpected preview path.");
            // Accept the returned ownership receipt even if decoding was just canceled.
            var image = await Task.Run(() => preview.Decode(result["previewReceipt"], cancellation));
            var drawing = new ImageDrawing(image, new Rect(0, 0, 8192, 4096)); drawing.Freeze(); return drawing;
        }
        finally
        {
            await Task.Run(preview.Dispose);
        }
    }
    private static (string[] Names, int[] Bounds, string Info) ImportMetadata(JsonObject source)
    {
        if (string.IsNullOrWhiteSpace(String(source, "path")) || source["textures"] is not JsonArray textures || textures.Count is < 1 or > 20000)
            throw new InvalidDataException("The import engine returned invalid court metadata.");
        var selected = String(source, "selected");
        if (!System.Text.RegularExpressions.Regex.IsMatch(String(source, "sourceRevision"), "\\A[0-9a-f]{64}\\z"))
            throw new InvalidDataException("The import engine returned an invalid source court revision.");
        var descriptors = textures.OfType<JsonObject>().ToArray();
        if (descriptors.Length != textures.Count) throw new InvalidDataException("Invalid court texture metadata.");
        var names = descriptors.Select(item => String(item, "name")).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Court texture names must be nonempty and unique.");
        var texture = descriptors.FirstOrDefault(item => String(item, "name") == selected) ?? throw new InvalidDataException("The selected court texture is missing.");
        var width = Number(texture["width"], double.NaN); var height = Number(texture["height"], double.NaN);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 16 || height < 16 || width > 16384 || height > 16384 || width != Math.Truncate(width) || height != Math.Truncate(height))
            throw new InvalidDataException("The selected texture has unsupported dimensions.");
        if (source["sourceBounds"] is not JsonArray bounds || bounds.Count != 4) throw new InvalidDataException("The imported court needs four detected edges.");
        var values = bounds.Select(value => Number(value, double.NaN)).ToArray();
        if (values.Any(value => !double.IsFinite(value) || value < 0 || value != Math.Truncate(value)) || values[0] >= values[2] || values[1] >= values[3] || values[2] > width || values[3] > height)
            throw new InvalidDataException("Detected court edges are outside the selected texture.");
        return (names, values.Select(value => (int)value).ToArray(), $"{width:0} x {height:0} - {String(texture, "format")}");
    }
    private JsonObject BuildImportRequest(JsonObject source, int[] edges, string? outputPath = null)
    {
        ImportMetadata(source);
        var texture = source["textures"]!.AsArray().OfType<JsonObject>().First(item => String(item, "name") == String(source, "selected"));
        if (edges.Length != 4 || edges[0] < 0 || edges[1] < 0 || edges[0] >= edges[2] || edges[1] >= edges[3] || edges[2] > Number(texture["width"]) || edges[3] > Number(texture["height"]))
            throw new InvalidOperationException("Court edges must form a rectangle inside the source texture.");
        return new JsonObject { ["sourcePath"] = String(source, "path"), ["textureName"] = String(source, "selected"),
            ["geometryRevision"] = _geometryRevision,
            ["sourceRevision"] = String(source, "sourceRevision"),
            ["backgroundRevision"] = _floorSourceRevision,
            ["bounds"] = new JsonArray(edges.Select(edge => (JsonNode?)JsonValue.Create(edge)).ToArray()), ["backgroundPath"] = _floor?.Path, ["outputPath"] = outputPath };
    }
    private bool ImportPreviewCurrent()
    {
        if (_importSource is null || _importPreviewRequest is null || _currentImport is not null) return false;
        try { return JsonNode.DeepEquals(_importPreviewRequest, ImportRequest()); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return false; }
    }
    private void RefreshImportState()
    {
        if (ConvertIffButton is null) return;
        var usable = _ready && !_closed && !_restoring && !_saving && !_closePending && PendingLogoImports == 0;
        var aligned = usable && !_exporting && ImportPreviewCurrent();
        ConvertIffButton.IsEnabled = ConvertPngButton.IsEnabled = aligned;
    }
    private void ResetImportContext()
    {
        CancelImportRequest(); _importSource = null; _importDrawing = null; _importPreviewRequest = null;
        var syncing = _syncing; _syncing = true;
        try { ImportTextures.ItemsSource = null; ImportSourceText.Text = "No source court selected"; ImportInfoText.Text = ""; foreach (var box in _edgeBoxes) box.Text = ""; }
        finally { _syncing = syncing; }
        RebuildBackground(); RefreshImportState();
    }
}
