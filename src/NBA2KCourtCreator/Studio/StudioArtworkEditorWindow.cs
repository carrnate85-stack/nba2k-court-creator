using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextureStudio;
using TextureStudio.Models;
using TwoK.Canvas.Hosting;

namespace NBA2KCourtCreator.Studio;

// Based on Canvas.CompactHost: the shared control owns every editing tool and draft.
public sealed class StudioArtworkEditorWindow : Window
{
    public CanvasEditor Editor { get; }
    private readonly Func<CanvasEditResult, CancellationToken, Task> _apply;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _accept = new() { Content = "Apply", MinWidth = 90, Margin = new(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private bool _closed, _applying, _closeRequested;
    public StudioArtworkEditorWindow(TextureDocument original, string name, bool dark, Func<CanvasEditResult, CancellationToken, Task> apply)
    {
        _apply = apply;
        Title = "Edit Artwork - " + name; Width = 1120; Height = 780; MinWidth = 920; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Editor = new CanvasEditor(new CanvasEditorOptions
        {
            DisplayName = name, ShowMenus = false, ShowDocumentTabs = true, ShowWindowControls = false,
            AllowFileOperations = false, ShowDocumentPanel = false, ShowMipMapsPanel = false,
            InspectorWidth = 320, Preferences = new() { Theme = dark ? "Dark" : "Light" },
            Tools = new HashSet<ToolMode> { ToolMode.Move, ToolMode.RectSelect, ToolMode.Lasso,
                ToolMode.PolygonLasso, ToolMode.MagicWand, ToolMode.ColorRange, ToolMode.Brush,
                ToolMode.Eraser, ToolMode.Bucket, ToolMode.Eyedropper, ToolMode.Type,
                ToolMode.Rectangle, ToolMode.Hand, ToolMode.Zoom },
            Presets = CourtPresets()
        });
        Resources.MergedDictionaries.Add(Editor.Resources);
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var layout = new Grid(); layout.SetResourceReference(Panel.BackgroundProperty, "WindowBrush");
        layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(Editor);
        var footer = new DockPanel { Margin = new(16, 10, 16, 10), MinHeight = 36 };
        Grid.SetRow(footer, 1);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        _accept.Click += async (_, _) => { try { await ApplyAsync(); DialogResult = true; } catch (Exception error) { if (!_closed) _status.Text = error.Message; } };
        actions.Children.Add(cancel); actions.Children.Add(_accept); DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions); footer.Children.Add(_status); layout.Children.Add(footer); Content = layout;
        try { Editor.BeginEdit(original); }
        catch { Editor.Dispose(); _lifetime.Dispose(); throw; }
        Editor.Commands.Changed += (_, change) => _status.Text = $"{change.Kind}: {change.Label}";
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closing += (_, e) =>
        {
            if (_applying) { _closeRequested = true; _lifetime.Cancel(); Editor.Cancel(); e.Cancel = true; return; }
            Editor.Cancel();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); Editor.Dispose(); layout.Children.Remove(Editor); _lifetime.Dispose(); };
    }
    public async Task ApplyAsync()
    {
        if (_closed || _applying) throw new InvalidOperationException("The artwork session is closed or applying.");
        _applying = true; _accept.IsEnabled = false;
        try
        {
            var result = await Editor.AcceptAsync(_lifetime.Token);
            if (_closed) throw new OperationCanceledException("The artwork editor was closed.");
            Editor.IsEnabled = false;
            await _apply(result, _lifetime.Token);
        }
        finally
        {
            _applying = false; if (!_closed) { _accept.IsEnabled = true; if (!Editor.IsDisposed) Editor.IsEnabled = true; }
            if (_closeRequested && !_closed) await Dispatcher.InvokeAsync(Close);
        }
    }
    internal static TeamColorPreset[] CourtPresets() => new[] { ("Court green", new RgbColor(25, 88, 63)),
        ("White court markings", new RgbColor(255, 255, 255)), ("Black court graphics", new RgbColor(0, 0, 0)) }
        .Select((item, index) => new TeamColorPreset(new Guid(index + 1, 0, 0, new byte[8]), item.Item1,
            new ColorLayerSettings(ColorAdjustmentKind.SelectiveRecolor,
                Recolor: new(new RgbColor(255, 255, 255), item.Item2, PreserveShading: true)))).ToArray();
}
