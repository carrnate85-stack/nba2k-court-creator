using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static bool _allowNativeWindows;
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _allowNativeWindows = args.Contains("--native-windows");
        StudioTheme.Apply(false);
        var frame = new DispatcherFrame(); Exception? failure = null;
        application.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var output = args.FirstOrDefault(arg => !arg.StartsWith("--"));
                if (args.Contains("--benchmark")) await Benchmark(output ?? "outputs/native-performance.json");
                else if (args.Contains("--artwork-editor"))
                {
                    output ??= "outputs/artwork-editor-check";
                    Directory.CreateDirectory(output);
                    await CheckArtworkEditors(output);
                }
                else if (args.Contains("--logo-actions"))
                {
                    output ??= "outputs/logo-actions-check";
                    Directory.CreateDirectory(output);
                    await CheckLogoActions(output);
                    var logoWindow=new StudioWindow(true);
                    try { await logoWindow.InitializeAsync();await CheckLogoCanvasStyle(logoWindow,output); }
                    finally { logoWindow.Close(); }
                }
                else if (args.Contains("--live-transforms"))
                {
                    output ??= "outputs/live-transform-check";
                    Directory.CreateDirectory(output);
                    var transformWindow=new StudioWindow(true);
                    try { await transformWindow.InitializeAsync();await CheckLiveResize(transformWindow,output); }
                    finally { transformWindow.Close(); }
                }
                else if (args.Contains("--shared-controls"))
                {
                    output ??= "outputs/shared-controls-check";
                    Directory.CreateDirectory(output);
                    await CheckSharedControls(output);
                }
                else if (args.Contains("--request-files"))
                {
                    output ??= "outputs/request-files-check";
                    Directory.CreateDirectory(output);
                    await CheckWorkerRequestFiles(output);
                }
                else if (args.Contains("--portable-assets"))
                {
                    output ??= "outputs/portable-assets-check";
                    Directory.CreateDirectory(output);
                    CheckPortableAssetPublication(output);
                }
                else if (args.Contains("--preview-files"))
                {
                    output ??= "outputs/preview-files-check";
                    Directory.CreateDirectory(output);
                    CheckImportPreviewFiles(output);
                }
                else if (args.Contains("--managed-logos"))
                {
                    output ??= "outputs/managed-logo-check";
                    Directory.CreateDirectory(output);
                    await CheckManagedLogos(output);
                }
                else if (args.Contains("--logo-importers"))
                {
                    output ??= "outputs/logo-importer-check";
                    Directory.CreateDirectory(output);
                    await CheckLogoImporters(output);
                }
                else if (args.Contains("--color-pickers"))
                {
                    output ??= "outputs/color-picker-check";
                    Directory.CreateDirectory(output);
                    CheckWindowBounds(output);
                    await CheckColorPickers(output);
                }
                else if (args.Contains("--paired-images"))
                {
                    output ??= "outputs/paired-image-check";
                    Directory.CreateDirectory(output);
                    await CheckPairedImages(output);
                }
                else if (args.Contains("--focused-close"))
                {
                    output ??= "outputs/focused-close-check";
                    Directory.CreateDirectory(output);
                    await CheckFocusedClose(output);
                }
                else if (args.Contains("--focused-exports"))
                {
                    output ??= "outputs/focused-export-check";
                    Directory.CreateDirectory(output);
                    await CheckFocusedExport(output);
                }
                else if (args.Contains("--focused-save"))
                {
                    output ??= "outputs/focused-save-check";
                    Directory.CreateDirectory(output);
                    await CheckFocusedSave(output);
                }
                else if (args.Contains("--pointer-safety"))
                {
                    CheckPointerSafety();
                }
                else if (args.Contains("--edit-boundaries"))
                {
                    output ??= "outputs/edit-boundary-check";
                    Directory.CreateDirectory(output);
                    await CheckEditBoundaries(output);
                }
                else if (args.Contains("--inspector-events"))
                {
                    output ??= "outputs/inspector-event-check";
                    Directory.CreateDirectory(output);
                    await CheckInspectorEvents(output);
                }
                else if (args.Contains("--keyboard-commands"))
                {
                    output ??= "outputs/keyboard-command-check";
                    Directory.CreateDirectory(output);
                    await CheckKeyboardCommands(output);
                }
                else if (args.Contains("--canvas-gestures"))
                {
                    output ??= "outputs/canvas-gesture-check";
                    Directory.CreateDirectory(output);
                    await CheckCanvasGestures(output);
                }
                else if (args.Contains("--layer-edits"))
                {
                    output ??= "outputs/layer-edit-check";
                    Directory.CreateDirectory(output);
                    await CheckLayerEdits(output);
                }
                else if (args.Contains("--window-bounds"))
                {
                    output ??= "outputs/window-bounds-check";
                    Directory.CreateDirectory(output);
                    CheckWindowBounds(output);
                }
                else if (args.Contains("--floor-requests"))
                {
                    output ??= "outputs/floor-requests-check";
                    Directory.CreateDirectory(output);
                    await CheckFloorRequests(output);
                }
                else if (args.Contains("--floor-names"))
                {
                    output ??= "outputs/floor-names-check";
                    Directory.CreateDirectory(output);
                    await CheckFloorNames(output);
                }
                else if (args.Contains("--catalog"))
                {
                    output ??= "outputs/catalog-check";
                    Directory.CreateDirectory(output);
                    await CheckFloorCatalog(output);
                }
                else if (args.Contains("--preferences"))
                {
                    output ??= "outputs/preferences-check";
                    Directory.CreateDirectory(output);
                    await CheckPreferencesPersistence(output);
                }
                else if (args.Contains("--recovery"))
                {
                    output ??= "outputs/recovery-check";
                    Directory.CreateDirectory(output);
                    await CheckRecovery(output);
                }
                else if (args.Contains("--project-open"))
                {
                    output ??= "outputs/project-open-check";
                    Directory.CreateDirectory(output);
                    await CheckProjectOpen(output);
                }
                else if (args.Contains("--staging"))
                {
                    output ??= "outputs/native-staging-check";
                    Directory.CreateDirectory(output);
                    await CheckProjectStaging(output);
                }
                else if (args.Contains("--project-files"))
                {
                    output ??= "outputs/project-publication-check";
                    Directory.CreateDirectory(output);
                    await CheckProjectFileSafety(output);
                    await CheckProjectPublication(output);
                    await CheckProjectStaging(output);
                }
                else await Check(output ?? "outputs/wpf-studio-check");
            }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame); application.Shutdown();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Layout(StudioWindow window, int width, int height)
    {
        window.Width = width; window.Height = height;
        window.SwitchSection(window.Section);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
    }
    private static void Snapshot(StudioWindow window, string path, int width, int height)
    {
        Layout(window, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
        var screen = window.Canvas.ToScreen(new Point(4096, 2048));
        var canvas = new RenderTargetBitmap(Math.Max(1, (int)window.Canvas.ActualWidth), Math.Max(1, (int)window.Canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        canvas.Render(window.Canvas); var pixel = new byte[4];
        canvas.CopyPixels(new Int32Rect((int)screen.X, (int)screen.Y, 1, 1), pixel, 4, 0);
        Assert(pixel[3] > 250 && pixel[2] > 100, "Court canvas rendered blank.");
    }
    private static async Task Check(string output)
    {
        Directory.CreateDirectory(output);
        CheckCleanup();
        await CheckProjectFileSafety(output);
        await CheckProjectPublication(output);
        await CheckProjectStaging(output);
        await CheckRecovery(output);
        await CheckProjectOpen(output);
        await CheckWorkerRecovery();
        await CheckWorkerRequestFiles(output);
        CheckBitmapCache();
        await CheckPairedImages(output);
        await CheckSharedControls(output);
        await CheckColorPickers(output);
        await CheckLogoImporters(output);
        await CheckManagedLogos(output);
        CheckWindowBounds(output);
        await CheckFloorRequests(output);
        await CheckFloorNames(output);
        await CheckFloorCatalog(output);
        await CheckPreferences(output);
        await CheckPreferencesPersistence(output);
        await CheckStartupRetry(output);
        await CheckPortableProjects(output);
        CheckPortableAssetPublication(output);
        await CheckPendingArtwork(output);
        await CheckImportRequests(output);
        CheckImportPreviewFiles(output);
        await CheckExportSafety(output);
        await CheckAssetRevisions(output);
        await CheckLargeLogoExport(output);
        await CheckLayerEdits(output);
        await CheckCanvasGestures(output);
        await CheckKeyboardCommands(output);
        await CheckInspectorEvents(output);
        await CheckEditBoundaries(output);
        CheckPointerSafety();
        await CheckFocusedSave(output);
        await CheckFocusedExport(output);
        await CheckFocusedClose(output);
        await CheckArtworkEditors(output);
        var window = new StudioWindow(true);
        try
        {
            var initializing = window.InitializeAsync();
            Assert(ReferenceEquals(initializing, window.InitializeAsync()), "Concurrent startup calls did not share initialization.");
            await initializing;
            var initialCounts = (window.Floors.Count, window.PaintLayers.Count, window.LineLayers.Count);
            await window.InitializeAsync();
            Assert(initialCounts == (window.Floors.Count, window.PaintLayers.Count, window.LineLayers.Count), "Repeated initialization duplicated catalog entries.");
            if (_allowNativeWindows) await CheckPolish(window, output);
            else Console.WriteLine("Native window/modal interaction checks skipped: all verification remains off-screen.");
            await CheckProjectSafety(window, output);
            await CheckCurrentFixes(window, output);
            await CheckLiveResize(window, output);
            await CheckLogoActions(output);
            await CheckLogoCanvasStyle(window, output);
            Assert(window.Floors.Count > 200, "Stock floor library did not load.");
            Assert(window.LineLayers.Count == 16 && window.PaintLayers.Count == 6, "Stock layers are incomplete.");
            Assert(window.CreateProject()["buildMode"]!.GetValue<string>() == "game-uv", "Wrong main build.");
            Assert(window.Canvas.Anchors.Count == 17, "Stock guides are missing.");
            Assert(window.FontSize == 13, "Suite typography was not applied to the native shell.");
            foreach (var (width, height) in new[] { (1600, 920), (1280, 760), (1000, 680) })
            {
                foreach (var section in new[] { "floors", "paint", "logos", "import", "export" })
                {
                    window.Width = width; window.SwitchSection(section); Layout(window, width, height);
                    Assert(window.Canvas.ActualWidth > 200 && window.Canvas.ActualHeight > 150, $"Blank canvas in {section} at {width}.");
                    var toolbar = (FrameworkElement)window.FindName("PreviewToolbar");
                    var views = (FrameworkElement)window.FindName("ViewControls"); var zoom = (FrameworkElement)window.FindName("ZoomControls");
                    var overlap = Rect.Intersect(views.TransformToAncestor(toolbar).TransformBounds(new Rect(views.RenderSize)), zoom.TransformToAncestor(toolbar).TransformBounds(new Rect(zoom.RenderSize)));
                    Assert(overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1, "Preview toolbar controls overlap.");
                    var card = (FrameworkElement)window.FindName("SelectedCourtCard");
                    var rootBounds = new Rect(0, 0, width, height);
                    Assert(rootBounds.Contains(card.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(card.RenderSize))), "Selected court card left the workspace.");
                    Assert(card.ActualWidth >= 280 && Math.Abs(window.Canvas.ActualWidth - (width - 500)) < 1, "Inspector consumed the compact court viewport.");
                    foreach (var name in new[] { "MoveToolButton", "EyedropperToolButton", "HandToolButton", "ZoomToolButton" })
                    { var button = (Button)window.FindName(name); Assert(button.Width == 42 && button.Height == 42 && button.Content is ToolIcon, "Canvas tool dimensions or exact icon component differ."); }
                    Snapshot(window, Path.Combine(output, $"{section}-{width}.png"), width, height);
                }
            }
            window.SetLayerSettings("paint-left", color: "#2266CC");
            Assert(window.CreateProject()["paintSettings"]?["paint-left"]?["color"]?.GetValue<string>() == "#2266CC", "Hex colors are not connected.");
            await window.UndoAsync(); Assert(window.PaintLayers.First(l => l.Id == "paint-left").Color == "#19583F", "Paint undo failed.");
            await window.UndoAsync(true); Assert(window.PaintLayers.First(l => l.Id == "paint-left").Color == "#2266CC", "Paint redo failed.");
            var root = Environment.GetEnvironmentVariable("COURT_CREATOR_ROOT");
            if (root is null) { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "court_creator"))) directory = directory.Parent; root = directory?.FullName ?? throw new DirectoryNotFoundException("Court Creator project root was not found."); }
            var logoPath = Path.Combine(root, "src", "NBA2KCourtCreator", "Assets", "app-icon.png");
            for (var index = 0; index < 4; index++) { await window.AddLogoAsync(logoPath); var logo = window.Canvas.SelectedLayer!; logo.X = 2800 + index * 650; logo.Y = 1100; logo.Width = 300; logo.Height = 250; logo.Rotation = index * 15; }
            window.SwitchSection("logos"); Layout(window, 1600, 920);
            Assert(window.Canvas.Layers.Count == 4, "Imported logos were lost.");
            var top = window.Canvas.Layers.Last(); Assert(window.Canvas.HitArtwork(top.Center) == top, "Alpha-aware topmost selection failed.");
            var resized = TransformGeometry.Resize(top.Capture(), top.Center + new Vector(600, 500), true);
            Assert(Math.Abs(resized.Width / resized.Height - top.Width / top.Height) < .0001, "Scale lock failed.");
            var project = window.CreateProject();
            var projectPath = Path.Combine(output, "round-trip.court.json"); StudioProjectStore.Write(projectPath, project);
            await window.RestoreProjectAsync(StudioProjectStore.Read(projectPath));
            Assert(window.Canvas.Layers.Count == 4 && window.Canvas.Layers.Last().Rotation == 45, "Project round-trip lost logos or rotation.");
            Layout(window, 1600, 920);
            var widthBox = Descendants<TextBox>((DependencyObject)window.Content).First(input => Equals(input.Tag, "Width"));
            var heightBox = Descendants<TextBox>((DependencyObject)window.Content).First(input => Equals(input.Tag, "Height"));
            Assert(widthBox.Text == "300", "Restored logo inspector is stale.");
            widthBox.Text = "600";
            widthBox.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, widthBox, heightBox) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Assert(window.Canvas.SelectedLayer!.Width == 600 && window.Canvas.SelectedLayer.Height == 500 && heightBox.Text == "500", "Locked numerical resize did not synchronize dimensions.");
            await window.UndoAsync();
            Snapshot(window, Path.Combine(output, "logos-placed.png"), 1600, 920);
            // Drive actual inspector events and persisted workflow, not only model setters.
            var colorInput = Descendants<TextBox>((DependencyObject)window.Content).First(input => Equals(input.Tag, "Color:paint-left"));
            colorInput.Text = "#A25B36";
            colorInput.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, colorInput, widthBox) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Assert(window.PaintLayers.First(layer => layer.Id == "paint-left").Color == "#A25B36", "Hex edit handler failed.");
            window.SetLayerSettings("college-three", visible: true, color: "#336699");
            await window.UndoAsync(); Assert(!window.LineLayers.First(layer => layer.Id == "college-three").Visible, "Line visibility undo failed.");
            await window.UndoAsync(true); Assert(window.LineLayers.First(layer => layer.Id == "college-three").Visible, "Line visibility redo failed.");
            window.SelectCanvasTool(ArtworkTool.Transform); Assert(window.Canvas.Tool == ArtworkTool.Transform && window.Section == "logos", "Transform tool is disconnected.");
            window.FlipSelectedLogo(); Assert(window.Canvas.SelectedLayer!.FlipX, "Logo flip failed.");
            await window.UndoAsync(); Assert(!window.Canvas.SelectedLayer!.FlipX, "Logo flip undo failed.");
            await window.UndoAsync(true); Assert(window.Canvas.SelectedLayer!.FlipX, "Logo flip redo failed.");
            window.CenterSelectedLogo();
            var saved = window.CreateProject(); var persisted = Path.Combine(output, "workflow.court.json"); window.SaveProjectTo(persisted);
            await window.NewProjectAsync(); await window.OpenProjectFromAsync(persisted);
            Assert(window.Canvas.Layers.Count == 4 && window.Canvas.SelectedLayer!.FlipX == saved["logoImages"]![0]!["flipX"]!.GetValue<bool>(), "Save/open lost logo data.");
            Assert(window.PaintLayers.First(layer => layer.Id == "paint-left").Color == "#A25B36", "Save/open lost paint edits.");
            Assert(window.LineLayers.First(layer => layer.Id == "college-three").Visible, "Save/open lost line edits.");
            var stateBeforeNavigation = window.CreateProject().ToJsonString();
            foreach (var section in new[] { "logos", "floors", "paint", "import", "export", "logos" }) { window.SwitchSection(section); window.Canvas.CancelGesture(); }
            Assert(window.CreateProject().ToJsonString() == stateBeforeNavigation, "Navigation/cancel changed the project.");
            foreach (var tool in new[] { ArtworkTool.Hand, ArtworkTool.Zoom, ArtworkTool.Eyedropper, ArtworkTool.Move }) window.SelectCanvasTool(tool);
            Assert(window.Canvas.Layers.Count == 4, "Tool changes lost logos.");
            var original = window.Canvas.SelectedLayer!.Capture();
            var canvasFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(ArtworkCanvas).GetField("_original", canvasFlags)!.SetValue(window.Canvas, original);
            typeof(ArtworkCanvas).GetField("_gesture", canvasFlags)!.SetValue(window.Canvas, "move");
            window.Canvas.SelectedLayer.X += 300; window.SwitchSection("paint");
            Assert(window.Canvas.SelectedLayer.Capture() == original, "Navigation failed to cancel an unfinished logo transform.");
            window.SelectCanvasTool(ArtworkTool.Move);
            var sampled = window.Canvas.SampleArtwork(new Point(10, 10)); Assert(sampled is { A: 255 }, "Eyedropper did not sample native court artwork.");
            var unlocked = TransformGeometry.Resize(window.Canvas.SelectedLayer!.Capture(), window.Canvas.SelectedLayer.Center + new Vector(900, 200), false);
            Assert(Math.Abs(unlocked.Width / unlocked.Height - window.Canvas.SelectedLayer.Width / window.Canvas.SelectedLayer.Height) > .1, "Unlocked resize did not allow stretch.");
            Snapshot(window, Path.Combine(output, "workflow-logos.png"), 1440, 900);
            window.SwitchSection("paint"); Snapshot(window, Path.Combine(output, "workflow-colors.png"), 1440, 900);
            var syntheticPath = Path.Combine(output, "cleanup-source.png"); CreateCleanupFixture().WritePng(syntheticPath);
            var importer = new LogoImportWindow(testing: true); await importer.LoadImageAsync(syntheticPath);
            Assert(importer.Cleanup!.RemoveRegion(4,4,0) == 9, "Importer's enclosed hole was not removable.");
            ((Button)importer.FindName("CleanupUndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!importer.Cleanup.HasChanges, "Importer Undo did not restore pixels.");
            importer.Cleanup.RemoveBackground(Colors.White, 0);
            // Load again simulates choosing another image: cleanup and all-color state must reset.
            await importer.LoadImageAsync(syntheticPath); Assert(!importer.Cleanup!.HasChanges && ((CheckBox)importer.FindName("AllMatchingCheck")).IsChecked == false, "Choosing an image leaked previous cleanup state.");
            var example = Path.Combine(output, "example-logo.png"); WriteLogoExample(example);
            await importer.LoadImageAsync(example);
            ((Button)importer.FindName("RemoveBackgroundButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var wait = 0; !((Button)importer.FindName("PlaceButton")).IsEnabled && wait < 400; wait++) await Task.Delay(10);
            Assert(importer.Cleanup!.HasChanges && ((Button)importer.FindName("CleanupUndoButton")).IsEnabled, "Background cleanup did not update its preview and Undo state.");
            ((Button)importer.FindName("WandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SnapshotDialog(importer, Path.Combine(output, "logo-importer.png"), 980, 700);
            Assert(window.Canvas.Layers.Count == 4, "Cancelled importer placed an image.");
            var reopened = new LogoImportWindow(testing: true); await reopened.LoadImageAsync(syntheticPath); Assert(!reopened.Cleanup!.HasChanges, "Cancelled cleanup persisted into reopened importer.");
            reopened.Cleanup.RemoveBackground(Colors.White, 0); reopened.Cleanup.RemoveRegion(4,4,0);
            var prepared = Path.Combine(output, "cleanup-prepared.png"); reopened.Cleanup.WritePng(prepared); reopened.Close();
            var accepted = new LogoImportWindow(testing: true);
            await accepted.LoadImageAsync(syntheticPath); accepted.Cleanup!.RemoveBackground(Colors.White, 0);
            Assert(await accepted.PreparePlacementAsync() && accepted.PreparedPath is not null && File.Exists(accepted.PreparedPath), "Placement did not prepare a local transparent PNG.");
            Assert(new LogoCleanupImage(StudioImages.Load(accepted.PreparedPath!)).Pixel(0,0).A == 0, "Prepared placement lost transparency."); accepted.ReleaseTemporaryOutput(); accepted.Close();
            var cancelled = new LogoImportWindow(testing: true);
            await cancelled.LoadImageAsync(syntheticPath); cancelled.Close(); Assert(cancelled.PreparedPath is null,"Cancel unexpectedly prepared placement.");
            window.DeleteSelectedLogo();
            await window.AddLogoAsync(prepared, "Cleaned fixture");
            Assert(window.Canvas.Layers.Last().Name == "Cleaned fixture" && window.Canvas.Layers.Last().Image is BitmapSource, "Prepared transparent logo did not import.");
            await window.UndoAsync(); Assert(window.Canvas.Layers.Count == 3, "Placement undo failed."); await window.UndoAsync(true); Assert(window.Canvas.Layers.Count == 4, "Placement redo failed.");
            var firstFloor = window.CreateProject()["floor"]!["id"]!.GetValue<string>();
            await window.SelectFloorAsync(window.Floors.First(floor => floor.Id != firstFloor));
            Assert(window.Canvas.Layers.Count == 4 && window.CreateProject()["floor"]!["id"]!.GetValue<string>() != firstFloor, "Hardwood switching lost artwork or did not change the court.");
            var catalog = new FloorCatalogWindow(null, window.Floors, null, [], []);
            SnapshotDialog(catalog, Path.Combine(output, "catalog.png"), 680, 520);
            var team = new JsonArray(new JsonObject { ["team"] = "Example Team", ["league"] = "NBA", ["colors"] = new JsonArray(new JsonObject { ["name"] = "Primary", ["hex"] = "#19583F" }) });
            var colors = new TeamColorWindow(null, team, testing: true);
            Assert(colors.Rows.OfType<PaletteHeading>().Count() == 1 && !colors.Rows.OfType<PaletteSwatches>().Any(), "Categories must start collapsed.");
            colors.SetSearch("Example");
            SnapshotDialog(colors, Path.Combine(output, "team-colors.png"), 480, 450);
            Assert(colors.Rows.OfType<PaletteSwatches>().Single().Colors.Single().Hex == "#19583F", "Search must reveal colors inside collapsed categories.");
            StudioTheme.Apply(true); Snapshot(window, Path.Combine(output, "logos-dark.png"), 1280, 760);
            Assert(((SolidColorBrush)window.Foreground).Color == ((SolidColorBrush)window.FindResource("TextBrush")).Color, "Native dark theme text did not update.");
            StudioTheme.Apply(false);
            var target = Path.Combine(output, "native-export.png"); await window.ExportToAsync(target, false);
            var image = StudioImages.Load(target, 1024); Assert(image.PixelWidth == 1024 && image.PixelHeight == 512, "Export aspect ratio is wrong.");
            var iff = Path.Combine(output, "native-export.iff"); await window.ExportToAsync(iff, true); Assert(new FileInfo(iff).Length > 1000, "IFF export is empty.");
            await CheckNativeImportPreview(window, iff, image, output);
            await window.NewProjectAsync(); Assert(window.Canvas.Layers.Count == 0, "New did not clear logos.");
            File.Delete(iff);
            File.Delete(target);
            Assert(window.PaintLayers.First(l => l.Id == "paint-left").Color == "#19583F", "New did not restore paint defaults.");
            Assert(window.LineLayers.First(l => l.Id == "college-three").Visible == false, "New did not restore NBA markings.");
            Console.WriteLine("PASS headless: 15 hybrid layouts; Canvas icon components/dimensions; native UV; hex/visibility edits; hardwood switching; synthetic move/transform/pan/zoom/eyedropper; scale lock/stretch; flip/center; save/open; repeated navigation/cancel; importer preparation/undo; transparent placement undo/redo; full-resolution PNG + IFF; New defaults; cleanup flood fill/tolerance/all-colors/alpha/coordinates. Native focus and modal interaction require the optional native-window checks.");
        }
        finally { window.Close(); }
    }
    private static LogoCleanupImage CreateCleanupFixture()
    {
        var pixels = new byte[9 * 9 * 4];
        for (var i = 0; i < 81; i++) { pixels[i*4] = pixels[i*4+1] = pixels[i*4+2] = pixels[i*4+3] = 255; }
        for (var y=2;y<=6;y++) for(var x=2;x<=6;x++) if(x==2||x==6||y==2||y==6) { var i=(y*9+x)*4; pixels[i]=pixels[i+1]=pixels[i+2]=0; }
        pixels[3]=0; pixels[(2*9+2)*4+3]=128;
        return new LogoCleanupImage(BitmapSource.Create(9,9,96,96,PixelFormats.Bgra32,null,pixels,36));
    }
    private static void WriteLogoExample(string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var blue = new SolidColorBrush(Color.FromRgb(39,70,108));
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,800,520));
            drawing.DrawRoundedRectangle(blue,null,new Rect(240,70,320,190),26,26);
            var badge = new FormattedText("2K",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Italic,FontWeights.Bold,FontStretches.Normal),124,Brushes.White,1);
            drawing.DrawText(badge,new Point(400-badge.Width/2,76));
            var text = new FormattedText("COURT",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),104,blue,1);
            drawing.DrawText(text,new Point(400-text.Width/2,280));
        }
        var bitmap = new RenderTargetBitmap(800,520,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); encoder.Save(stream);
    }
    private static void CheckCleanup()
    {
        var image=CreateCleanupFixture(); Assert(image.RemoveBackground(Colors.White,0)==55,"Edge background crossed an enclosed boundary or modified transparency.");
        Assert(image.Pixel(4,4).A==255 && image.Pixel(2,2).A==128 && image.Pixel(0,0).A==0,"Background removal changed protected alpha.");
        Assert(image.RemoveRegion(4,4,0)==9,"Connected wand did not remove the enclosed hole."); image.Undo(); Assert(image.Pixel(4,4).A==255,"Wand undo failed."); image.Reset(); Assert(!image.HasChanges,"Reset did not restore original bytes."); image.Undo(); Assert(image.HasChanges,"Reset was not undoable.");
        image=CreateCleanupFixture(); Assert(image.RemoveRegion(4,4,0,true)==64,"All matching colors missed disconnected regions.");
        image=CreateCleanupFixture(); Assert(image.RemoveRegion(4,4,255)==80,"Tolerance maximum did not reach all opaque colors.");
        var rgba=new byte[]{255,255,255,255,0,0,0,255,255,255,255,255,255,255,255,255,0,0,0,255,0,0,0,255};
        var narrow=new LogoCleanupImage(BitmapSource.Create(3,2,96,96,PixelFormats.Bgra32,null,rgba,12)); Assert(narrow.RemoveRegion(2,0,0)==1 && narrow.Pixel(0,1).A==255,"Flood fill wrapped rows.");
        var near=new LogoCleanupImage(BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,255,255,255,250,250,250,127},8));
        Assert(near.RemoveRegion(0,0,0)==1 && near.Pixel(1,0).A==127,"Exact tolerance removed a near color or changed semi-transparency."); near.Reset(); Assert(near.RemoveRegion(0,0,5)==2,"Tolerance boundary failed.");
        Assert(LogoCleanupImage.MapPreview(new Point(100,25),new Size(200,200),new Size(200,100)) is null,"Letterbox click mapped into artwork.");
        Assert(LogoCleanupImage.MapPreview(new Point(100,100),new Size(200,200),new Size(200,100))==new Point(100,50),"Scaled preview coordinate mapping failed.");
        Assert(LogoCleanupImage.MapPreview(new Point(199,149),new Size(200,200),new Size(200,100))==new Point(199,99),"Preview edge mapping failed.");
        Assert(LogoCleanupImage.MapPreview(new Point(200,100),new Size(200,200),new Size(200,100)) is null,"Out-of-image click was accepted.");
        Console.WriteLine("PASS: local cleanup connected/edge/all-matching, enclosed holes, row boundaries, tolerance 0/5/255, transparency, undo/reset, scaled/letterbox coordinates.");
    }
    private static void SnapshotDialog(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); window.Close();
    }
}
