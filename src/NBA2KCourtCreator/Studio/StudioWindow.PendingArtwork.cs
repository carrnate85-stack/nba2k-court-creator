using System.IO;
using System.Windows.Media.Imaging;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private readonly HashSet<CancellationTokenSource> _pendingLogos = [];
    private readonly Func<string, CancellationToken, Task<PreparedLogoAsset>> _prepareLogo;
    private long _documentVersion;
    private bool _logoImporterOpen;
    private readonly Func<LogoImportWindow> _createLogoImporter;
    private readonly Func<LogoImportWindow, bool?> _showLogoImporter;
    internal int PendingLogoImports => _pendingLogos.Count;
    private void InvalidateDocumentOperations()
    {
        ++_documentVersion; CancelImportRequest(); _importPreviewRequest = null;
        foreach (var pending in _pendingLogos) pending.Cancel();
        _pendingLogos.Clear();
    }
    private Task<PreparedLogoAsset> PrepareLogoAsync(string path, CancellationToken cancellation)
    {
        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if (!_testing && !path.StartsWith(_engine.ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return PreparedLogoAsset.CopyAndLoad(path, Path.Combine(_engine.ProjectRoot, "logos"), cancellation);
            var image = StudioImages.Load(path);
            cancellation.ThrowIfCancellationRequested();
            return new PreparedLogoAsset(image, path);
        }, cancellation);
    }
    private async Task<ArtworkLayer?> TryAddLogoAsync(string path, string? displayName = null, string? expectedSourceRevision = null)
    {
        if (!CanChangeDocument) throw new InvalidOperationException("Wait for the current court operation to finish before importing a logo.");
        if (CourtCanvas.Layers.Count + PendingLogoImports >= 4) throw new InvalidOperationException("Court export supports four logo slots. Delete a logo before importing another.");
        path = Path.GetFullPath(path);
        var name = displayName ?? Path.GetFileNameWithoutExtension(path);
        var version = _documentVersion;
        using var cancellation = new CancellationTokenSource();
        _pendingLogos.Add(cancellation); RefreshToolState(); RefreshMutationState();
        bool Current() => CanChangeDocument && version == _documentVersion && _pendingLogos.Contains(cancellation);
        try
        {
            using var prepared = await _prepareLogo(path, cancellation.Token);
            if (!Current()) return null;
            if (expectedSourceRevision is not null && StudioImages.SourceRevision(prepared.Image) != expectedSourceRevision)
                throw new InvalidDataException("The logo changed after its preview. Choose the image again before placing it.");
            if (CourtCanvas.Layers.Count >= 4) throw new InvalidOperationException("All four logo slots are now occupied.");
            var image = prepared.Image;
            var scale = Math.Min(1310.0 / image.PixelWidth, 820.0 / image.PixelHeight);
            var width = image.PixelWidth * scale; var height = image.PixelHeight * scale;
            var center = CourtCanvas.Anchors.FirstOrDefault(anchor => anchor.Id == "court-center")?.Position ?? new System.Windows.Point(4096, 2048);
            var logo = new ArtworkLayer { Name = name, Path = prepared.Path, Image = image, Width = width, Height = height,
                X = center.X - width / 2 + CourtCanvas.Layers.Count * 40, Y = center.Y - height / 2 + CourtCanvas.Layers.Count * 40 };
            Change(() => { if (Current()) CourtCanvas.Layers.Add(logo); });
            if (!CourtCanvas.Layers.Contains(logo)) return null;
            prepared.Commit(); CourtCanvas.SelectedLayer = logo; RefreshLogoInspector(); return logo;
        }
        catch (Exception) when (!Current()) { return null; }
        finally { _pendingLogos.Remove(cancellation); if (!_closed) { RefreshToolState(); RefreshMutationState(); } }
    }
}
