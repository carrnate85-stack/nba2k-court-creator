using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TextureStudio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckHardwoodTools(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var sourceRoot = Environment.CurrentDirectory;
        using var locator = new PythonServiceClient(); var installedRoot = locator.ProjectRoot;
        var fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        foreach (var folder in new[] { "court_creator", "tools" })
            foreach (var source in Directory.EnumerateFiles(Path.Combine(sourceRoot, folder), "*.py", SearchOption.TopDirectoryOnly))
            { var destination = Path.Combine(fixture, folder, Path.GetFileName(source)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
        File.Copy(Path.Combine(installedRoot, "tools/texconv.exe"), Path.Combine(fixture, "tools/texconv.exe"));
        Directory.CreateDirectory(Path.Combine(fixture, "data/generated"));
        File.Copy(Path.Combine(installedRoot, "data/generated/experimental-stock-lines.json"), Path.Combine(fixture, "data/generated/experimental-stock-lines.json"));
        File.Copy(Path.Combine(installedRoot, "data/team_palettes.json"), Path.Combine(fixture, "data/team_palettes.json"));
        var catalogName = "assets/court_floor_templates/nba2k27/nba2k27_floor_templates.json";
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(installedRoot, catalogName)))!.AsObject();
        foreach (var item in catalog["templates"]!.AsArray().OfType<JsonObject>())
            foreach (var field in new[] { "path", "thumbnailPath" }) item[field] = Path.Combine(installedRoot, "assets", item[field]!.GetValue<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture, catalogName))!); File.WriteAllText(Path.Combine(fixture, catalogName), catalog.ToJsonString());
        ProcessStartInfo Worker()
        {
            var start = new ProcessStartInfo(Path.Combine(installedRoot, "runtime/python/python.exe")) { WorkingDirectory = fixture, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-u", "-m", "court_creator.service" }) start.ArgumentList.Add(argument);
            return start;
        }
        var geometry = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, "data/generated/experimental-stock-lines.json")))!.AsObject();
        Point Interior(string id)
        {
            var polygons = geometry["paints"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id)["gameUvPolygons"]!.AsArray().OfType<JsonArray>();
            double Area(JsonArray polygon) => Math.Abs(polygon.Select((p, i) => p![0]!.GetValue<double>() * polygon[(i + 1) % polygon.Count]![1]!.GetValue<double>() - polygon[(i + 1) % polygon.Count]![0]!.GetValue<double>() * p[1]!.GetValue<double>()).Sum());
            var largest = polygons.MaxBy(Area)!;
            return new(largest.Average(p => p![0]!.GetValue<double>()), largest.Average(p => p![1]!.GetValue<double>()));
        }
        var left = Interior("two-point-left"); var right = Interior("two-point-right"); var key = Interior("paint-left");
        var centerAnchor = geometry["guides"]!["game-uv"]!["anchors"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == "court-center");
        var center = new Point(centerAnchor["x"]!.GetValue<double>(), centerAnchor["y"]!.GetValue<double>());
        var mainPath = Path.Combine(fixture, "main.bmp"); var secondaryPath = Path.Combine(fixture, "second.bmp");
        WriteRevisionBitmap(mainPath, Colors.Red); WriteRevisionBitmap(secondaryPath, Colors.Blue);
        StockFloor Floor(string id, string path) => StockFloor.Read(new JsonObject { ["id"] = id, ["name"] = id, ["category"] = "Custom", ["path"] = path }, fixture);
        var main = Floor("Main test hardwood", mainPath); var second = Floor("Two-point test hardwood", secondaryPath);
        var originals = (AssetHash(mainPath), AssetHash(secondaryPath));
        var window = new StudioWindow(true, new PythonServiceClient(fixture, Worker), new PythonServiceClient(fixture, Worker));
        try
        {
            await window.InitializeAsync(); await window.SelectFloorAsync(main);
            Assert(!((ComboBoxItem)window.FindName("TwoPointEditTarget")).IsEnabled, "Secondary adjustment target is available when two-point hardwood is disabled.");
            Assert(((FrameworkElement)window.FindName("HardwoodOptionsPanel")).Visibility == Visibility.Visible, "Hardwood bar did not open by default.");
            CheckToolInspectorTabs(window, mainPath);
            window.SwitchSection("logos"); Assert(((FrameworkElement)window.FindName("HardwoodOptionsPanel")).Visibility == Visibility.Visible, "Inspector tabs closed the hardwood bar."); window.SwitchSection("paint");
            foreach (var layer in window.PaintLayers.Concat(window.LineLayers)) window.SetLayerSettings(layer.Id, visible: false);
            window.SetLayerSettings("NBA_line_three_point_lowShape", visible: true);
            window.SetLayerSettings("paint-left", visible: true, color: "#008000");
            await window.SelectTwoPointFloorAsync(second);
            Color NativePixel(Point point)
            {
                var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) { drawing.PushTransform(new ScaleTransform(.25, .25)); drawing.DrawDrawing(window.Canvas.BackgroundDrawing); }
                var image = new RenderTargetBitmap(2048, 1024, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var pixels = new byte[4]; image.CopyPixels(new Int32Rect((int)(point.X / 4), (int)(point.Y / 4), 1, 1), pixels, 4, 0);
                return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
            }
            Assert(NativePixel(left) == Colors.Blue && NativePixel(right) == Colors.Blue, "Second hardwood is not clipped to both two-point areas.");
            Assert(NativePixel(center) == Colors.Red && NativePixel(key) == Colors.Green, "Second hardwood changed the center or key.");
            window.SetLayerSettings("paint-left", visible: false);
            Assert(NativePixel(key) == Colors.Blue && NativePixel(Interior("secondary-paint-left")) == Colors.Blue
                && NativePixel(Interior("paint-right")) == Colors.Blue && NativePixel(Interior("secondary-paint-right")) == Colors.Blue,
                "Uncolored primary/secondary keys did not follow the two-point hardwood.");
            await window.ExportToAsync(Path.Combine(output, "unpainted-keys.png"), false);
            CheckExportPixel(Path.Combine(output, "unpainted-keys.png"), key, Colors.Blue);
            window.SetLayerSettings("paint-left", visible: true, color: "#008000");
            var surfaces = (Dictionary<string, Geometry>)typeof(StudioWindow).GetField("_threePointSurfaces", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            Point Between(string outer, string inner)
            {
                for (var x = 1200; x < 4000; x += 20)
                    for (var y = 700; y < 3400; y += 20)
                    {
                        var point = new Point(x, y);
                        var nearby = new[] { point, point + new Vector(8, 0), point + new Vector(-8, 0), point + new Vector(0, 8), point + new Vector(0, -8) };
                        if (nearby.All(p => surfaces[outer].FillContains(p) && !surfaces[inner].FillContains(p)
                            && !window.PaintLayers.First(layer => layer.Id == "paint-left").Geometry.FillContains(p)
                            && !window.LineLayers.Any(line => line.Geometry.FillContains(p)))) return point;
                    }
                throw new Exception("No area between the three-point boundaries.");
            }
            var nbaCollege = Between("NBA_line_three_point_lowShape", "college-three");
            var collegeSchool = Between("college-three", "high-school-three");
            window.SetLayerSettings("NBA_line_three_point_lowShape", visible: false);
            window.SetLayerSettings("high-school-three", visible: true);
            Assert(NativePixel(nbaCollege) == Colors.Red && NativePixel(collegeSchool) == Colors.Red && NativePixel(left) == Colors.Blue,
                "High-school-only hardwood extends beyond its three-point line.");
            await window.ExportToAsync(Path.Combine(output, "high-school-only.png"), false);
            CheckExportPixel(Path.Combine(output, "high-school-only.png"), nbaCollege, Colors.Red);
            CheckExportPixel(Path.Combine(output, "high-school-only.png"), collegeSchool, Colors.Red);
            window.SetLayerSettings("college-three", visible: true);
            Assert(NativePixel(nbaCollege) == Colors.Red && NativePixel(collegeSchool) == Colors.Blue, "Multiple lines did not choose College as the outermost enabled line.");
            window.SetLayerSettings("NBA_line_three_point_lowShape", visible: true);
            Assert(NativePixel(nbaCollege) == Colors.Blue, "NBA hardwood boundary was not restored.");
            window.SetLayerSettings("college-three", visible: false); window.SetLayerSettings("high-school-three", visible: false);
            window.SetLayerSettings("NBA_line_three_point_lowShape", visible: false);
            Assert(NativePixel(left) == Colors.Red, "Two-point hardwood draws without any visible three-point boundary.");
            window.SetLayerSettings("NBA_line_three_point_lowShape", visible: true);
            await CheckTwoPointPaintBoundaries(window, output, left, right, key, nbaCollege, collegeSchool, NativePixel);
            await CheckPaintAndText(window, output, left, key, center);
            window.ShowHardwoodTools(true);
            var toggleContent = (FrameworkElement)window.Content; toggleContent.Measure(new Size(1200, 800)); toggleContent.Arrange(new Rect(0, 0, 1200, 800)); toggleContent.UpdateLayout();
            var toggleBar = (Grid)window.FindName("HardwoodOptions");
            var enableTwoPoint = (CheckBox)window.FindName("TwoPointHardwoodCheckBox");
            var disableTwoPoint = (Button)window.FindName("DisableTwoPointHardwoodButton");
            Assert(enableTwoPoint.IsChecked == true && ((TextBlock)window.FindName("TwoPointCourtText")).Text == second.Name && ((TextBlock)window.FindName("SelectedCourtText")).Text == main.Name && enableTwoPoint.Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("TwoPointSelector")).Visibility == Visibility.Visible, "Secondary choice did not update the preview card/checkbox.");
            var rememberedSecond = window.CreateProject()["twoPointFloor"]!.ToJsonString(); disableTwoPoint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!window.CreateProject()["twoPointHardwoodEnabled"]!.GetValue<bool>() && window.CreateProject()["twoPointFloor"]!.ToJsonString() == rememberedSecond && NativePixel(left) == Colors.Red && NativePixel(right) == Colors.Red && NativePixel(key) == Colors.Green, "Checkbox did not hide the second texture without forgetting it.");
            var disabledProject = Path.Combine(output, "disabled-two-point.court.json"); await window.SaveProjectToAsync(disabledProject); await window.OpenProjectFromAsync(disabledProject);
            Assert(enableTwoPoint.IsChecked == false && window.CreateProject()["twoPointFloor"] is JsonObject && NativePixel(left) == Colors.Red, "Save/reopen lost the disabled second texture.");
            var disabledSnapshot = window.CreateProject(); var legacySnapshot = (JsonObject)disabledSnapshot.DeepClone(); legacySnapshot.Remove("twoPointHardwoodEnabled");
            await window.RestoreProjectAsync(legacySnapshot); Assert(enableTwoPoint.IsChecked == true && NativePixel(left) == Colors.Blue, "Legacy second hardwood was disabled."); await window.RestoreProjectAsync(disabledSnapshot);
            var disabledExport = Path.Combine(output, "two-point-disabled-export.png"); await window.ExportToAsync(disabledExport, false);
            using (var stream = File.OpenRead(disabledExport))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); var bytes = new byte[4];
                rgba.CopyPixels(new Int32Rect((int)left.X, (int)left.Y, 1, 1), bytes, 4, 0); Assert(bytes[2] == 255 && bytes[1] == 0 && bytes[0] == 0, "PNG export ignored the disabled checkbox.");
            }
            enableTwoPoint.IsChecked = true; await window.UndoAsync(); Assert(enableTwoPoint.IsChecked == false && NativePixel(left) == Colors.Red, "Checkbox undo failed."); await window.UndoAsync(true); Assert(enableTwoPoint.IsChecked == true && NativePixel(left) == Colors.Blue, "Checkbox redo failed.");
            window.ShowHardwoodTools(); Assert(((TextBlock)window.FindName("SelectedCourtText")).Text == main.Name, "Main target did not update the preview card.");
            window.ShowHardwoodTools(true);
            var mainDefault = window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString();
            await window.SetHardwoodTextureAsync(new(20, 10, -30, 150, 45), true);
            Assert(window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString() == mainDefault, "Secondary adjustments changed the main hardwood.");
            Assert(NativePixel(key) == Colors.Green, "Secondary adjustments escaped the clipping mask.");
            var adjustedSecond = window.CreateProject()["twoPointFloor"]!.ToJsonString();
            await window.SetHardwoodTextureAsync(new(-20, -10, 20, 80, -30));
            Assert(window.CreateProject()["twoPointFloor"]!.ToJsonString() == adjustedSecond, "Main adjustments changed the secondary hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString() == mainDefault, "Adjustment undo failed.");
            await window.UndoAsync(true); Assert(window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Adjustment redo failed.");
            var savedPath = Path.Combine(output, "two-hardwoods.court.json"); await window.SaveProjectToAsync(savedPath);
            var saved = StudioProjectStore.Read(savedPath);
            Assert(!Path.IsPathRooted(saved["floor"]!["path"]!.GetValue<string>()) && !Path.IsPathRooted(saved["twoPointFloor"]!["path"]!.GetValue<string>()), "Portable save did not bundle both hardwoods.");
            await window.OpenProjectFromAsync(savedPath);
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["rotation"]!.GetValue<int>() == 45, "Reopening lost secondary settings.");
            var reopened = window.CreateProject()["twoPointFloor"]!.ToJsonString();
            window.ShowHardwoodTools(true);
            var initialContent = (FrameworkElement)window.Content; initialContent.Measure(new Size(1200, 800)); initialContent.Arrange(new Rect(0, 0, 1200, 800)); initialContent.UpdateLayout();
            await window.SelectTwoPointFloorAsync(null);
            Assert(window.CreateProject()["twoPointFloor"] is null && enableTwoPoint.Visibility == Visibility.Visible, "Removing the second hardwood did not restore the checkbox.");
            await window.UndoAsync(); Assert(window.CreateProject()["twoPointFloor"]!.ToJsonString() == reopened, "Undo did not restore the bundled secondary hardwood.");
            await window.NewProjectAsync(); Assert(window.CreateProject()["twoPointFloor"] is null, "New project retained the secondary hardwood."); await window.UndoAsync();
            Layout(window, 1200, 800); window.ShowHardwoodTools(true);
            var content = (FrameworkElement)window.Content; content.Measure(new Size(1200, 800)); content.Arrange(new Rect(0, 0, 1200, 800)); content.UpdateLayout();
            var bar = (Grid)window.FindName("HardwoodOptions");
            var reset = (Button)window.FindName("HardwoodResetButton");
            Assert(bar.Visibility == Visibility.Visible && reset.Visibility == Visibility.Visible && !Descendants<ScrollViewer>(bar).Any(), "Adjustment bar has scrolling or its reset action is missing.");
            foreach (var width in new[] { 1000, 1200, 1440, 1920 })
            {
                Layout(window, width, 800); content.UpdateLayout();
                var panel = (FrameworkElement)window.FindName("HardwoodOptionsPanel");
                var workspace = (Grid)window.FindName("WorkspaceRoot");
                var overlay = (FrameworkElement)window.FindName("ToolOptionsOverlay");
                var viewport = (FrameworkElement)window.FindName("ViewportCard");
                var host = (Grid)window.Canvas.Parent;
                var root = (Grid)workspace.Parent;
                var attachedBounds = panel.TransformToAncestor(host).TransformBounds(new Rect(panel.RenderSize));
                var document = (FrameworkElement)window.FindName("DocumentChrome");
                var documentBounds = document.TransformToAncestor(root).TransformBounds(new Rect(document.RenderSize));
                var workspaceBounds = workspace.TransformToAncestor(root).TransformBounds(new Rect(workspace.RenderSize));
                var rail = (FrameworkElement)window.FindName("LeftToolRail");
                var railBounds = rail.TransformToAncestor(root).TransformBounds(new Rect(rail.RenderSize));
                var viewportBounds = viewport.TransformToAncestor(root).TransformBounds(new Rect(viewport.RenderSize));
                Assert(ReferenceEquals(overlay.Parent, host) && Grid.GetRow(workspace) == 2
                    && Math.Abs(attachedBounds.Top) < .001 && Math.Abs(attachedBounds.Left) < .001
                    && Math.Abs(attachedBounds.Width - host.ActualWidth) < .001
                    && attachedBounds.Height == TextureStudio.ContextualToolOptionsBar.RowHeight
                    && Math.Abs(workspaceBounds.Top - documentBounds.Bottom) < .001
                    && Math.Abs(railBounds.Top - documentBounds.Bottom) < .001 && Math.Abs(railBounds.Left) < .001
                    && viewport.Margin == new Thickness(0, 0, 12, 12)
                    && Math.Abs(viewportBounds.Left - railBounds.Right) < .001
                    && Math.Abs(viewportBounds.Top - documentBounds.Bottom) < .001,
                    "Tool bar is not attached inside the court frame while the left tool rail extends to the document tabs at width " + width);
                var bounds = new Rect(0, 0, panel.ActualWidth, panel.ActualHeight); bounds.Inflate(.1, .1);
                var sliders = Descendants<Slider>(bar).ToArray(); Assert(sliders.Length == 5, "An adjustment slider is missing.");
                foreach (var control in sliders.Cast<FrameworkElement>().Concat(Descendants<Button>(bar)))
                    Assert(control.ActualWidth >= 18 && bounds.Contains(control.TransformToAncestor(panel).TransformBounds(new Rect(control.RenderSize))), "An adjustment is clipped at width " + width);
                foreach (var caption in Descendants<TextBlock>(bar))
                    Assert(bounds.Contains(caption.TransformToAncestor(panel).TransformBounds(new Rect(caption.RenderSize))), "An adjustment label is clipped at width " + width);
                var strip = (FrameworkElement)window.FindName("HardwoodSelectionStrip");
                Assert(strip.TransformToAncestor(content).TransformBounds(new Rect(strip.RenderSize)).Top >= ((FrameworkElement)window.FindName("ViewportCard")).TransformToAncestor(content).TransformBounds(new Rect(((FrameworkElement)window.FindName("ViewportCard")).RenderSize)).Bottom, "Hardwood selectors are not below the workspace.");
                Assert(Math.Abs(((FrameworkElement)window.FindName("SelectedCourtCard")).ActualWidth - ((FrameworkElement)window.FindName("TwoPointCourtCard")).ActualWidth) <= .1, "Bottom hardwood selectors do not have equal widths at " + width);
                var dropdown = (FrameworkElement)window.FindName("HardwoodEditTarget");
                Assert(dropdown.ActualHeight == 28 && bounds.Contains(dropdown.TransformToAncestor(panel).TransformBounds(new Rect(dropdown.RenderSize))), "Adjustment target dropdown is clipped at " + width);
                Snapshot(window, Path.Combine(output, "hardwood-bottom-" + width + ".png"), width, 800);
                CheckHardwoodBarView(window, width, mainPath);
            }
            Layout(window, 1200, 800);
            var brightness = Descendants<Slider>(bar).Single(slider => Equals(slider.Tag, "brightness"));
            brightness.Value = 15; await window.FlushHardwoodPreviewAsync();
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 15, "Slider did not edit the selected hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 20, "Slider undo did not restore the previous settings.");
            window.ShowHardwoodTools(true); content.UpdateLayout();
            TextBox EditNumber(string setting, string text)
            {
                Descendants<Button>(bar).Single(button => AutomationProperties.GetName(button) == "Enter hardwood " + setting).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var input = Descendants<TextBox>(bar).Single(box => Equals(box.Tag, "HardwoodNumber:" + setting));
                Assert(input.Visibility == Visibility.Visible, $"Clicking the value did not open number entry: slider={brightness.IsEnabled}, bar={bar.IsEnabled}, target={((ComboBox)window.FindName("HardwoodEditTarget")).SelectedIndex}, status={((TextBlock)window.FindName("StatusText")).Text}."); input.Text = text; return input;
            }
            void NumberKey(TextBox input, Key key) => input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenKeySource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            foreach (var (setting, number) in new[] { ("brightness", 17), ("contrast", -13), ("saturation", 24), ("scale", 125), ("rotation", -90) })
            {
                var input = EditNumber(setting, number + (setting == "rotation" ? "°" : "%")); NumberKey(input, Key.Enter);
                Assert(input.Visibility == Visibility.Collapsed && window.CreateProject()["twoPointFloor"]!["textureSettings"]![setting]!.GetValue<int>() == number, "Exact number did not apply to " + setting);
            }
            Assert(window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Exact entry changed the other hardwood.");
            var draft = EditNumber("brightness", "91"); NumberKey(draft, Key.Escape);
            Assert(draft.Visibility == Visibility.Collapsed && window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 17, "Escape applied a draft.");
            draft = EditNumber("brightness", "101"); NumberKey(draft, Key.Enter);
            Assert(draft.Visibility == Visibility.Visible && window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 17, "Out-of-range entry was accepted.");
            await Expect<InvalidOperationException>(() => window.SaveProjectToAsync(Path.Combine(output, "invalid-number.court.json")));
            draft.Text = "18"; await window.SaveProjectToAsync(Path.Combine(output, "exact-number.court.json"));
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 18, "Save missed an active number draft.");
            draft = EditNumber("brightness", "19"); draft.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, draft, reset) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert(draft.Visibility == Visibility.Collapsed && window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 19, "Leaving number entry did not apply it.");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var resetDeadline = DateTime.UtcNow.AddSeconds(10);
            while (window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() != 0 && DateTime.UtcNow < resetDeadline) await Task.Delay(25);
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!.ToJsonString() == new HardwoodTextureSettings().ToJson().ToJsonString() && window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Reset icon did not reset only the selected hardwood.");
            await window.UndoAsync(); Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 19, "Reset icon undo failed.");
            window.ShowHardwoodTools(true); content.UpdateLayout();
            var editTarget = (ComboBox)window.FindName("HardwoodEditTarget");
            editTarget.SelectedIndex = 0; Assert(brightness.Value == -20, "Main target did not populate its independent settings.");
            draft = EditNumber("brightness", "-7"); editTarget.SelectedIndex = 1;
            Assert(draft.Visibility == Visibility.Collapsed && window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -7 && window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 19 && brightness.Value == 19, "Target switch lost a main draft or changed the secondary settings.");
            await window.UndoAsync(); Assert(window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Main exact edit undo failed.");
            editTarget.SelectedIndex = 0; draft = EditNumber("brightness", "101"); editTarget.SelectedIndex = 1;
            Assert(editTarget.SelectedIndex == 0 && draft.Visibility == Visibility.Visible, "Invalid draft was discarded by changing the adjustment target."); NumberKey(draft, Key.Escape);
            editTarget.SelectedIndex = 1; brightness.Value = 24;
            Assert(window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 24 && window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == -20, "Secondary dropdown target changed the main hardwood.");
            await window.UndoAsync();
            editTarget.SelectedIndex = 0; reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); resetDeadline = DateTime.UtcNow.AddSeconds(10);
            while (window.CreateProject()["floor"]!["textureSettings"]!["brightness"]!.GetValue<int>() != 0 && DateTime.UtcNow < resetDeadline) await Task.Delay(25);
            Assert(window.CreateProject()["floor"]!["textureSettings"]!.ToJsonString() == new HardwoodTextureSettings().ToJson().ToJsonString() && window.CreateProject()["twoPointFloor"]!["textureSettings"]!["brightness"]!.GetValue<int>() == 19, "Main dropdown target reset changed the secondary hardwood.");
            await window.UndoAsync(); editTarget.SelectedIndex = 1; content.UpdateLayout();
            var screenshot = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); screenshot.Render((Visual)window.Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "hardwood-controls.png"))) encoder.Save(stream);
            window.SelectCanvasTool(ArtworkTool.Hand); Assert(bar.Visibility == Visibility.Collapsed, "Hardwood bar remained active after choosing another tool.");
            window.SwitchSection("paint"); Assert(bar.Visibility == Visibility.Collapsed, "Changing inspector tabs reactivated hardwood over another tool.");
            ((Button)window.FindName("HardwoodToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(bar.Visibility == Visibility.Visible, "Hardwood tool did not reopen the bar.");
            await window.SetHardwoodTextureAsync(new()); await window.SetHardwoodTextureAsync(new(), true);
            await window.ExportToAsync(Path.Combine(output, "two-point-export.png"), false);
            await window.ExportToAsync(Path.Combine(output, "two-point-export.iff"), true);
            Assert(originals == (AssetHash(mainPath), AssetHash(secondaryPath)), "Hardwood controls modified an original source.");
            File.WriteAllText(Path.Combine(output, "samples.json"), new JsonObject { ["left"] = new JsonArray(left.X, left.Y), ["right"] = new JsonArray(right.X, right.Y), ["key"] = new JsonArray(key.X, key.Y), ["center"] = new JsonArray(center.X, center.Y), ["fixture"] = fixture }.ToJsonString());
            Console.WriteLine("PASS hardwood tools: both region masks, unchanged keys/center, independent sliders and exact numeric entry, Enter/Escape/focus/save validation, equal-width bottom selectors at 1000/1200/1440/1920, Main/2-point adjustment dropdown and draft validation, responsive Canvas-styled adjustments without scrolling, stationary canvas across tool bars at both heights, all viewports and zoom/pan, independent reset and editing after undo, portable save/reopen, Use main/New undo, full PNG/IFF exports, original files unchanged. No native windows opened.");
        }
        finally { window.Close(); }
    }

    private static void CheckHardwoodBarView(StudioWindow window, int width, string logoPath)
    {
        var canvas = window.Canvas;
        var content = (FrameworkElement)window.Content;
        var logo = new ArtworkLayer { Path = logoPath, Name = "Toolbar position check" };
        canvas.Layers.Add(logo); canvas.SelectedLayer = logo;
        try
        {
            foreach (var height in new[] { 680, 800 })
            foreach (var viewport in new Rect?[] { null, new Rect(0, 0, 4096, 4096), new Rect(4096, 0, 4096, 4096) })
            foreach (var zoom in new[] { 1.0, 1.75 })
            {
                Layout(window, width, height); window.ShowHardwoodTools(true); content.UpdateLayout();
                canvas.Viewport = viewport; canvas.Fit();
                var view = viewport ?? new Rect(new Point(), canvas.DocumentSize);
                var parent = (FrameworkElement)canvas.Parent;
                var canvasBounds = canvas.TransformToAncestor(parent).TransformBounds(new Rect(canvas.RenderSize));
                Assert(canvas.Margin == new Thickness() && canvasBounds.Top == 0 && canvasBounds.Left == 0 && canvas.ActualWidth == parent.ActualWidth,
                    "Court centering moved or narrowed the workspace control.");
                var fitScale = Math.Min((canvas.ActualWidth - 24) / view.Width, (canvas.ActualHeight - 24) / view.Height);
                var fitOffset = Math.Min(24, Math.Max(0, (canvas.ActualHeight - view.Height * fitScale) / 2 - 12));
                var fitCenter = canvas.ToScreen(new Point(view.X + view.Width / 2, view.Y + view.Height / 2));
                Assert(Math.Abs(canvas.Scale - fitScale) < .000001 && Math.Abs(fitCenter.Y - canvas.ActualHeight / 2 - fitOffset) < .000001,
                    "Initial court centering changed the fit scale or moved the workspace instead of the artwork.");
                Assert(canvas.ToScreen(view.TopLeft).Y >= 12 - .001 && canvas.ToScreen(view.BottomRight).Y <= canvas.ActualHeight - 12 + .001,
                    "Initial court offset clipped a fitted viewport.");
                Assert((canvas.ToDocument(fitCenter) - new Point(view.X + view.Width / 2, view.Y + view.Height / 2)).Length < .000001,
                    "Offset court input coordinates do not match its preview.");
                canvas.ChangeZoom(zoom, new Point(canvas.ActualWidth * .3, canvas.ActualHeight * .65));
                (Rect Bounds, double Scale, double Zoom, Point Corner, Point Center) Mapping() => (
                    canvas.TransformToAncestor(content).TransformBounds(new Rect(canvas.RenderSize)), canvas.Scale, canvas.Zoom,
                    canvas.TransformToAncestor(content).Transform(canvas.ToScreen(new Point(0, 0))),
                    canvas.TransformToAncestor(content).Transform(canvas.ToScreen(new Point(4096, 2048))));
                var before = Mapping(); var project = window.CreateProject().ToJsonString();
                var options = (FrameworkElement)window.FindName("ToolOptionsOverlay");
                var optionsBounds = options.TransformToAncestor(content).TransformBounds(new Rect(options.RenderSize));
                foreach (var tool in new[] { ArtworkTool.Hand, ArtworkTool.Zoom, ArtworkTool.Eyedropper, ArtworkTool.Move, ArtworkTool.Transform, ArtworkTool.Bucket, ArtworkTool.Type })
                {
                    window.SelectCanvasTool(tool); content.UpdateLayout();
                    Assert(((FrameworkElement)window.FindName("HardwoodOptionsPanel")).Visibility == Visibility.Collapsed, "Choosing a tool did not hide the hardwood bar.");
                    Assert(options.Visibility == Visibility.Visible && options.ActualHeight == TextureStudio.ContextualToolOptionsBar.RowHeight
                        && options.TransformToAncestor(content).TransformBounds(new Rect(options.RenderSize)) == optionsBounds,
                        "Switching tools collapsed or moved the attached options bar.");
                    if (tool == ArtworkTool.Type)
                        Assert(((FrameworkElement)window.FindName("TextOptionsBar")).Visibility == Visibility.Visible
                            && ((FrameworkElement)window.FindName("PreviewContextLabel")).Visibility == Visibility.Collapsed,
                            "Text tool options overlap the default context label.");
                    Assert(Mapping() == before, $"Hiding hardwood bar moved or scaled the court at {width}x{height}, {viewport}, zoom {zoom}, tool {tool}.");
                    Assert(window.CreateProject().ToJsonString() == project, "Switching tool bars changed the court document.");
                    window.ShowHardwoodTools(true); content.UpdateLayout();
                    Assert(Mapping() == before, $"Showing hardwood bar moved or scaled the court at {width}x{height}, {viewport}, zoom {zoom}.");
                }
            }
        }
        finally
        {
            canvas.Layers.Remove(logo); canvas.SelectedLayer = null; canvas.Viewport = null; canvas.Fit();
            Layout(window, width, 800); window.ShowHardwoodTools(true); content.UpdateLayout();
        }
    }

    private static void CheckToolInspectorTabs(StudioWindow window, string logoPath)
    {
        var canvas = window.Canvas;
        var logo = new ArtworkLayer { Path = logoPath, Image = StudioImages.Load(logoPath, 144), X = 2500, Y = 1400 };
        try
        {
            foreach (var selected in new[] { false, true })
            {
                if (selected) { canvas.Layers.Add(logo); canvas.SelectedLayer = logo; }
                foreach (var section in new[] { "paint", "logos", "export", "import" })
                {
                    window.SwitchSection(section); Layout(window, 1200, 800);
                    var project = window.CreateProject().ToJsonString();
                    foreach (var (name, tool) in new[] { ("Move", ArtworkTool.Move), ("Transform", ArtworkTool.Transform), ("Eyedropper", ArtworkTool.Eyedropper), ("Hand", ArtworkTool.Hand), ("Zoom", ArtworkTool.Zoom), ("Paint", ArtworkTool.Bucket), ("Text", ArtworkTool.Type) })
                    {
                        var button = (Button)window.FindName(name + "ToolButton");
                        if (tool == ArtworkTool.Transform) Assert(button.IsEnabled == (selected && section != "import"), "Transform availability depends on the inspector tab.");
                        if (tool == ArtworkTool.Eyedropper) Assert(button.IsEnabled == (section != "import"), "Eyedropper availability is incorrect.");
                        if (!button.IsEnabled) continue;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(window.Section == section && canvas.Tool == tool, $"{name} changed the {section} inspector tab or failed to select its tool.");
                        if (selected && section != "import" && tool is ArtworkTool.Move or ArtworkTool.Transform)
                        {
                            Assert(canvas.EditingEnabled && ((FrameworkElement)window.FindName("TransformOptionsBar")).Visibility == Visibility.Visible, "Logo editing depends on the inspector tab.");
                            Assert(canvas.BeginArtworkGesture(canvas.ToScreen(logo.Center)), "Logo gesture cannot start outside the Logos tab."); canvas.CancelGesture();
                        }
                        ((Button)window.FindName("HardwoodToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(window.Section == section && ((FrameworkElement)window.FindName("HardwoodOptionsPanel")).Visibility == Visibility.Visible, "Hardwood changed the inspector tab or failed to open.");
                        Assert(window.CreateProject().ToJsonString() == project, "Changing left tools edited the court.");
                    }
                    Assert(((FrameworkElement)window.FindName("PaintPanel")).Visibility == (section == "paint" ? Visibility.Visible : Visibility.Collapsed)
                        && ((FrameworkElement)window.FindName("LogoPanel")).Visibility == (section == "logos" ? Visibility.Visible : Visibility.Collapsed), "Tool buttons changed the visible inspector content.");
                }
            }
        }
        finally { canvas.Layers.Remove(logo); canvas.SelectedLayer = null; window.SwitchSection("paint"); window.ShowHardwoodTools(); }
        Console.WriteLine("PASS left toolbar: every available tool preserves Colors & Lines/Logos/Export/Import with and without a selected logo; Move/Transform gestures work outside Logos; Hardwood preserves the tab and opens its adjustments.");
    }

    private static void CheckExportPixel(string path, Point point, Color expected)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); var bytes = new byte[4];
        image.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), bytes, 4, 0);
        Assert(bytes[2] == expected.R && bytes[1] == expected.G && bytes[0] == expected.B && bytes[3] == expected.A, "Exported hardwood boundary/color differs from preview.");
    }
}
