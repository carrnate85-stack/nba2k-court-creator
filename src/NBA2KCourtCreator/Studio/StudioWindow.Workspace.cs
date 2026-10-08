using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==WindowStateProperty && MaximizeButton is not null)RefreshWindowStateIcon();
    }
    private void RefreshWindowStateIcon()
    {
        var maximized=WindowState==WindowState.Maximized;
        MaximizeGlyph.Data=Geometry.Parse(maximized?"M6,3 H17 V14 H14 M3,6 H14 V17 H3 Z":"M3,3 H15 V15 H3 Z");
        MaximizeButton.ToolTip=maximized?"Restore":"Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton,maximized?"Restore":"Maximize");
    }
    private string _colorLayerId = "paint-left";
    public void SelectCanvasTool(ArtworkTool tool)
    {
        if (_section == "import" && tool is ArtworkTool.Transform or ArtworkTool.Eyedropper or ArtworkTool.Bucket or ArtworkTool.Type) return;
        CommitHardwoodGesture(); _hardwoodToolActive = false;
        CourtCanvas.CancelGesture();
        if (tool == ArtworkTool.Transform && CourtCanvas.SelectedLayer is null) { RefreshToolState(); return; }
        CourtCanvas.Tool = tool; RefreshToolState(); CourtCanvas.Focus();
    }
    private void ToolClick(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string name } && Enum.TryParse<ArtworkTool>(name, out var tool)) SelectCanvasTool(tool); }
    private void RefreshToolState()
    {
        if (MoveToolButton is null) return;
        var usable = _initialized && !_closePending && !_closed;
        SelectedCourtCard.IsEnabled = _ready && !_catalogBusy && !_closePending && !_closed && PendingLogoImports == 0;
        foreach (var button in new[] { MoveToolButton, TransformToolButton, EyedropperToolButton, PaintToolButton, TextToolButton, HandToolButton, ZoomToolButton })
        { if (!_hardwoodToolActive && Equals(button.Tag, CourtCanvas.Tool.ToString())) button.SetResourceReference(Control.BackgroundProperty, "AccentDarkBrush"); else button.Background = Brushes.Transparent; }
        MoveToolButton.IsEnabled = usable; HandToolButton.IsEnabled = ZoomToolButton.IsEnabled = usable;
        TransformToolButton.IsEnabled = usable && CourtCanvas.SelectedLayer is not null && _section != "import";
        EyedropperToolButton.IsEnabled = usable && _section != "import";
        PaintToolButton.IsEnabled = TextToolButton.IsEnabled = CanChangeDocument && _section != "import";
        TextOptionsBar.Visibility = !_hardwoodToolActive && CourtCanvas.Tool == ArtworkTool.Type && _section != "import" ? Visibility.Visible : Visibility.Collapsed;
        EditTextButton.IsEnabled = ReadTextSettings(CourtCanvas.SelectedLayer) is not null;
        LogoActions.IsEnabled = CourtCanvas.SelectedLayer is not null;
        LogoLayerCount.Text = CourtCanvas.Layers.Count == 1 ? "1 layer" : $"{CourtCanvas.Layers.Count} layers";
        ImportLogoButton.IsEnabled = CanChangeDocument && !_logoImporterOpen && CourtCanvas.Layers.Count + PendingLogoImports < 4;
        DuplicateLogoButton.IsEnabled = MirrorLogoButton.IsEnabled = CopyXMenu.IsEnabled = CopyYMenu.IsEnabled = CourtCanvas.Layers.Count + PendingLogoImports < 4 && CourtCanvas.SelectedLayer is not null;
        var index = CourtCanvas.SelectedLayer is null ? -1 : CourtCanvas.Layers.IndexOf(CourtCanvas.SelectedLayer);
        CourtCanvas.EditingEnabled = _section != "import" && ((!_hardwoodToolActive && CourtCanvas.Tool is ArtworkTool.Move or ArtworkTool.Transform) || _section == "logos");
        TransformOptionsBar.Visibility = _section != "import" && CourtCanvas.Tool is ArtworkTool.Move or ArtworkTool.Transform && index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewContextLabel.Visibility = TransformOptionsBar.Visibility == Visibility.Visible || TextOptionsBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MoveLogoDownButton.IsEnabled = index >= 0 && index < CourtCanvas.Layers.Count - 1;
        MoveLogoUpButton.IsEnabled = index > 0;
        PinnedColorButton.IsEnabled = CanChangeDocument && !_colorPickerOpen;
        ActiveToolText.Text = CourtCanvas.Tool == ArtworkTool.Move && _section != "logos" ? _section switch { "paint" => "Colors & Lines", "import" => "Convert Court", "export" => "Export Court", _ => "Hardwood" } : CourtCanvas.Tool switch { ArtworkTool.Transform => "Resize / rotate logo", ArtworkTool.Eyedropper => "Eyedropper: " + (SelectedColorLayer?.Name ?? "selected layer"), ArtworkTool.Hand => "Hand", ArtworkTool.Zoom => "Zoom", _ => "Move logo" };
        CanvasHint.Text = CourtCanvas.Tool switch { ArtworkTool.Bucket => "Click a court region to fill it with the primary color. Undo restores its previous color or hardwood.", ArtworkTool.Type => "Click to place text; click an existing text layer to edit. Move and Transform position it.", ArtworkTool.Transform => "Drag corner handles to resize; top handle to rotate. Hold Shift to temporarily invert aspect lock.", ArtworkTool.Eyedropper => "Click court artwork to store its color in the primary swatch.", ArtworkTool.Hand => "Drag to pan. Scroll to zoom.", ArtworkTool.Zoom => "Click to zoom in; Alt-click to zoom out.", _ when CourtCanvas.EditingEnabled => "Drag a logo to move it. Corner handles resize; top handle rotates.", _ => "Scroll to zoom. Space + drag to pan." };
        RefreshHardwoodBar();
    }
    private StockLayer? SelectedColorLayer => _paints.Concat(_lines).Append(_outside).FirstOrDefault(layer => layer.Id == _colorLayerId);
    private void RefreshSelectedColor()
    {
        if (PinnedColorSwatch is null) return;
        PickerColors.PrimaryColor = TextureStudio.Services.RasterPaintService.ParseHexColor(_primaryColorHex);
        PinnedColorSwatch.Background = StudioImages.Brush(_primaryColorHex);
        PinnedColorButton.ToolTip = "Primary color " + _primaryColorHex + " · sample with Eyedropper or click to choose";

    }
    private void PickSelectedColorClick(object sender, RoutedEventArgs e)
    {
        PickPrimaryColor();
    }
    private void TeamColorsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedColorLayer is { } layer) PickLayerColor(layer, true);
    }
    private void RefreshHistoryState()
    { if (UndoToolbarButton is null) return; UndoToolbarButton.IsEnabled = UndoMenuItem.IsEnabled = _undo.Count > 0; RedoToolbarButton.IsEnabled = RedoMenuItem.IsEnabled = _redo.Count > 0; }
    private async void UndoClick(object sender, RoutedEventArgs e) => await Guard(() => UndoAsync());
    private async void RedoClick(object sender, RoutedEventArgs e) => await Guard(() => UndoAsync(true));
    private void HardwoodClick(object sender, RoutedEventArgs e) => CatalogClick(sender, e);
    private async void LogosClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocument) return;
        var version = _documentVersion; SwitchSection("logos");
        if (CanChangeDocument && version == _documentVersion) await OpenLogoImporterAsync();
    }
    private async Task OpenLogoImporterAsync()
    {
        if (!CanChangeDocument || _logoImporterOpen || CourtCanvas.Layers.Count + PendingLogoImports >= 4) return;
        var version = _documentVersion;
        bool Current() => CanChangeDocument && version == _documentVersion;
        CourtCanvas.CancelGesture();
        if (!Current()) return;
        _logoImporterOpen = true; RefreshToolState();
        LogoImportWindow? importer = null;
        try
        {
            importer = _createLogoImporter();
            if (!Current()) return;
            if (_showLogoImporter(importer) == true && Current() && CourtCanvas.Layers.Count + PendingLogoImports < 4 && importer.PreparedPath is not null)
            { await Guard(async () => { if (await TryAddLogoAsync(importer.PreparedPath, Path.GetFileNameWithoutExtension(importer.SourcePath), importer.PreparedSourceRevision) is not null && Current()) SelectCanvasTool(ArtworkTool.Move); }); }
        }
        catch (Exception error) { if (!_closed) SetStatus("Logo import could not be completed: " + error.Message); }
        finally
        {
            try { importer?.ReleaseTemporaryOutput(); }
            catch (Exception error) { if (!_closed) SetStatus("Temporary logo cleanup could not finish: " + error.Message); }
            finally { _logoImporterOpen = false; if (!_closed) RefreshToolState(); }
        }
    }
    public void FlipSelectedLogo(bool vertical = false)
    {
        if(!CanChangeDocument || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo))return;
        if(Change(()=>{if(!CourtCanvas.Layers.Contains(logo))return;if(vertical)logo.FlipY=!logo.FlipY;else logo.FlipX=!logo.FlipX;}))RefreshLogoInspector();
    }
    public void CenterSelectedLogo()
    {
        if(!CanChangeDocument || CourtCanvas.SelectedLayer is not { } logo || !CourtCanvas.Layers.Contains(logo))return;
        var center=CourtCanvas.Anchors.FirstOrDefault(anchor=>anchor.Id=="court-center")?.Position ?? new Point(4096,2048);
        if(Change(()=>{if(CourtCanvas.Layers.Contains(logo)){logo.X=center.X-logo.Width/2;logo.Y=center.Y-logo.Height/2;}}))RefreshLogoInspector();
    }
    private void FlipXClick(object sender, RoutedEventArgs e) => FlipSelectedLogo();
    private void FlipYClick(object sender, RoutedEventArgs e) => FlipSelectedLogo(true);
    private void LogoActionMenuClick(object sender, RoutedEventArgs e)
    {
        if(!CanChangeDocument || sender is not Button { IsEnabled: true, ContextMenu: { } menu } button)return;
        menu.PlacementTarget=button;menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;
    }
    private void ExportMenuClick(object sender, RoutedEventArgs e) { ExportTopButton.ContextMenu.PlacementTarget = ExportTopButton; ExportTopButton.ContextMenu.Placement = PlacementMode.Bottom; ExportTopButton.ContextMenu.IsOpen = true; }
    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        for(var source=e.OriginalSource as DependencyObject;source is not null && !ReferenceEquals(source,sender);source=source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if(source is ButtonBase or TextBoxBase or MenuItem or ComboBox)return;
        if(e.ClickCount==2){WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;e.Handled=true;}
        else if(e.LeftButton==MouseButtonState.Pressed)DragMove();
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    public void SaveProjectTo(string path)
    {
        var snapshot = BeginProjectSave(path);
        try { StudioProjectAssets.Save(path, snapshot, _engine.ProjectRoot); CompleteProjectSave(path); }
        finally { _saving = false; RefreshMutationState(); }
    }
    public async Task SaveProjectToAsync(string path)
    {
        path = Path.GetFullPath(path);
        var snapshot = BeginProjectSave(path);
        try
        {
            await Task.Run(() => StudioProjectAssets.Save(path, snapshot, _engine.ProjectRoot));
            if (!_closed) CompleteProjectSave(path);
        }
        finally { _saving = false; RefreshMutationState(); }
    }
    private bool _saving;
    private JsonObject BeginProjectSave(string path)
    {
        CommitPendingDocumentInput();
        CourtCanvas.CancelGesture(); FinishLogoOpacity();
        if(!CanChangeDocument)throw new InvalidOperationException("A court operation is already in progress.");
        var project = CreateProject(); StudioProjectValidation.Validate(project);
        StudioProjectAssets.ValidateDestination(Path.GetFullPath(path), project, _engine.ProjectRoot);
        project["_projectPath"] = Path.GetFullPath(path);
        _saving = true; InvalidateFloorRequests(); RefreshMutationState(); SetStatus("Saving project and portable assets...");
        return project;
    }
    private void CompleteProjectSave(string path)
    { _projectPath = Path.GetFullPath(path); _dirty = false; UpdateProjectLabel(); SetStatus("Project saved."); WriteRecovery(); }
    private readonly Func<string, JsonObject> _readProject;
    public Task OpenProjectFromAsync(string path)
    {
        if (!_ready) throw new InvalidOperationException("Load the court workspace before opening a project.");
        path = Path.GetFullPath(path);
        return RestoreProjectFromAsync(() => Task.Run(() =>
        {
            var project = _readProject(path);
            project["_projectPath"] = path;
            return project;
        }), opened: true);
    }
}
