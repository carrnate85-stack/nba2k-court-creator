using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckCanvasGestures(string output)
    {
        var failures = new List<string>();
        var pixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        image.Freeze();
        var transitions = new Dictionary<string, Action<ArtworkCanvas, ArtworkLayer, ArtworkLayer>>
        {
            ["select another"] = (canvas, _, second) => canvas.SelectedLayer = second,
            ["deselect"] = (canvas, _, _) => canvas.SelectedLayer = null,
            ["remove selected"] = (canvas, first, _) => canvas.Layers.Remove(first),
            ["clear layers"] = (canvas, _, _) => canvas.Layers.Clear(),
            ["disable editing"] = (canvas, _, _) => canvas.EditingEnabled = false,
            ["disable element"] = (canvas, _, _) => canvas.IsEnabled = false,
            ["hide artwork"] = (canvas, _, _) => canvas.ShowArtwork = false,
            ["hide selected"] = (_, first, _) => first.Visible = false,
            ["zero opacity"] = (_, first, _) => first.Opacity = 0,
            ["unload"] = (canvas, _, _) => canvas.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)),
            ["zoom"] = (canvas, _, _) => canvas.ChangeZoom(1.25, new Point(420, 260)),
            ["actual size"] = (canvas, _, _) => canvas.ActualSize(),
            ["fit"] = (canvas, _, _) => canvas.Fit(),
            ["viewport"] = (canvas, _, _) => canvas.Viewport = new Rect(4096, 0, 4096, 4096),
            ["document size"] = (canvas, _, _) => canvas.DocumentSize = new Size(4096, 2048),
            ["canvas size"] = (canvas, _, _) => { canvas.Measure(new Size(1000, 600)); canvas.Arrange(new Rect(0, 0, 1000, 600)); },
        };
        (ArtworkCanvas Canvas, ArtworkLayer First, ArtworkLayer Second) Fixture()
        {
            var canvas = new ArtworkCanvas { EditingEnabled = true };
            canvas.Measure(new Size(1200, 700)); canvas.Arrange(new Rect(0, 0, 1200, 700));
            var first = new ArtworkLayer { Name = "First", Image = image, X = 1700, Y = 1000, Width = 900, Height = 700, Rotation = 23 };
            var second = new ArtworkLayer { Name = "Second", Image = image, X = 5200, Y = 2300, Width = 450, Height = 550, Rotation = -12 };
            canvas.Layers.Add(first); canvas.Layers.Add(second); canvas.SelectedLayer = first;
            return (canvas, first, second);
        }
        void Verify(string name, Action work)
        {
            try { work(); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); }
        }
        Point GestureStart(ArtworkCanvas canvas, ArtworkLayer layer, string gesture) => canvas.ToScreen(gesture switch
        {
            "resize" => layer.Center + TransformGeometry.Rotate(new Vector(layer.Width / 2, layer.Height / 2), layer.Rotation),
            "rotate" => layer.Center + TransformGeometry.Rotate(new Vector(0, -layer.Height / 2 - 28 / canvas.Scale), layer.Rotation),
            _ => layer.Center,
        });
        foreach (var gesture in new[] { "move", "resize", "rotate" })
        foreach (var (name, transition) in transitions)
        {
            Verify(gesture + " / " + name, () =>
            {
                var (canvas, first, second) = Fixture();
                var original = first.Capture(); var other = second.Capture();
                var ended = 0; var committed = 0;
                canvas.TransformPreviewEnded += (_, _) => ++ended;
                canvas.TransformCommitted += (_, _) => ++committed;
                var start = GestureStart(canvas, first, gesture);
                Assert(canvas.BeginArtworkGesture(start), "Gesture did not begin.");
                canvas.ContinueArtworkGesture(start + new Vector(55, -25));
                Assert(first.Capture() != original, "Fixture did not move.");
                transition(canvas, first, second);
                canvas.ContinueArtworkGesture(start + new Vector(90, 40));
                canvas.CommitArtworkGesture(); canvas.CancelGesture();
                Assert(first.Capture() == original, "Original logo kept an uncommitted pose.");
                Assert(second.Capture() == other, "Another logo received the original logo's pose.");
                Assert(ended == 1 && committed == 0, "Cancellation generated missing/duplicate preview or history events.");
                if (name == "hide selected") Assert(!first.Visible, "Cancellation undid visibility.");
                if (name == "zero opacity") Assert(first.Opacity == 0, "Cancellation undid opacity.");
                if (name == "hide artwork") Assert(canvas.HitArtwork(first.Center) is null && !canvas.BeginArtworkGesture(start), "Hidden artwork remains editable.");
            });
        }
        Verify("reentrant move selection", () =>
        {
            var (canvas, first, second) = Fixture(); var original = first.Capture(); var other = second.Capture();
            var triggered = false;
            first.PropertyChanged += (_, args) =>
            {
                if (!triggered && args.PropertyName == nameof(ArtworkLayer.X)) { triggered = true; canvas.SelectedLayer = second; }
            };
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30)); canvas.CommitArtworkGesture();
            Assert(triggered && first.Capture() == original && second.Capture() == other, "Selection callback redirected or partially applied a move.");
        });
        foreach (var property in new[] { nameof(ArtworkLayer.X), nameof(ArtworkLayer.Y), nameof(ArtworkLayer.Width), nameof(ArtworkLayer.Height) })
        Verify("reentrant resize selection / " + property, () =>
        {
            var (canvas, first, second) = Fixture(); var original = first.Capture(); var other = second.Capture();
            var triggered = false;
            first.PropertyChanged += (_, args) =>
            {
                if (!triggered && args.PropertyName == property) { triggered = true; canvas.SelectedLayer = second; }
            };
            var corner = first.Center + TransformGeometry.Rotate(new Vector(first.Width / 2, first.Height / 2), first.Rotation);
            var start = canvas.ToScreen(corner); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(70, 40)); canvas.CommitArtworkGesture();
            Assert(triggered && first.Capture() == original && second.Capture() == other, "Selection callback redirected or partially applied a resize.");
        });
        Verify("reentrant rotation selection", () =>
        {
            var (canvas, first, second) = Fixture(); var original = first.Capture(); var other = second.Capture();
            var triggered = false;
            first.PropertyChanged += (_, args) =>
            {
                if (!triggered && args.PropertyName == nameof(ArtworkLayer.Rotation)) { triggered = true; canvas.SelectedLayer = second; }
            };
            var start = GestureStart(canvas, first, "rotate"); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(70, 40)); canvas.CommitArtworkGesture();
            Assert(triggered && first.Capture() == original && second.Capture() == other, "Selection callback redirected or partially applied rotation.");
        });
        Verify("unchanged selection keeps gesture", () =>
        {
            var (canvas, first, _) = Fixture(); var ended = 0; var committed = 0;
            canvas.TransformPreviewEnded += (_, _) => ++ended;
            canvas.TransformCommitted += (_, _) => ++committed;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30)); var final = first.Capture();
            canvas.SelectedLayer = first; canvas.CommitArtworkGesture();
            Assert(first.Capture() == final && ended == 1 && committed == 1, "A no-op selection interrupted the drag or duplicated history.");
        });
        Verify("unchanged view keeps gesture", () =>
        {
            var (canvas, first, _) = Fixture(); var ended = 0; var committed = 0;
            canvas.TransformPreviewEnded += (_, _) => ++ended;
            canvas.TransformCommitted += (_, _) => ++committed;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30)); var final = first.Capture();
            canvas.Viewport = canvas.Viewport; canvas.DocumentSize = canvas.DocumentSize;
            foreach (var factor in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity }) canvas.ChangeZoom(factor);
            canvas.ChangeZoom(1, new Point(420, 260)); canvas.CommitArtworkGesture();
            Assert(first.Capture() == final && ended == 1 && committed == 1 && double.IsFinite(canvas.Scale) && canvas.Zoom == 1,
                "A no-op/invalid view update interrupted the drag, changed the mapping or duplicated history.");
        });
        Verify("pointer-centered zoom after cancel", () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture();
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            var center = new Point(420, 260); var document = canvas.ToDocument(center);
            canvas.ChangeZoom(1.25, center);
            Assert(first.Capture() == original, "Zoom retained a transient pose.");
            Assert((canvas.ToScreen(document) - center).Length < 0.000001, "Gesture cancellation broke pointer-centered zoom.");
            var next = canvas.ToScreen(first.Center); Assert(canvas.BeginArtworkGesture(next), "New gesture could not start after zoom.");
            canvas.ContinueArtworkGesture(next + new Vector(40, -20)); var final = first.Capture(); canvas.CommitArtworkGesture();
            Assert(Math.Abs(final.X - original.X - 40 / canvas.Scale) < 0.000001 && Math.Abs(final.Y - original.Y + 20 / canvas.Scale) < 0.000001,
                "New gesture used the previous view's coordinate mapping.");
        });
        Verify("resize before commit without another move", () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture(); var ended = 0; var committed = 0;
            canvas.TransformPreviewEnded += (_, _) => ++ended;
            canvas.TransformCommitted += (_, _) => ++committed;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            canvas.Measure(new Size(1000, 600)); canvas.Arrange(new Rect(0, 0, 1000, 600));
            canvas.CommitArtworkGesture();
            Assert(first.Capture() == original && ended == 1 && committed == 0, "Mouse release committed a preview made under the old window size.");
        });
        Verify("immutable commit event", () =>
        {
            var (canvas, first, _) = Fixture(); ArtworkGestureEventArgs? committed = null;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30)); var final = first.Capture();
            canvas.TransformPreviewEnded += (_, _) => first.X += 1000;
            canvas.TransformCommitted += (_, args) => committed = args;
            canvas.CommitArtworkGesture();
            Assert(committed is not null && committed.After == final, "Observer callback changed the committed snapshot.");
        });
        foreach (var (name, transition) in transitions)
        Verify("cancel callback / " + name, () =>
        {
            var (canvas, first, second) = Fixture(); var original = first.Capture(); var other = second.Capture();
            bool? restarted = null; var committed = 0;
            canvas.TransformPreviewEnded += (_, _) => restarted = canvas.BeginArtworkGesture(canvas.ToScreen(first.Center));
            canvas.TransformCommitted += (_, _) => ++committed;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start);
            canvas.ContinueArtworkGesture(start + new Vector(60, 30)); transition(canvas, first, second);
            canvas.ContinueArtworkGesture(start + new Vector(90, 40)); canvas.CommitArtworkGesture();
            Assert(restarted == false && first.Capture() == original && second.Capture() == other && committed == 0, "Cancellation callback started a cross-owner gesture.");
        });
        Verify("new gesture from property callback", () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture(); var start = canvas.ToScreen(first.Center);
            var triggered = false; var ended = 0; var committed = 0;
            canvas.TransformPreviewEnded += (_, _) => ++ended;
            canvas.TransformCommitted += (_, _) => ++committed;
            first.PropertyChanged += (_, args) =>
            {
                if (!triggered && args.PropertyName == nameof(ArtworkLayer.X)) { triggered = true; Assert(canvas.BeginArtworkGesture(start), "Replacement gesture was rejected after cancellation completed."); }
            };
            canvas.BeginArtworkGesture(start); canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            Assert(triggered && first.Capture() == original, "Old pointer update continued into a value-equal new gesture.");
            canvas.ContinueArtworkGesture(start + new Vector(25, -50)); var final = first.Capture(); canvas.CommitArtworkGesture();
            Assert(final != original && first.Capture() == final && ended == 2 && committed == 1, "Replacement gesture did not have independent commit/cancel semantics.");
        });
        var logoPath = Path.Combine(output, "gesture-logo.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(logoPath)) encoder.Save(file);
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            foreach (var action in new[] { "delete", "flip", "flip-y", "center", "duplicate", "copy-x", "copy-y", "reorder" })
            {
                await window.NewProjectAsync(); await window.AddLogoAsync(logoPath, "First"); await window.AddLogoAsync(logoPath, "Second");
                window.SwitchSection("logos"); Layout(window, 1440, 900);
                var first = window.Canvas.Layers[0]; first.X = 2100; first.Y = 1300; first.Width = 900; first.Height = 700;
                window.Canvas.SelectedLayer = first; var original = first.Capture(); var before = window.CreateProject().ToJsonString();
                var start = window.Canvas.ToScreen(first.Center); window.Canvas.BeginArtworkGesture(start);
                window.Canvas.ContinueArtworkGesture(start + new Vector(55, -25));
                switch (action)
                {
                    case "delete": window.DeleteSelectedLogo(); break;
                    case "flip": window.FlipSelectedLogo(); break;
                    case "flip-y": window.FlipSelectedLogo(true); break;
                    case "center": window.CenterSelectedLogo(); break;
                    case "duplicate": ((Button)window.FindName("DuplicateLogoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                    case "reorder": ((Button)window.FindName("MoveLogoDownButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                    default: ((MenuItem)window.FindName(action == "copy-x" ? "CopyXMenu" : "CopyYMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); break;
                }
                if (action is not ("delete" or "center") && first.Capture() != original) failures.Add(action + ": command retained the drag preview.");
                if (action is "duplicate" or "copy-x" or "copy-y")
                {
                    var copy = window.Canvas.Layers.Last();
                    var center = window.Canvas.Anchors.Single(anchor => anchor.Id == "court-center").Position;
                    var expectedX = action == "duplicate" ? original.X + 40 : action == "copy-x" ? center.X * 2 - original.X - original.Width : original.X;
                    var expectedY = action == "duplicate" ? original.Y + 40 : action == "copy-y" ? center.Y * 2 - original.Y - original.Height : original.Y;
                    if (copy.Capture() != new ArtworkState(expectedX, expectedY, original.Width, original.Height, original.Rotation)) failures.Add(action + ": copied the drag preview instead of committed coordinates.");
                }
                var after = window.CreateProject().ToJsonString();
                await window.UndoAsync();
                if (window.CreateProject().ToJsonString() != before) failures.Add(action + ": undo restored the transient drag pose.");
                await window.UndoAsync(true);
                if (window.CreateProject().ToJsonString() != after) failures.Add(action + ": redo did not restore the exact command.");
            }
        }
        finally { window.Close(); }
        Assert(failures.Count == 0, "Canvas gesture failures:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS canvas gesture lifecycle: selection/removal/disable/hide/unload and view changes cancel once; no-op/invalid zoom retains the gesture; pointer-centered zoom and new view mapping remain exact; no cross-logo or reentrant transform mutation; immutable commit events; delete/flip/center/duplicate/mirror/reorder use committed poses and exact undo/redo. All windows stayed invisible.");
    }
}
