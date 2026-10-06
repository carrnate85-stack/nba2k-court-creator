using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private Drawing? _importDrawing;
    public async Task ExportToAsync(string path, bool iff)
    {
        await ExportProjectToAsync(CaptureExportProject(), path, iff);
    }
    private void RequireExportReady()
    {
        if (_exporting) throw new InvalidOperationException("An export is already running.");
        if (!CanChangeDocument || PendingLogoImports>0 || _floor is null)
            throw new InvalidOperationException("Wait for the court workspace and logo imports to finish loading.");
    }
    private JsonObject CaptureExportProject()
    {
        RequireExportReady();
        CommitPendingDocumentInput();
        RequireExportReady();
        CourtCanvas.CancelGesture();
        RequireExportReady();
        FinishLogoOpacity();
        RequireExportReady();
        var project=CreateProject();StudioProjectValidation.Validate(project);
        return project;
    }
    private async Task ExportProjectToAsync(JsonObject project, string path, bool iff)
    {
        RequireExportReady();
        StudioProjectValidation.Validate(project);
        _exporting = true; SetExportButtons(false);
        try
        {
            var request = (JsonObject)project.DeepClone(); request["outputPath"] = path; request["exportFullResolution"] = true;
            request["geometryRevision"] = _geometryRevision;
            SetStatus(iff ? "Building the single-texture NBA 2K27 IFF..." : "Rendering the original assets at 8192 × 4096...");
            await _exports.RequestFileAsync(iff ? "export-current-iff" : "render", request, TimeSpan.FromMinutes(10));
            SetStatus("Export saved: " + path);
        }
        finally { _exporting = false; SetExportButtons(true); }
    }
    private void SetExportButtons(bool enabled)
    { BuildIffButton.IsEnabled = enabled; RefreshMutationState(); }
    private async void ExportPngClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireExportReady();CommitPendingDocumentInput();
        var dialog = new SaveFileDialog { FileName = "Court.png", Filter = "PNG texture|*.png" };
        if (dialog.ShowDialog(this) == true) await ExportToAsync(dialog.FileName, false);
    });
    private async void ExportIffClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireExportReady();CommitPendingDocumentInput();
        var dialog = new SaveFileDialog { FileName = "Court.iff", Filter = "NBA 2K27 court|*.iff" };
        if (dialog.ShowDialog(this) == true) await ExportToAsync(dialog.FileName, true);
    });
    private async void PrepareBaseClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        SetStatus("Preparing the game export base...");
        var response = await _exports.RequestAsync(["prepare-import-base", ""], TimeSpan.FromMinutes(10));
        ExportInfoText.Text = response["prepared"]?.GetValue<bool>() == true ? "NBA 2K27 export base ready · 8192 × 4096 BC7" : "Export base not prepared";
        SetStatus("Game export base ready.");
    });
    private async void ImportSourceClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "Court IFF|*.iff" }; if (dialog.ShowDialog(this) != true) return;
        await LoadImportSourceAsync(dialog.FileName);
    });
    public async Task LoadImportSourceAsync(string path, string? texture = null)
    {
        if (!_ready || _restoring || _saving || _closed || _closePending) throw new InvalidOperationException("Load the court workspace first.");
        var version = _documentVersion;
        using var cancellation = BeginImportRequest();
        try
        {
            SetStatus("Reading source court textures...");
            var source = (JsonObject)(await _imports.Inspect(path, texture, cancellation.Token)).DeepClone();
            if (!CurrentImport(cancellation, version)) return;
            var metadata = ImportMetadata(source); var request = BuildImportRequest(source, metadata.Bounds);
            var drawing = await _imports.Preview(request, cancellation.Token);
            if (!CurrentImport(cancellation, version)) return;
            _syncing = true;
            try
            {
                _importSource = source; _importDrawing = drawing; _importPreviewRequest = (JsonObject)request.DeepClone();
                ImportSourceText.Text = Path.GetFileName(String(source, "path"));
                ImportTextures.ItemsSource = metadata.Names; ImportTextures.SelectedItem = String(source, "selected");
                for (var index = 0; index < 4; index++) _edgeBoxes[index].Text = metadata.Bounds[index].ToString();
                ImportInfoText.Text = metadata.Info;
            }
            finally { _syncing = false; }
            RebuildBackground(); SetStatus("Imported court aligned to the 2K27 texture size.");
        }
        catch (Exception) when (!CurrentImport(cancellation, version)) { }
        finally
        {
            if (CurrentImport(cancellation, version))
            { _syncing = true; try { ImportTextures.SelectedItem = _importSource is null ? null : String(_importSource, "selected"); } finally { _syncing = false; } }
            FinishImportRequest(cancellation);
        }
    }
    private void ResetImportBounds()
    {
        if (_importSource is null) return;
        var bounds = _importSource["sourceBounds"]!.AsArray();
        for (var index = 0; index < 4; index++) _edgeBoxes[index].Text = Number(bounds[index]).ToString("0");
    }
    private JsonObject ImportRequest(string? outputPath = null)
    {
        if (_importSource is null) throw new InvalidOperationException("Choose a source court IFF first.");
        var edges = _edgeBoxes.Select(box => int.TryParse(box.Text, out var value) ? value : -1).ToArray();
        return BuildImportRequest(_importSource, edges, outputPath);
    }
    public async Task RefreshImportAsync()
    {
        if (_importSource is null || _closed || _restoring || _saving || _closePending) return;
        var request = ImportRequest(); var version = _documentVersion;
        using var cancellation = BeginImportRequest();
        try
        {
            SetStatus("Aligning the imported court...");
            var drawing = await _imports.Preview(request, cancellation.Token);
            if (!CurrentImport(cancellation, version)) return;
            if (!JsonNode.DeepEquals(request, ImportRequest())) { SetStatus("Conversion preview is out of date."); return; }
            _importDrawing = drawing; _importPreviewRequest = (JsonObject)request.DeepClone(); RebuildBackground();
            SetStatus("Imported court aligned to the 2K27 texture size.");
        }
        catch (Exception) when (!CurrentImport(cancellation, version)) { }
        catch { _importPreviewRequest = null; throw; }
        finally { FinishImportRequest(cancellation); }
    }
    private async void ImportTextureChanged(object sender, SelectionChangedEventArgs e) => await Guard(async () =>
    { if (!_syncing && _importSource is not null && ImportTextures.SelectedItem is string texture && texture != String(_importSource, "selected")) await LoadImportSourceAsync(String(_importSource, "path"), texture); });
    private async void ResetImportBoundsClick(object sender, RoutedEventArgs e) => await Guard(async () => { ResetImportBounds(); await RefreshImportAsync(); });
    private async void RefreshImportClick(object sender, RoutedEventArgs e) => await Guard(RefreshImportAsync);
    private async Task ExportConvertedAsync(bool iff)
    {
        if (_exporting) return;
        if (!ImportPreviewCurrent()) throw new InvalidOperationException("Refresh the conversion preview before exporting this court.");
        var dialog = new SaveFileDialog { FileName = iff ? "Converted Court.iff" : "Converted Court.png", Filter = iff ? "NBA 2K27 court|*.iff" : "PNG texture|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        var request = ImportRequest(dialog.FileName); _exporting = true; SetExportButtons(false);
        try { await _exports.RequestFileAsync(iff ? "export-import-iff" : "export-import-png", request, TimeSpan.FromMinutes(10)); SetStatus("Converted court saved: " + dialog.FileName); }
        finally { _exporting = false; SetExportButtons(true); }
    }
    private async void ConvertIffClick(object sender, RoutedEventArgs e) => await Guard(() => ExportConvertedAsync(true));
    private async void ConvertPngClick(object sender, RoutedEventArgs e) => await Guard(() => ExportConvertedAsync(false));
    internal async Task<(string TexturePath, string ProjectPath)> PrepareCanvasExchangeAsync(string? directory = null)
    {
        var project = CaptureExportProject();
        directory ??= Path.Combine(StudioProjectStore.SettingsDirectory, "exchange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var texture = Path.Combine(directory, "court.png");
        var projectPath = Path.Combine(directory, "court.court.json");
        await ExportProjectToAsync(project, texture, false);
        StudioProjectStore.Write(projectPath, project);
        return (texture, projectPath);
    }
    private async void SendCanvasClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireExportReady();CommitPendingDocumentInput();
        var sibling = Path.Combine(Directory.GetParent(_engine.ProjectRoot)!.FullName, "2k Texture Studio", "src", "TextureStudio", "bin", "Release", "net8.0-windows", "TextureStudio.exe");
        var executable = sibling;
        if (!File.Exists(executable))
        { var dialog = new OpenFileDialog { Title = "Locate 2K Canvas", FileName = "TextureStudio.exe", Filter = "2K Canvas executable|TextureStudio.exe" }; if (dialog.ShowDialog(this) != true) return; executable = dialog.FileName; }
        var exchange = await PrepareCanvasExchangeAsync();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--open"); start.ArgumentList.Add(exchange.TexturePath); Process.Start(start);
        SetStatus("Court texture sent to 2K Canvas. The editable court project remains in Court Creator.");
    });
}
