using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private sealed class OffscreenKeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static async Task CheckKeyboardCommands(string output)
    {
        var failures = new List<string>();
        var source = new OffscreenKeySource();
        var pixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128); image.Freeze();
        void KeyPress(ArtworkCanvas canvas, Key key) => canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        void Verify(string name, Action work)
        { try { work(); } catch (Exception error) { failures.Add(name + ": " + error.Message); } }
        (ArtworkCanvas Canvas, ArtworkLayer First, ArtworkLayer Second) Fixture()
        {
            var canvas = new ArtworkCanvas { EditingEnabled = true };
            canvas.Measure(new Size(1200, 700)); canvas.Arrange(new Rect(0, 0, 1200, 700));
            var first = new ArtworkLayer { Name = "First", Image = image, X = 1700, Y = 1000, Width = 900, Height = 700, Rotation = 23 };
            var second = new ArtworkLayer { Name = "Second", Image = image, X = 5200, Y = 2300, Width = 450, Height = 550 };
            canvas.Layers.Add(first); canvas.Layers.Add(second); canvas.SelectedLayer = first;
            return (canvas, first, second);
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        foreach (var (key, delta) in new[] { (Key.Left, new Vector(-step, 0)), (Key.Right, new Vector(step, 0)), (Key.Up, new Vector(0, -step)), (Key.Down, new Vector(0, step)) })
        Verify("nudge interrupts drag / " + key, () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture(); var ended = 0; var commits = new List<ArtworkGestureEventArgs>();
            canvas.TransformPreviewEnded += (_, _) => ++ended; canvas.TransformCommitted += (_, args) => commits.Add(args);
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start); canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            KeyPress(canvas, key); canvas.ContinueArtworkGesture(start + new Vector(90, 50)); canvas.CommitArtworkGesture();
            var expected = original with { X = original.X + delta.X, Y = original.Y + delta.Y };
            Assert(first.Capture() == expected && commits.Count == 1 && commits[0].Before == original && commits[0].After == expected && ended == 2,
                "Nudge retained/overwrote the pending drag or recorded transient undo state.");
        });
        foreach (var (key, property) in new[] { (Key.Right, nameof(ArtworkLayer.X)), (Key.Down, nameof(ArtworkLayer.Y)) })
        Verify("reentrant nudge selection / " + property, () =>
        {
            var (canvas, first, second) = Fixture(); var original = first.Capture(); var other = second.Capture(); var commits = 0; var triggered = false;
            canvas.TransformCommitted += (_, _) => ++commits;
            first.PropertyChanged += (_, args) => { if (!triggered && args.PropertyName == property) { triggered = true; canvas.SelectedLayer = second; } };
            KeyPress(canvas, key);
            Assert(triggered && first.Capture() == original && second.Capture() == other && commits == 0, "Nudge callback changed another selection's geometry/history or left a partial edit.");
        });
        foreach (var unavailable in new[] { "hidden", "zero opacity", "hidden artwork", "disabled", "editing disabled", "removed", "no image" })
        Verify("unavailable artwork / " + unavailable, () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture(); var commits = 0; var deletes = 0;
            switch (unavailable)
            {
                case "hidden": first.Visible = false; break;
                case "zero opacity": first.Opacity = 0; break;
                case "hidden artwork": canvas.ShowArtwork = false; break;
                case "disabled": canvas.IsEnabled = false; break;
                case "editing disabled": canvas.EditingEnabled = false; break;
                case "removed": canvas.Layers.Remove(first); canvas.SelectedLayer = first; break;
                case "no image": first.Image = null; break;
            }
            canvas.TransformCommitted += (_, _) => ++commits;
            canvas.DeleteRequested += (_, _) => ++deletes;
            KeyPress(canvas, Key.Right);
            KeyPress(canvas, Key.Delete);
            Assert(first.Capture() == original && commits == 0 && deletes == 0, "Keyboard input edited/deleted unavailable artwork.");
            Assert(!canvas.BeginArtworkGesture(canvas.ToScreen(first.Center)), "Unavailable selected box can still start a drag.");
        });
        Verify("cancel callback disables nudge", () =>
        {
            var (canvas, first, _) = Fixture(); var original = first.Capture(); var commits = 0;
            canvas.TransformCommitted += (_, _) => ++commits;
            var start = canvas.ToScreen(first.Center); canvas.BeginArtworkGesture(start); canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            canvas.TransformPreviewEnded += (_, _) => canvas.EditingEnabled = false;
            KeyPress(canvas, Key.Right);
            Assert(!canvas.EditingEnabled && first.Capture() == original && commits == 0, "Nudge continued after cancellation disabled editing.");
        });

        var logoPath = Path.Combine(output, "keyboard-logo.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(logoPath)) encoder.Save(file);
        var window = new StudioWindow(true);
        var flags = new Dictionary<string, bool> { ["_initialized"] = false, ["_ready"] = false, ["_syncing"] = true, ["_restoring"] = true, ["_saving"] = true, ["_catalogBusy"] = true, ["_closed"] = true, ["_closePending"] = true };
        var actions = new[] { "delete", "flip", "flip-y", "center", "duplicate", "copy-x", "copy-y", "reorder" };
        FieldInfo Field(string name) => typeof(StudioWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Command(string action)
        {
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
        }
        async Task Prepare()
        {
            await window.NewProjectAsync(); await window.AddLogoAsync(logoPath, "First"); await window.AddLogoAsync(logoPath, "Second");
            window.SwitchSection("logos"); Layout(window, 1440, 900); window.Canvas.SelectedLayer = window.Canvas.Layers[0];
            window.Canvas.SelectedLayer.X=1700;window.Canvas.SelectedLayer.Y=1000;
        }
        try
        {
            await window.InitializeAsync();
            foreach (var (flag, blocked) in flags)
            foreach (var action in actions)
            {
                await Prepare(); var field = Field(flag); var previous = field.GetValue(window);
                var before = window.CreateProject().ToJsonString(); var selected = window.Canvas.SelectedLayer;
                var undo = ((ICollection)Field("_undo").GetValue(window)!).Count; var redo = ((ICollection)Field("_redo").GetValue(window)!).Count;
                field.SetValue(window, blocked);
                try
                {
                    Command(action);
                    if(window.RenameProject("Blocked rename"))failures.Add(flag+" / rename: blocked rename reported success.");
                    if (window.CreateProject().ToJsonString() != before || !ReferenceEquals(window.Canvas.SelectedLayer, selected) ||
                        ((ICollection)Field("_undo").GetValue(window)!).Count != undo || ((ICollection)Field("_redo").GetValue(window)!).Count != redo || !Equals(field.GetValue(window),blocked))
                        failures.Add(flag + " / " + action + ": blocked command changed the document, selection or history.");
                }
                catch (Exception error) { failures.Add(flag + " / " + action + ": " + error.Message); }
                finally { field.SetValue(window, previous); }
            }
            foreach (var action in actions)
            {
                await Prepare(); var foreign = new ArtworkLayer { Image = image }; window.Canvas.SelectedLayer = foreign;
                var before = window.CreateProject().ToJsonString();var pose=foreign.Capture();var flip=foreign.FlipX;
                Verify("foreign selection / " + action, () => { Command(action); Assert(window.CreateProject().ToJsonString() == before && ReferenceEquals(window.Canvas.SelectedLayer, foreign) && foreign.Capture()==pose && foreign.FlipX==flip, "Command accepted a layer outside this document."); });
            }
            foreach(var action in actions)
            {
                await Prepare();var active=window.Canvas.SelectedLayer!;var before=window.CreateProject().ToJsonString();
                var undo=((ICollection)Field("_undo").GetValue(window)!).Count;
                var pointer=window.Canvas.ToScreen(active.Center);window.Canvas.BeginArtworkGesture(pointer);window.Canvas.ContinueArtworkGesture(pointer+new Vector(60,30));
                EventHandler block=(_,_)=>Field("_saving").SetValue(window,true);
                window.Canvas.TransformPreviewEnded+=block;
                try
                {
                    Command(action);
                    Verify("cancel callback blocks / "+action,()=>Assert(window.CreateProject().ToJsonString()==before && ReferenceEquals(window.Canvas.SelectedLayer,active) &&
                        ((ICollection)Field("_undo").GetValue(window)!).Count==undo,"Command continued after cancellation made the document read-only."));
                }
                finally {window.Canvas.TransformPreviewEnded-=block;Field("_saving").SetValue(window,false);}
            }
            foreach(var forward in new[]{true,false})
            {
                await Prepare();var active=forward?window.Canvas.Layers.Last():window.Canvas.Layers.First();window.Canvas.SelectedLayer=active;
                var pointer=window.Canvas.ToScreen(active.Center);window.Canvas.BeginArtworkGesture(pointer);window.Canvas.ContinueArtworkGesture(pointer+new Vector(60,30));
                var preview=active.Capture();var undo=((ICollection)Field("_undo").GetValue(window)!).Count;
                ((Button)window.FindName(forward?"MoveLogoDownButton":"MoveLogoUpButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Verify("boundary reorder / "+forward,()=>Assert(active.Capture()==preview && ReferenceEquals(window.Canvas.SelectedLayer,active) &&
                    ((ICollection)Field("_undo").GetValue(window)!).Count==undo,"A no-op reorder interrupted a pending drag or changed history."));
                window.Canvas.CancelGesture();
            }
            await Prepare();
            var first = window.Canvas.SelectedLayer!; var beforeNudge = window.CreateProject().ToJsonString(); var original = first.Capture();
            var start = window.Canvas.ToScreen(first.Center); window.Canvas.BeginArtworkGesture(start); window.Canvas.ContinueArtworkGesture(start + new Vector(60, 30));
            KeyPress(window.Canvas, Key.Right); var afterNudge = window.CreateProject().ToJsonString();
            if (first.Capture() != (original with { X = original.X + step })) failures.Add("Native window nudge used transient coordinates.");
            await window.UndoAsync(); Verify("native nudge undo", () => Assert(window.CreateProject().ToJsonString() == beforeNudge, "Native nudge undo restored the drag preview."));
            await window.UndoAsync(true); Verify("native nudge redo", () => Assert(window.CreateProject().ToJsonString() == afterNudge, "Native nudge redo differs."));
        }
        finally { window.Close(); }
        Assert(failures.Count == 0, "Keyboard/command failures:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS native keyboard/command guards: routed arrow nudges cancel pending drags and use exact undo/redo; reentrant X/Y selection/disable callbacks leave no partial edit; unavailable artwork cannot nudge/delete/drag; eight logo commands preserve document/selection/history/readiness across eight blocked states, reject foreign layers and recheck readiness after cancellation; boundary reorder is a no-op; blocked renames report failure. Synthetic presentation source only; no native windows opened.");
    }
}
