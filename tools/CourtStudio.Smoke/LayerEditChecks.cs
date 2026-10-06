using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckLayerEdits(string output)
    {
        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            Layout(window, 1440, 900);
            foreach (var floor in window.Floors.Take(8)) await window.SelectFloorAsync(floor);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var undo = (List<JsonObject>)typeof(StudioWindow).GetField("_undo", flags)!.GetValue(window)!;
            var redo = (List<JsonObject>)typeof(StudioWindow).GetField("_redo", flags)!.GetValue(window)!;
            void Flush() => typeof(StudioWindow).GetMethod("FlushLayerHex", flags)!.Invoke(window, null);
            bool Pending() => (bool)typeof(StudioWindow).GetField("_layerHexFramePending", flags)!.GetValue(window)!;
            void Blur(TextBox input) => input.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, input, (StackPanel)window.FindName("LayersHost"))
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            var host = (StackPanel)window.FindName("LayersHost");
            var expander = host.Children.OfType<Expander>().First();
            Grid Row(string id) => host.Children.OfType<Expander>().SelectMany(group => ((StackPanel)group.Content).Children.OfType<Grid>()).Single(row => Equals(row.Tag, id));
            var allRows = host.Children.OfType<Expander>().SelectMany(group => ((StackPanel)group.Content).Children.OfType<Grid>()).ToArray();
            Assert(allRows.Length == 23, "Layer rows are missing.");
            var row = Row("paint-left");
            var hex = Descendants<TextBox>(row).Single();
            var toggle = Descendants<CheckBox>(row).Single();
            var colors = (StackPanel)hex.Parent;
            var layer = window.PaintLayers.First(item => item.Id == "paint-left");
            var beforeDrawing = window.Canvas.BackgroundDrawing;
            window.SetLayerSettings("paint-left", color: "#2266CC");
            Assert(ReferenceEquals(Row("paint-left"), row) && hex.IsDescendantOf(host)
                   && ReferenceEquals(host.Children[0], expander), "A color edit rebuilt the active layer row/hex input/accordion.");
            Assert(layer.Color == "#2266CC" && !ReferenceEquals(beforeDrawing, window.Canvas.BackgroundDrawing)
                   && undo.Count == 1 && Pending(), "Color edit did not immediately update the model/preview and queue one field frame.");
            Flush();
            Assert(hex.Text == "#2266CC", "In-place color edit left the hex input stale.");
            var history = undo.Count;
            for (var index = 0; index < 12; index++) window.SetLayerSettings("paint-left", color: $"#{index + 1:X6}");
            Assert(undo.Count == history + 12 && Pending() && hex.Text == "#2266CC", "Rapid edits lost history or refreshed fields before the coalesced frame.");
            Assert(allRows.All(item => ReferenceEquals(Row((string)item.Tag), item)), "Repeated edits replaced an unrelated row.");
            Flush(); Assert(hex.Text == "#00000C" && !Pending(), "Coalesced hex frame did not apply the latest value.");
            var state = window.CreateProject().ToJsonString(); beforeDrawing = window.Canvas.BackgroundDrawing; history = undo.Count;
            for (var index = 0; index < 1000; index++) window.SetLayerSettings("paint-left", visible: layer.Visible, color: layer.Color.ToLowerInvariant());
            Assert(undo.Count == history && window.CreateProject().ToJsonString() == state
                   && ReferenceEquals(beforeDrawing, window.Canvas.BackgroundDrawing) && !Pending(), "No-op settings changed history/document/preview or scheduled work.");
            hex.Text = "12";
            window.SetLayerSettings("paint-right", color: "#112233"); Flush();
            Assert(hex.Text == "12", "Editing another layer erased the current uncommitted text.");
            Assert(((Button)window.FindName("PinnedColorButton")).ToolTip.ToString()!.Contains("Right Paint"), "Selected color target did not follow the changed row.");
            foreach (var group in host.Children.OfType<Expander>())
            {
                var siblings = ((StackPanel)group.Content).Children.OfType<Grid>().ToArray();
                for (var index = 0; index < siblings.Length; index++)
                {
                    var resource = Equals(siblings[index].Tag, "paint-right") ? "AccentDarkBrush" : index % 2 == 0 ? "PanelRaisedBrush" : "PanelBrush";
                    Assert(ReferenceEquals(siblings[index].Background, window.FindResource(resource)), "Selecting a row erased alternating backgrounds or misplaced selection.");
                }
            }
            hex.Text = "abc"; history = undo.Count; Blur(hex);
            Assert(layer.Color == "#AABBCC" && hex.Text == "#AABBCC" && undo.Count == history + 1 && !Pending(), "Typed shorthand hex did not normalize with one undo action.");
            hex.Text = "invalid"; history = undo.Count; Blur(hex);
            Assert(hex.Text == layer.Color && undo.Count == history, "Invalid hex changed history or failed to restore the field.");
            history = undo.Count; toggle.IsChecked = false;
            Assert(!layer.Visible && colors.Visibility == Visibility.Collapsed && undo.Count == history + 1
                   && ReferenceEquals(row, Row("paint-left")), "Toggle did not hide controls in place with one action.");
            hex.Text = "#FF0000"; Blur(hex);
            Assert(layer.Color == "#AABBCC" && undo.Count == history + 1, "A hidden color field changed the layer.");
            window.SetLayerSettings("paint-left", visible: true); Flush();
            Assert(layer.Visible && colors.Visibility == Visibility.Visible && toggle.IsChecked == true && hex.Text == layer.Color,
                "Programmatic visibility failed to restore the same controls/color.");
            history = undo.Count; window.SetLayerSettings("paint-left", visible: false, color: "#223344"); Flush();
            Assert(undo.Count == history + 1 && !layer.Visible && layer.Color == "#223344" && colors.Visibility == Visibility.Collapsed,
                "Combined color/visibility edit did not produce exactly one action.");
            window.SetLayerSettings("paint-left", visible: true); Flush();
            history = undo.Count;
            window.SetLayerSettings("paint-left", color: "#345678"); Blur(hex);
            Assert(hex.Text == "#345678" && layer.Color == "#345678" && undo.Count == history + 1 && !Pending(), "Blur replayed a stale hex value over a pending selected color.");
            window.SetLayerSettings("paint-left", color: "#56789A");
            hex.Text = "fedcba"; Blur(hex); Flush();
            Assert(layer.Color == "#FEDCBA" && hex.Text == "#FEDCBA", "Pending readout work overwrote a newer typed value.");
            expander.IsExpanded = false; window.SetLayerSettings("paint-left", color: "#ABCDEF"); Flush();
            Assert(!expander.IsExpanded && ReferenceEquals(host.Children[0], expander), "A color edit reopened/replaced its accordion.");
            expander.IsExpanded = true;
            window.SwitchSection("logos"); window.SwitchSection("paint");
            Assert(ReferenceEquals(row, Row("paint-left")), "Tab switching replaced layer controls.");
            Snapshot(window, Path.Combine(output, "layer-edits-1440.png"), 1440, 900);
            Snapshot(window, Path.Combine(output, "layer-edits-1000.png"), 1000, 680);
            var staleHex = hex; var staleToggle = toggle;
            window.SetLayerSettings("paint-left", color: "#102030");
            await window.UndoAsync(); Flush();
            state = window.CreateProject().ToJsonString(); history = undo.Count; var redoCount = redo.Count;
            staleHex.Text = "#FFFFFF"; Blur(staleHex); staleToggle.IsChecked = !staleToggle.IsChecked;
            Assert(window.CreateProject().ToJsonString() == state && undo.Count == history && redo.Count == redoCount && !Pending(),
                "Detached control events changed the restored project/history.");
            window.SetLayerSettings("paint-left", color: layer.Color, visible: layer.Visible);
            Assert(redo.Count == redoCount, "No-op edit cleared redo history.");
            window.SetLayerSettings("paint-left", color: "#334455");
            var theme = typeof(StudioWindow).GetMethod("ThemeClick", flags)!;
            theme.Invoke(window, [null, new RoutedEventArgs()]);
            Assert(!Pending() && Descendants<TextBox>(Row("paint-left")).Single().Text == "#334455", "Theme rebuild left stale queued color work.");
            theme.Invoke(window, [null, new RoutedEventArgs()]);
            foreach (var current in host.Children.OfType<Expander>().SelectMany(group => ((StackPanel)group.Content).Children.OfType<Grid>()))
            {
                Assert(Descendants<TextBox>(current).Count() == 1 && Descendants<CheckBox>(current).Count() == 1, "Theme rebuild duplicated/missed layer controls.");
            }
            Assert(!window.IsVisible, "Layer-edit verification opened a native window.");
            window.SetLayerSettings("paint-left", color: "#445566"); Assert(Pending(), "Close regression did not queue field work.");
            window.Close(); Assert(!Pending(), "Closing retained the rendering subscription."); Flush();
            Console.WriteLine("PASS layer edits: stable rows/hex/accordions; latest-value frame coalescing; no-op history/redo/preview; typed/invalid/hidden hex; visibility; selection/stripes; pending blur/user typing; stale-event/restore/theme/close cancellation; compact renders. No native windows opened.");
        }
        finally { window.Close(); }
    }
}
