using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow : Window
{
    private readonly PythonServiceClient _engine, _exports;
    private readonly List<StockLayer> _paints = [], _lines = [];
    private readonly List<StockFloor> _floors = [];
    private readonly List<JsonObject> _undo = [], _redo = [];
    private readonly HashSet<string> _favorites = [];
    private readonly List<string> _recent = [];
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBox[] _edgeBoxes = new TextBox[4];
    private JsonObject? _geometry, _loadedVisibility, _importSource;
    private string? _geometryRevision;
    private JsonArray _teams = [];
    private StockFloor? _floor;
    private string? _defaultFloorId, _projectPath;
    private readonly StockLayer _outside = new() { Id = "stock-outside", Name = "Outside Color", DefaultColor = "#19583F", DefaultVisible = true, Color = "#19583F", Visible = true };
    private string _section = "paint";
    private bool _initialized, _syncing, _dirty, _exporting, _ready, _restoring, _closed;
    private Geometry? _courtSurface;
    private Task? _initializationTask;
    private readonly bool _testing;
    private readonly Func<TextBox?> _focusedInput;
    private long _floorRevision, _importRevision;
    private readonly HashSet<string> _collapsed = [];
    public ArtworkCanvas Canvas => CourtCanvas;
    public IReadOnlyList<StockLayer> PaintLayers => _paints;
    public IReadOnlyList<StockLayer> LineLayers => _lines;
    public IReadOnlyList<StockFloor> Floors => _floors;
    public string Section => _section;

    public StudioWindow() : this(false) { }
    public StudioWindow(bool testing) : this(testing, new PythonServiceClient(), new PythonServiceClient()) { }
    internal StudioWindow(bool testing, PythonServiceClient engine, PythonServiceClient exports, Func<string, CancellationToken, Task<PreparedLogoAsset>>? prepareLogo = null, StudioImportBackend? imports = null, StudioSnapshotWriter? recovery = null, Func<string, JsonObject>? readProject = null, StudioPreferenceStore? preferences = null, Func<string, int, System.Windows.Media.Imaging.BitmapSource>? loadFloorImage = null, Func<TextBox?>? focusedInput = null, Func<StockLayer, bool, string?>? pickLayerColor = null, Func<LogoImportWindow>? createLogoImporter = null, Func<LogoImportWindow, bool?>? showLogoImporter = null, Func<string, string?>? pickPrimaryColor = null, Func<TextureStudio.Models.TextLayerSettings, TextureStudio.Models.TextLayerSettings?>? pickTextSettings = null)
    {
        _engine = engine; _exports = exports; _prepareLogo = prepareLogo ?? PrepareLogoAsync;
        _focusedInput=focusedInput ?? (()=>Keyboard.FocusedElement as TextBox);
        _pickLayerColor=pickLayerColor ?? ShowLayerColorDialog;
        _pickPrimaryColor=pickPrimaryColor ?? (testing ? _ => null : ShowPrimaryColorDialog);
        _createLogoImporter=createLogoImporter ?? (()=>new LogoImportWindow(this));
        _showLogoImporter=showLogoImporter ?? (importer=>importer.ShowDialog());
        _loadFloorImage = loadFloorImage ?? StudioImages.Load;
        _usePairedFloorLoader=loadFloorImage is null;
        _readProject = readProject ?? StudioProjectStore.Read;
        _imports = imports ?? new StudioImportBackend(InspectImportAsync, RenderImportPreviewAsync);
        _testing = testing; _recovery = recovery ?? (testing ? null : new StudioSnapshotWriter(StudioProjectStore.WriteRecovery));
        _preferences = preferences ?? (testing ? null : new StudioPreferenceStore(StudioProjectStore.SettingsDirectory));
        InitializeComponent(); Style = (Style)FindResource(typeof(Window));
        ConfigureHardwoodTools();
        ConfigureArtworkActions();
        _pickTextSettings = pickTextSettings ?? (testing ? _ => null : ShowTextDialog);
        ConfigurePaintAndTextTools();
        if (!testing) { StudioWindowBounds.Attach(this); ContentRendered += async (_, _) => await Guard(InitializePortableAsync); }
        CourtCanvas.ShowGuides=false;CourtCanvas.SnapEnabled=false;
        CourtCanvas.TransformPreviewChanged+=(_,_)=>QueueLiveLogoFields();
        CourtCanvas.TransformPreviewEnded+=(_,_)=>FlushLiveLogoFields();
        CourtCanvas.TransformStarting+=(_,e)=>
        {
            if(!CanChangeDocument){e.Cancel=true;return;}
            FinishLogoOpacity();
            if(!CanChangeDocument)e.Cancel=true;
        };
        Closed+=(_,_)=>{ CancelLiveLogoFields(); CancelLayerHex(); };
        CourtCanvas.SelectionChanged += (_, _) => { RefreshLogoInspector(); RefreshToolState(); };
        CourtCanvas.ColorSampled += StoreSampledColor;
        CourtCanvas.TransformCommitted += (_, e) =>
        {
            var before = CreateProject();
            var logo = (before["logoImages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => String(item, "id") == e.Layer.Id);
            if (logo is not null) ApplyState(logo, e.Before);
            RecordUndo(before); Changed(); RefreshLogoInspector();
        };
        CourtCanvas.DeleteRequested += (_, _) => DeleteSelectedLogo();
        CourtCanvas.ViewChanged += (_, _) => ZoomText.Text = $"{CourtCanvas.Scale * 100:0}%";
        StateChanged += (_,_) => RefreshWindowStateIcon();

        RefreshWindowStateIcon();
        LogoList.ItemsSource = CourtCanvas.Layers;
        _recoveryTimer.Tick += (_, _) => { _recoveryTimer.Stop(); WriteRecovery(); };
        Closing += StudioClosing;
        Closed += (_, _) => { _closed = true; _floorPreparation?.Dispose(); CancelProjectRestore(); InvalidateFloorRequests(); InvalidateDocumentOperations(); CourtCanvas.CancelGesture(); _recoveryTimer.Stop(); _engine.Dispose(); _exports.Dispose(); };
        PreviewKeyDown += WindowKeyDown;
        SizeChanged += (_, _) => InspectorWidth.Width = new GridLength(400);
        var names = new[] { "Left", "Top", "Right", "Bottom" };
        for (var index = 0; index < 4; index++)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 6) };
            stack.Children.Add(new TextBlock { Text = names[index], FontSize = 12 });
            _edgeBoxes[index] = new TextBox(); stack.Children.Add(_edgeBoxes[index]); ImportBoundsHost.Children.Add(stack);
            _edgeBoxes[index].LostKeyboardFocus += async (_, _) => { if (!_syncing && _importSource is not null) await Guard(RefreshImportAsync); };
        }
        SwitchSection("paint");
        RefreshMutationState();
    }

    public Task InitializeAsync()
    {
        if (_closed) throw new ObjectDisposedException(nameof(StudioWindow));
        if (_ready) return Task.CompletedTask;
        if (_initializationTask is null || _initializationTask.IsFaulted || _initializationTask.IsCanceled)
            _initializationTask = InitializeWithRecoveryAsync();
        return _initializationTask;
    }

    private async Task InitializeWithRecoveryAsync()
    {
        StartupRetryButton.Visibility = Visibility.Collapsed;
        StartupRetryButton.IsEnabled = false;
        try { await InitializeCoreAsync(); }
        catch (Exception error)
        {
            if (!_closed) { SetStatus("Startup failed: " + error.Message); StartupRetryButton.Visibility = Visibility.Visible; StartupRetryButton.IsEnabled = true; }
            throw;
        }
    }
    private async void StartupRetryClick(object sender, RoutedEventArgs e) => await Guard(InitializeAsync);

    private async Task InitializeCoreAsync()
    {
        SetStatus("Loading Game UV court library...");
        var response = await _engine.RequestAsync(["load-stock"], TimeSpan.FromMinutes(4));
        if (_closed) throw new ObjectDisposedException(nameof(StudioWindow));
        var geometryRevision = String(response, "geometryRevision");
        if (!System.Text.RegularExpressions.Regex.IsMatch(geometryRevision, "\\A[0-9a-f]{64}\\z"))
            throw new InvalidDataException("The court engine returned an invalid geometry revision.");
        _geometryRevision = geometryRevision;
        _geometry = response["geometry"]!.AsObject(); _teams = response["teamPalettes"]!.AsArray();
        _courtSurface = Polygons(_geometry["gameUv"]!["courtSurfacePolygons"]!.AsArray());
        _loadedVisibility = response["visibility"]!.AsObject();
        var floors = StockFloor.ReadMany(response["customFloorImages"]!.AsArray().OfType<JsonObject>(), _engine.ProjectRoot);
        var paints = _geometry["paints"]!.AsArray().OfType<JsonObject>().Select(ReadLayer).ToArray();
        var lines = _geometry["layers"]!.AsArray().OfType<JsonObject>().Select(ReadLayer).ToArray();
        _floors.Clear(); _floors.AddRange(floors);
        _paints.Clear(); _paints.AddRange(paints);
        _lines.Clear(); _lines.AddRange(lines);
        var twoPointSurface = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var layer in paints) twoPointSurface.Children.Add(layer.Geometry);
        twoPointSurface.Freeze();
        PrepareThreePointSurfaces();
        var mainArea = Geometry.Combine(_courtSurface, twoPointSurface, GeometryCombineMode.Exclude, null);
        mainArea.Freeze();
        _paints.Add(new StockLayer { Id = "main-court-area", Name = "Three-Point Area", DefaultColor = "#19583F", Color = "#19583F", DefaultVisible = false, Visible = false, Geometry = mainArea });
        _defaultFloorId = _floors.FirstOrDefault(f => _loadedVisibility[f.Id]?.GetValue<bool>() == true)?.Id ?? _floors.FirstOrDefault()?.Id;
        CourtCanvas.Anchors = _geometry["guides"]?["game-uv"]?["anchors"]?.AsArray().OfType<JsonObject>()
            .Select(item => new StudioAnchor(String(item, "id"), String(item, "name"), new Point(Number(item["x"]), Number(item["y"])))) .ToArray() ?? [];
        _initialized = true;
        var preferencesWarning = await LoadPreferencesAsync();
        var recovery = _testing ? null : await Task.Run(() => StudioProjectStore.Recovery());
        string? recoveryWarning = null;
        if (recovery is not null)
        {
            try { await RestoreProjectAsync(recovery); }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
            { recoveryWarning = "Recovery could not be loaded: " + error.Message; await SelectFloorAsync(_floors.FirstOrDefault(f => f.Id == _defaultFloorId)); }
        }
        else await SelectFloorAsync(_floors.FirstOrDefault(f => f.Id == _defaultFloorId));
        if (_closed) throw new ObjectDisposedException(nameof(StudioWindow));
        RebuildLayerRows(); RefreshLogoInspector();
        _ready = true;
        RefreshMutationState();
        RefreshToolState(); RefreshSelectedColor(); RefreshHistoryState();
        SetStatus(recoveryWarning ?? preferencesWarning ?? (recovery is null ? "Studio ready." : "Project restored in the native studio."));
    }

    private static StockLayer ReadLayer(JsonObject item) => new()
    {
        Id = String(item, "id"), Name = String(item, "name"), DefaultVisible = item["visible"]!.GetValue<bool>(),
        Visible = item["visible"]!.GetValue<bool>(), DefaultColor = String(item, "color"), Color = String(item, "color"),
        Geometry = Polygons(item["gameUvPolygons"]!.AsArray())
    };
    private static Geometry Polygons(JsonArray polygons)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
            foreach (var polygon in polygons.OfType<JsonArray>())
            {
                if (polygon.Count < 3) continue;
                Point PointAt(JsonNode? point) => new(Number(point![0]), Number(point[1]));
                context.BeginFigure(PointAt(polygon[0]), true, true);
                context.PolyLineTo(polygon.Skip(1).Select(PointAt).ToArray(), true, false);
            }
        geometry.Freeze(); return geometry;
    }
    public async Task SelectFloorAsync(StockFloor? floor)
    {
        CommitHardwoodGesture();
        if (_restoring || _saving || PendingLogoImports > 0 || _closed || _closePending || _artworkEditorOpen) throw new InvalidOperationException("The court is not available for editing right now.");
        if (floor is null) throw new InvalidOperationException("No hardwood textures were found in the local library.");
        var revision = InvalidateFloorRequests();
        using var cancellation = new CancellationTokenSource();
        _floorSelectionCancellation = cancellation;
        try
        {
            var selected = floor;
            if (_floor is not null && floor.Id == _floor.Id && StringComparer.OrdinalIgnoreCase.Equals(floor.Path, _floor.Path))
            {
                var source = (JsonObject)floor.Source.DeepClone(); source["textureSettings"] = _mainHardwoodSettings.ToJson(); selected = floor with { Source = source };
            }
            var prepared = await PrepareFloorAsync(selected, cancellation.Token);
            if (revision != _floorRevision || cancellation.IsCancellationRequested || _closed || _closePending) return;
            ApplyFloor(prepared); RebuildBackground();
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_floorSelectionCancellation, cancellation)) _floorSelectionCancellation = null; }
    }
    private Drawing? _hardwoodDrawing;
    private void RebuildBackground()
    {
        RefreshPaintRegionGeometries();
        if (_section == "import" && _importDrawing is not null) { CourtCanvas.BackgroundDrawing = _importDrawing; return; }
        var group = new DrawingGroup();
        if (_outside.Visible) group.Children.Add(new GeometryDrawing(StudioImages.Brush(_outside.Color), null, new RectangleGeometry(new Rect(0, 0, 8192, 4096))));
        if (_hardwoodDrawing is not null) group.Children.Add(_hardwoodDrawing);
        if (_twoPointHardwoodEnabled && _twoPointDrawing is not null)
        {
            var secondary = new DrawingGroup { ClipGeometry = CurrentTwoPointSurface() };
            secondary.Children.Add(_twoPointDrawing); secondary.Freeze(); group.Children.Add(secondary);
        }
        // Paint regions partition the court. Rasterize equal-color neighbors together so their
        // shared boundary is internal to one fill, rather than two independently antialiased edges.
        foreach (var paintGroup in _paints.Where(layer => layer.Visible).GroupBy(layer => layer.Color, StringComparer.OrdinalIgnoreCase))
        {
            var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var layer in paintGroup) geometry.Children.Add(PaintRegionGeometry(layer));
            geometry.Freeze(); group.Children.Add(new GeometryDrawing(StudioImages.Brush(paintGroup.Key), null, geometry));
        }
        foreach (var layer in _lines.Where(layer => layer.Visible)) group.Children.Add(new GeometryDrawing(StudioImages.Brush(layer.Color), null, layer.Geometry));
        group.Freeze(); CourtCanvas.BackgroundDrawing = group;
    }
    public void SwitchSection(string section)
    {
        CommitHardwoodGesture();
        if (section == "floors") section = "paint";
        CourtCanvas.CancelGesture(); _section = section;
        Inspector.Visibility = Visibility.Visible;
        InspectorWidth.Width = new GridLength(400);
        InspectorGap.Width = new GridLength(0);
        PaintPanel.Visibility = section == "paint" ? Visibility.Visible : Visibility.Collapsed;
        LogoPanel.Visibility = section == "logos" ? Visibility.Visible : Visibility.Collapsed;
        ImportPanel.Visibility = section == "import" ? Visibility.Visible : Visibility.Collapsed;
        ExportPanel.Visibility = section == "export" ? Visibility.Visible : Visibility.Collapsed;
        CourtCanvas.ShowArtwork = section != "import";
        foreach (var button in new[] { PaintButton, LogosButton, ImportButton, ExportButton })
            {
            var selected = (string)button.Tag == section;
            button.SetResourceReference(Control.BackgroundProperty, selected ? "AccentDarkBrush" : "PanelBrush");
            button.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentBrightBrush" : "BorderBrush");
        }
        if (section == "import" && CourtCanvas.Tool is ArtworkTool.Transform or ArtworkTool.Eyedropper or ArtworkTool.Bucket or ArtworkTool.Type) CourtCanvas.Tool = ArtworkTool.Move;
        RefreshToolState(); RefreshSelectedColor();
        RebuildBackground(); CourtCanvas.InvalidateVisual();
    }

    public JsonObject CreateProject()
    {
        var visibility = (JsonObject)(_loadedVisibility?.DeepClone() ?? new JsonObject());
        foreach (var floor in _floors) visibility[floor.Id] = floor.Id == _floor?.Id;
        foreach (var key in visibility.Select(pair => pair.Key).Where(key => key.StartsWith("floor_template_category_")).ToArray()) visibility[key] = true;
        var colors = new JsonObject(); var names = new JsonObject();
        foreach (var layer in _paints.Concat(_lines).Append(_outside))
        {
            visibility[layer.Id] = layer.Visible; names[layer.Id] = layer.Name;
            var color = (Color)ColorConverter.ConvertFromString(layer.Color);
            colors[layer.Id] = new JsonArray((int)color.R, (int)color.G, (int)color.B);
        }
        JsonObject Settings(IEnumerable<StockLayer> layers) => new(layers.Select(layer => KeyValuePair.Create<string, JsonNode?>(layer.Id,
            new JsonObject { ["visible"] = layer.Visible, ["color"] = layer.Color })));
        var floorData = (JsonObject)(_floor?.Source.DeepClone() ?? new JsonObject());
        if (_floor is not null) { floorData["path"] = _floor.Path; floorData["name"] = _floor.Name; }
        if (_floorSourceRevision is not null) floorData["sourceRevision"] = _floorSourceRevision;
        floorData["textureSettings"] = _mainHardwoodSettings.ToJson();
        return new JsonObject { ["version"] = 2, ["buildMode"] = "game-uv", ["mappingMode"] = "game-uv",
            ["floor"] = floorData, ["twoPointFloor"] = TwoPointFloorSnapshot(), ["twoPointHardwoodEnabled"] = _twoPointHardwoodEnabled, ["outsideColor"] = _outside.Color, ["outsideVisible"] = _outside.Visible,
            ["visibility"] = visibility, ["colorOverrides"] = colors, ["layerNames"] = names,
            ["paintSettings"] = Settings(_paints), ["lineSettings"] = Settings(_lines),
            ["logoImages"] = new JsonArray(CourtCanvas.Layers.Select(SerializeLogo).Cast<JsonNode?>().ToArray()),
            ["customFloorImages"] = new JsonArray(_floors.Where(f => f.Category == "Custom").Select(f => (JsonNode?)new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["path"] = f.Path, ["visible"] = f.Id == _floor?.Id }).ToArray()),
            ["_projectPath"] = _projectPath, ["projectName"] = _projectName, ["suite"] = new JsonObject { ["app"] = "court-creator", ["editor"] = "TwoK.Studio", ["version"] = 1 } };
    }
    public Task RestoreProjectAsync(JsonObject project)
    {
        var snapshot = (JsonObject)project.DeepClone();
        return RestoreProjectFromAsync(() => Task.FromResult(snapshot));
    }
    private CancellationTokenSource? _projectRestoreCancellation;
    private List<PreparedLogoAsset>? _projectRestoreAssets;
    private bool _historyRestoring;
    private void CancelProjectRestore()
    {
        _projectRestoreCancellation?.Cancel();
        // Closing may stop the dispatcher before a blocked load can resume its finally block.
        if (_projectRestoreAssets is not null) foreach (var asset in _projectRestoreAssets) asset.Dispose();
    }
    private async Task RestoreProjectFromAsync(Func<Task<JsonObject>> load, bool opened = false, bool history = false)
    {
        if (_restoring || _saving || _catalogBusy || _closed || _closePending || _artworkEditorOpen) throw new InvalidOperationException("A court operation is already in progress.");
        CommitHardwoodGesture(); ++_hardwoodPreviewRevision; _hardwoodPreviewTimer.Stop();
        CourtCanvas.CancelGesture(); FinishLogoOpacity();
        using var cancellation = new CancellationTokenSource();
        _projectRestoreCancellation = cancellation;
        _restoring = true; _historyRestoring = history; InvalidateFloorRequests(); InvalidateDocumentOperations(); _recoveryTimer.Stop();
        if (!history) RefreshToolState(); RefreshMutationState();
        SetStatus(opened ? "Opening project..." : "Preparing the court project...");
        var preparedAssets = new List<PreparedLogoAsset>();
        _projectRestoreAssets = preparedAssets;
        var unchangedLogos = false;
        try
        {
            var project = await load();
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Run(() => StudioProjectValidation.Validate(project), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var projectPath = LocalProjectPath(project["_projectPath"]?.GetValue<string>());
            var projectRelative = String(project, "assetPathMode") == "project-relative";
            unchangedLogos = history && JsonNode.DeepEquals(new JsonArray(CourtCanvas.Layers.Select(SerializeLogo).Cast<JsonNode?>().ToArray()), project["logoImages"]);
            var preparedLogos = new List<ArtworkLayer>();
            var preparedArtwork = new Dictionary<string, JsonObject>();
            var missing = new List<string>();
            var updatedAssets = new List<string>();
            foreach (var item in (project["logoImages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var path = ResolvePath(String(item, "path"), projectPath, projectRelative);
                var existing = history ? CourtCanvas.Layers.FirstOrDefault(layer => layer.Id == String(item, "id") && layer.Image is not null
                    && HistoryArtworkMatches(item, path, SerializeLogo(layer), layer.Path, StudioImages.SourceRevision(layer.Image))) : null;
                var artwork = existing is not null ? _logoArtwork.GetValueOrDefault(existing.Id)?.DeepClone() as JsonObject
                    : await Task.Run(() => RestoreArtworkReference(item, projectPath, projectRelative), cancellation.Token);
                var logo = new ArtworkLayer { Id = String(item, "id", Guid.NewGuid().ToString("N")), Name = String(item, "name", "Logo"), Path = path,
                    X = Number(item["x"]), Y = Number(item["y"]), Width = Number(item["width"], 400), Height = Number(item["height"], 400), Rotation = Number(item["rotation"]),
                    Opacity = Number(item["opacity"], 100), Visible = item["visible"]?.GetValue<bool>() ?? true, ScaleLocked = item["scaleLocked"]?.GetValue<bool>() ?? true,
                    FlipX = item["flipX"]?.GetValue<bool>() ?? false, FlipY = item["flipY"]?.GetValue<bool>() ?? false };
                try
                {
                    if (existing is not null) logo.Image = existing.Image;
                    else
                    {
                        var asset = await _prepareLogo(path, cancellation.Token);
                        preparedAssets.Add(asset); logo.Image = artwork?["artworkAlphaMode"]?.GetValue<string>() == "GameData"
                            ? await Task.Run(() => StudioArtworkPreview.Load(asset.Path, artwork), cancellation.Token) : asset.Image;
                        logo.Path = asset.Path;
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (item["sourceRevision"]?.GetValue<string>() is { } savedRevision && savedRevision != StudioImages.SourceRevision(logo.Image))
                        updatedAssets.Add(logo.Name);
                }
                catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException) { missing.Add(logo.Name); }
                preparedLogos.Add(logo);
                if (artwork is not null) preparedArtwork[logo.Id] = artwork;
            }
            var visibility = project["visibility"] as JsonObject;
            var floorId = project["floor"]?["id"]?.GetValue<string>() ?? _floors.FirstOrDefault(f => visibility?[f.Id]?.GetValue<bool>() == true)?.Id ?? _defaultFloorId;
            var customFloors = new List<StockFloor>();
            StockFloor ReadSavedFloor(JsonObject source, string path, bool custom)
            {
                var restored = (JsonObject)source.DeepClone(); restored["path"] = path; restored["previewPath"] = path;
                var retained = history ? HistoryFloor(source, path) : null;
                if ((retained is not null ? retained.Floor.Source : RestoreArtworkReference(source, projectPath, projectRelative)) is { } artwork)
                    // A retained preview already owns the exact revision and resolved artwork references.
                    // Copy only artwork fields, not its old texture settings or catalog metadata.
                    foreach (var item in artwork.Where(item => item.Key.StartsWith("artwork", StringComparison.Ordinal) || item.Key == "textSettings")) restored[item.Key] = item.Value?.DeepClone();
                if (custom) restored["category"] = "Custom";
                restored["id"] ??= "custom_floor_" + Guid.NewGuid().ToString("N"); restored["name"] ??= Path.GetFileNameWithoutExtension(path);
                return StockFloor.Read(restored, _engine.ProjectRoot, custom ? null : _floors);
            }
            foreach (var item in (project["customFloorImages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (String(item, "path") is not { Length: > 0 } savedPath) continue;
                var path = ResolvePath(savedPath, projectPath, projectRelative);
                var custom = ReadSavedFloor(item, path, true); customFloors.Add(custom);
                if (!File.Exists(path)) missing.Add(custom.Name);
            }
            var selectedFloor = customFloors.FirstOrDefault(f => f.Id == floorId) ?? _floors.FirstOrDefault(f => f.Id == floorId);
            if (project["floor"] is JsonObject savedFloor && savedFloor["path"] is JsonNode savedPathNode)
            {
                var path = ResolvePath(savedPathNode.GetValue<string>(), projectPath, projectRelative);
                var matchesLibraryPath = selectedFloor is not null && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(selectedFloor.Path));
                var savedPathExists = File.Exists(path);
                if (savedPathExists && (projectRelative || !matchesLibraryPath))
                    selectedFloor = ReadSavedFloor(savedFloor, path, false);
                else if (savedPathExists && selectedFloor is not null)
                    // Preserve the snapshot metadata rather than rebuilding it from a custom
                    // catalog entry (which adds visibility fields during an undo).
                    selectedFloor = selectedFloor with { Source = (JsonObject)savedFloor.DeepClone() };
                else if (!savedPathExists && selectedFloor is not null && (projectRelative || !matchesLibraryPath))
                    missing.Add((projectRelative ? "bundled" : "saved") + " hardwood (using the local library copy)");
            }
            if (selectedFloor is null && floorId != _defaultFloorId) missing.Add("hardwood (using the default court)");
            if (selectedFloor is not null && project["floor"]?["textureSettings"] is { } savedTextureSettings)
            {
                var source = (JsonObject)selectedFloor.Source.DeepClone(); source["textureSettings"] = savedTextureSettings.DeepClone();
                selectedFloor = selectedFloor with { Source = source };
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var preparedFloor = await PrepareRestoredFloorAsync(selectedFloor ?? _floors.First(f => f.Id == _defaultFloorId), history, cancellation.Token);
            if (project["floor"]?["sourceRevision"]?.GetValue<string>() is { } floorRevision && floorRevision != preparedFloor.SourceRevision)
                updatedAssets.Add("hardwood");
            PreparedFloor? preparedTwoPoint = null;
            if (project["twoPointFloor"] is JsonObject savedTwoPoint)
            {
                var savedId = String(savedTwoPoint, "id");
                var path = ResolvePath(String(savedTwoPoint, "path"), projectPath, projectRelative);
                var secondary = File.Exists(path) ? ReadSavedFloor(savedTwoPoint, path, false)
                    : customFloors.Concat(_floors).FirstOrDefault(floor => floor.Id == savedId && File.Exists(floor.Path));
                if (secondary is null) missing.Add("two-point hardwood (using main hardwood)");
                else
                {
                    preparedTwoPoint = await PrepareRestoredFloorAsync(secondary, history, cancellation.Token);
                    if (savedTwoPoint["sourceRevision"]?.GetValue<string>() is { } secondaryRevision && secondaryRevision != preparedTwoPoint.SourceRevision)
                        updatedAssets.Add("two-point hardwood");
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            // Load and validate assets first. A failed read must not replace the open document.
            _syncing = true;
            FinishRename(false);
            var layerNamesChanged = false;
            foreach (var layer in _paints.Concat(_lines).Append(_outside))
            {
                var settings = (project["paintSettings"] as JsonObject)?[layer.Id] ?? (project["lineSettings"] as JsonObject)?[layer.Id];
                layer.Visible = settings?["visible"]?.GetValue<bool>() ?? visibility?[layer.Id]?.GetValue<bool>() ?? layer.DefaultVisible;
                var rgb = project["colorOverrides"]?[layer.Id] as JsonArray;
                layer.Color = StudioImages.Hex(settings?["color"]?.GetValue<string>()) ?? (rgb?.Count >= 3 ? $"#{(int)Number(rgb[0]):X2}{(int)Number(rgb[1]):X2}{(int)Number(rgb[2]):X2}" : layer.DefaultColor);
                var restoredName = project["layerNames"]?[layer.Id]?.GetValue<string>() ?? layer.Name;
                layerNamesChanged |= restoredName != layer.Name; layer.Name = restoredName;
            }
            if (Number(project["version"]) == 1) RestoreLegacyColors(project);
            _outside.Color = StudioImages.Hex(project["outsideColor"]?.GetValue<string>()) ?? _outside.Color;
            _outside.Visible = project["outsideVisible"]?.GetValue<bool>() ?? _outside.Visible;
            if (!unchangedLogos)
            {
                CourtCanvas.Layers.Clear();
                _logoArtwork.Clear(); foreach (var item in preparedArtwork) _logoArtwork[item.Key] = item.Value;
                foreach (var logo in preparedLogos) CourtCanvas.Layers.Add(logo);
            }
            foreach (var custom in customFloors)
            {
                var index = _floors.FindIndex(f => f.Id == custom.Id && f.Category == "Custom");
                if (index >= 0) _floors[index] = custom;
                else if (!_floors.Any(f => f.Id == custom.Id)) _floors.Add(custom);
            }
            if (!_floors.Any(f => f.Id == preparedFloor.Floor.Id)) _floors.Add(preparedFloor.Floor);
            _projectPath = projectPath;
            ApplyFloor(preparedFloor); ApplyTwoPointFloor(preparedTwoPoint, project["twoPointHardwoodEnabled"]?.GetValue<bool>() ?? preparedTwoPoint is not null); RebuildBackground();
            if (!unchangedLogos) CourtCanvas.SelectedLayer = CourtCanvas.Layers.FirstOrDefault();
            _projectName = project["projectName"]?.GetValue<string>() is { } savedName && !string.IsNullOrWhiteSpace(savedName) ? savedName : _projectPath is null ? "Untitled court" : Path.GetFileNameWithoutExtension(_projectPath); UpdateProjectLabel();
            _dirty = updatedAssets.Count > 0;
            if (history && !layerNamesChanged)
            {
                CancelLayerHex(); foreach (var layer in _paints.Concat(_lines).Append(_outside)) RefreshLayerRow(layer);
                FlushLayerHex(); RefreshSelectedColor();
            }
            else RebuildLayerRows();
            var notices = new List<string>();
            if (missing.Count > 0) notices.Add("Missing assets: " + string.Join(", ", missing));
            if (updatedAssets.Count > 0) notices.Add("Updated artwork loaded: " + string.Join(", ", updatedAssets));
            SetStatus(notices.Count == 0 ? "Project restored." : "Project restored. " + string.Join(". ", notices));
            foreach (var asset in preparedAssets) asset.Commit();
            if (opened)
            { ResetImportContext(); _undo.Clear(); _redo.Clear(); RefreshHistoryState(); }
        }
        catch (Exception error) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && !_closePending) SetStatus("Project load canceled; the current court was kept.");
            throw new OperationCanceledException("Project loading was canceled.", error, cancellation.Token);
        }
        catch (Exception error) { if (!_closed) SetStatus("Project could not be loaded: " + error.Message); throw; }
        finally
        {
            foreach (var asset in preparedAssets) asset.Dispose();
            _projectRestoreCancellation = null; _projectRestoreAssets = null; _syncing = false; _restoring = false; _historyRestoring = false;
            if (!_closed) { RefreshMutationState(); if (_dirty && _recovery is not null && !_closePending) _recoveryTimer.Start(); }
        }
        if (unchangedLogos) RefreshToolState(); else RefreshLogoInspector();
        if (opened) WriteRecovery();
    }
    public async Task NewProjectAsync()
    {
        CommitHardwoodGesture();
        var before = CreateProject();
        var reset = (JsonObject)before.DeepClone();
        foreach (var layer in _paints.Concat(_lines))
        {
            var settings = reset[_paints.Contains(layer) ? "paintSettings" : "lineSettings"]![layer.Id]!;
            settings["visible"] = layer.DefaultVisible; settings["color"] = layer.DefaultColor;
        }
        reset["outsideColor"] = _outside.DefaultColor; reset["outsideVisible"] = _outside.DefaultVisible;
        reset["floor"] = _floors.First(f => f.Id == _defaultFloorId).Source.DeepClone();
        reset["twoPointFloor"] = null;
        reset["twoPointHardwoodEnabled"] = false;
        reset["logoImages"] = new JsonArray(); reset["_projectPath"] = null; reset["projectName"] = "Untitled court";
        await RestoreProjectAsync(reset); ResetImportContext(); RecordUndo(before); Changed(); SwitchSection("paint"); CourtCanvas.Fit();
    }
    private JsonObject SerializeLogo(ArtworkLayer layer)
    {
        var result = new JsonObject { ["id"] = layer.Id, ["name"] = layer.Name, ["path"] = layer.Path,
            ["x"] = layer.X, ["y"] = layer.Y, ["width"] = layer.Width, ["height"] = layer.Height, ["rotation"] = layer.Rotation,
            ["opacity"] = layer.Opacity, ["visible"] = layer.Visible, ["scaleLocked"] = layer.ScaleLocked, ["flipX"] = layer.FlipX, ["flipY"] = layer.FlipY };
        if (StudioImages.SourceRevision(layer.Image) is { } revision) result["sourceRevision"] = revision;
        if (_logoArtwork.TryGetValue(layer.Id, out var artwork))
            foreach (var item in artwork) result[item.Key] = item.Value?.DeepClone();
        return result;
    }
    private static void ApplyState(JsonObject item, ArtworkState state) { item["x"] = state.X; item["y"] = state.Y; item["width"] = state.Width; item["height"] = state.Height; item["rotation"] = state.Rotation; }
    private void RecordUndo(JsonObject before) { _undo.Add(before); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); RefreshHistoryState(); }
    public void SetLayerSettings(string id, bool? visible = null, string? color = null)
    {
        var layer = _paints.Concat(_lines).Append(_outside).First(item => item.Id == id);
        ApplyLayerSettings(layer, visible, color);
    }
    private void ApplyLayerSettings(StockLayer layer, bool? visible, string? color, Func<bool>? ownerCurrent = null)
    {
        var version = _documentVersion;
        bool Current() => CanChangeDocument && version == _documentVersion && (ownerCurrent?.Invoke() ?? true)
            && _paints.Concat(_lines).Append(_outside).Any(item => ReferenceEquals(item, layer));
        var hex = color is null ? null : StudioImages.Hex(color) ?? throw new ArgumentException("Invalid hex color.", nameof(color));
        if (!Current()) return;
        var changed = (visible.HasValue && visible.Value != layer.Visible) || (hex is not null && !StringComparer.OrdinalIgnoreCase.Equals(hex, layer.Color));
        if (changed)
        {
            CourtCanvas.CancelGesture();
            if (!Current()) return;
            FinishLogoOpacity();
            if (!Current()) return;
            var before = CreateProject();
            if (visible.HasValue) layer.Visible = visible.Value;
            if (hex is not null) layer.Color = hex;
            RecordUndo(before); Changed(); RefreshLayerRow(layer);
        }
        var selectionChanged = _colorLayerId != layer.Id;
        _colorLayerId = layer.Id;
        if (selectionChanged) RefreshLayerSelection();
        if (selectionChanged || changed && visible.HasValue) RefreshToolState();
        if (changed || selectionChanged) RefreshSelectedColor();
    }
    private bool CanChangeDocument => _initialized && _ready && !_syncing && !_restoring && !_saving && !_catalogBusy && !_closed && !_closePending && !_artworkEditorOpen;
    private bool Change(Action change)
    {
        CommitHardwoodGesture();
        if(!CanChangeDocument)return false;
        CourtCanvas.CancelGesture();
        if(!CanChangeDocument)return false;
        FinishLogoOpacity();
        if(!CanChangeDocument)return false;
        var before=CreateProject();change();
        if(before.ToJsonString()!=CreateProject().ToJsonString()){RecordUndo(before);Changed();}
        return true;
    }
    private void Changed() { _dirty = true; RebuildBackground(); CourtCanvas.InvalidateVisual(); if (_recovery is not null && !_closePending) { _recoveryTimer.Stop(); _recoveryTimer.Start(); } }
    public async Task UndoAsync(bool redo = false)
    {
        CommitHardwoodGesture();
        if(!CanChangeDocument)throw new InvalidOperationException("A court operation is already in progress.");
        CourtCanvas.CancelGesture();
        if(!CanChangeDocument)throw new InvalidOperationException("A court operation is already in progress.");
        FinishLogoOpacity();
        if(!CanChangeDocument)throw new InvalidOperationException("A court operation is already in progress.");
        var source = redo ? _redo : _undo; var destination = redo ? _undo : _redo;
        if (source.Count == 0) return; var target = source[^1]; var before = CreateProject();
        var snapshot = (JsonObject)target.DeepClone();
        await RestoreProjectFromAsync(() => Task.FromResult(snapshot), history: true); source.RemoveAt(source.Count - 1); destination.Add(before); Changed(); RefreshHistoryState(); SetStatus(redo ? "Redo applied." : "Undo applied.");
    }
    private void SetStatus(string text) => StatusText.Text = text;
    private async Task Guard(Func<Task> work) { try { await work(); } catch (OperationCanceledException error) when (error.CancellationToken.IsCancellationRequested) { } catch (Exception error) { if (_closed) return; SetStatus(error.Message); if (!_testing) MessageBox.Show(this, error.Message, "Court Creator", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private static string String(JsonObject item, string key, string fallback = "") => item[key]?.GetValue<string>() ?? fallback;
    private static double Number(JsonNode? node, double fallback = 0) => double.TryParse(node?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    private string ResolvePath(string path, string? projectPath = null, bool projectRelative = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!Path.IsPathRooted(path))
        {
            var local = Path.GetFullPath(path, _engine.ProjectRoot);
            if (projectPath is not null)
            {
                var adjacent = Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
                if (projectRelative || !File.Exists(local) && File.Exists(adjacent)) return adjacent;
            }
            return local;
        }
        if (File.Exists(path)) return Path.GetFullPath(path);
        foreach (var marker in new[] { "\\assets\\", "\\custom_floors\\", "\\logos\\" })
        { var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (index >= 0) { var local = Path.Combine(_engine.ProjectRoot, path[(index + 1)..]); if (File.Exists(local)) return local; } }
        return path;
    }
    private string? LocalProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "NBA 2K Court Creator");
        return path.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Path.Combine(_engine.ProjectRoot, path[(old.Length + 1)..]) : path;
    }
    private void RestoreLegacyColors(JsonObject project)
    {
        var aliases = new Dictionary<string, string[]> {
            ["paint"] = ["paint-left", "paint-right"], ["secondary paint color"] = ["secondary-paint-left", "secondary-paint-right"],
            ["outside color"] = ["stock-outside"], ["nba three"] = ["NBA_line_three_point_lowShape"], ["3 point lines"] = ["NBA_line_three_point_lowShape"],
            ["college three"] = ["college-three"], ["high school three"] = ["high-school-three"],
            ["center line"] = ["line_midcourt_side_lowShape", "line_midcourt_center_lowShape"], ["out of bound line"] = ["line_side_base_lowShape"],
            ["half court circle"] = ["line_center_circle_outer_lowShape"], ["charge circle"] = ["line_charge_circle_lowShape"], ["media lines"] = ["line_camera_lowShape"] };
        foreach (var entry in project["layerNames"]?.AsObject() ?? new JsonObject())
        {
            var name = entry.Value?.GetValue<string>().Trim().ToLowerInvariant() ?? "";
            if (!aliases.TryGetValue(name, out var ids)) continue;
            foreach (var id in ids)
            {
                var layer = _paints.Concat(_lines).Append(_outside).First(item => item.Id == id);
                if (project["visibility"]?[entry.Key] is JsonNode visible) layer.Visible = visible.GetValue<bool>();
                if (project["colorOverrides"]?[entry.Key] is JsonArray rgb && rgb.Count >= 3) layer.Color = $"#{(int)Number(rgb[0]):X2}{(int)Number(rgb[1]):X2}{(int)Number(rgb[2]):X2}";
            }
        }
    }
}
