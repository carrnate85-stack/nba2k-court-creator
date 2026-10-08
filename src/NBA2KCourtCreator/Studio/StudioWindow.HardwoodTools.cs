using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TextureStudio;
using TextureStudio.Models;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private PreparedFloor? _preparedMainFloor, _preparedTwoPointFloor;
    private HardwoodTextureSettings _mainHardwoodSettings = new(), _twoPointHardwoodSettings = new();
    private bool _hardwoodToolActive, _editingTwoPointHardwood, _writingHardwoodValues;
    private readonly List<Action> _hardwoodPeers = [];
    private readonly Dictionary<TextBox, Func<bool>> _hardwoodNumberCommits = [];
    private readonly List<Action> _cancelHardwoodNumbers = [];
    private readonly DispatcherTimer _hardwoodPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private JsonObject? _hardwoodUndoBefore;
    private long _hardwoodPreviewRevision;
    private sealed class HardwoodOptionsHost(ToolOptionsContext context) : IContextualToolOptionsHost
    {
        public ToolOptionsContext Context { get; } = context;
        public event EventHandler? ContextChanged { add { } remove { } }
    }

    private void ConfigureHardwoodTools()
    {
        HardwoodOptions.Host = new HardwoodOptionsHost(new(ToolMode.Move, "Hardwood", [
            new("target", "", CreateHardwoodTarget),
            new("choose", "", CreateHardwoodChooser),
            new("brightness", "Brightness", () => CreateHardwoodSlider("brightness", -100, 100)),
            new("contrast", "Contrast", () => CreateHardwoodSlider("contrast", -100, 100)),
            new("saturation", "Saturation", () => CreateHardwoodSlider("saturation", -100, 100)),
            new("scale", "Grain scale", () => CreateHardwoodSlider("scale", 50, 200)),
            new("rotation", "Rotation", () => CreateHardwoodSlider("rotation", -180, 180))
        ]));
        // The host uses a scrollable row and a persistent reset action instead of the toolkit's overflow menu.
        foreach (var button in ((DockPanel)HardwoodOptions.Content).Children.OfType<Button>()) button.Visibility = Visibility.Collapsed;
        foreach (var caption in ((DockPanel)HardwoodOptions.Content).Children.OfType<StackPanel>()) caption.Visibility = Visibility.Collapsed;
        _hardwoodPreviewTimer.Tick += async (_, _) => { _hardwoodPreviewTimer.Stop(); await Guard(RefreshHardwoodPreviewAsync); };
        Closed += (_, _) => { ++_hardwoodPreviewRevision; _hardwoodPreviewTimer.Stop(); HardwoodOptions.Dispose(); _hardwoodPeers.Clear(); _hardwoodNumberCommits.Clear(); _cancelHardwoodNumbers.Clear(); };
    }

    private FrameworkElement CreateHardwoodTarget()
    {
        var input = new ComboBox { ItemsSource = new[] { "Main hardwood", "Two-point hardwood" }, Width = 155, Height = 28, MinHeight = 28, Padding = new Thickness(5, 2, 5, 2) };
        _hardwoodPeers.Add(() => { input.SelectedIndex = _editingTwoPointHardwood ? 1 : 0; input.ToolTip = _editingTwoPointHardwood ? "Left and right two-point areas: " + (_twoPointFloor?.Name ?? "Use main hardwood") : "Main hardwood: " + (_floor?.Name ?? "Choose a hardwood"); });
        input.SelectedIndex = _editingTwoPointHardwood ? 1 : 0;
        input.SelectionChanged += (_, _) => { if (_writingHardwoodValues) return; CommitHardwoodGesture(); _editingTwoPointHardwood = input.SelectedIndex == 1; RefreshHardwoodValues(); };
        return input;
    }

    private FrameworkElement CreateHardwoodChooser()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button { Content = "Choose", Height = 28, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2) };
        AutomationProperties.SetName(choose, "Choose hardwood");
        choose.Click += async (_, _) => await Guard(() => OpenFloorCatalogAsync(_editingTwoPointHardwood));
        var clear = new Button { Content = "Use main", Height = 28, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "Remove the second hardwood and restore the main hardwood / two-point colors" };
        AutomationProperties.SetName(clear, "Remove two-point hardwood"); clear.Click += ClearTwoPointFloorClick;
        void Update()
        {
            var floor = _editingTwoPointHardwood ? _twoPointFloor : _floor;
            choose.ToolTip = (_editingTwoPointHardwood ? "Two-point hardwood: " : "Main hardwood: ") + (floor?.Name ?? (_editingTwoPointHardwood ? "Use main hardwood" : "Choose a hardwood")) + "\nChoose a texture from the catalog or add an image.";
            choose.IsEnabled = CanChangeDocument && PendingLogoImports == 0;
            clear.Visibility = _editingTwoPointHardwood && _twoPointFloor is not null ? Visibility.Visible : Visibility.Collapsed;
            clear.IsEnabled = CanChangeDocument && PendingLogoImports == 0;
        }
        _hardwoodPeers.Add(Update); Update(); row.Children.Add(choose); row.Children.Add(clear); return row;
    }

    private FrameworkElement CreateHardwoodSlider(string key, int minimum, int maximum)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Width = 90, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Tag = key };
        slider.SetResourceReference(StyleProperty, "CanvasLayerSlider");
        AutomationProperties.SetName(slider, "Hardwood " + key);
        var value = new Button { Width = 48, Height = 24, Margin = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Click to enter {key} ({minimum} to {maximum})" };
        value.SetResourceReference(StyleProperty, "CanvasActionButton");
        value.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(value, "Enter hardwood " + key);
        var input = new TextBox { Width = 48, Height = 24, MinHeight = 24, Margin = new Thickness(0), Padding = new Thickness(3, 0, 3, 0), FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Tag = "HardwoodNumber:" + key };
        AutomationProperties.SetName(input, "Hardwood " + key + " exact value");
        var numberHost = new Grid { VerticalAlignment = VerticalAlignment.Center }; numberHost.Children.Add(value); numberHost.Children.Add(input);
        var editing = false; var second = false; long version = 0; PreparedFloor? owner = null;
        bool Current() => second == _editingTwoPointHardwood && version == _documentVersion && ReferenceEquals(owner, second ? _preparedTwoPointFloor : _preparedMainFloor);
        void Cancel() { editing = false; input.Visibility = Visibility.Collapsed; value.Visibility = Visibility.Visible; input.ClearValue(Control.BorderBrushProperty); }
        bool Commit()
        {
            if (!editing) return true;
            if (!Current()) { Cancel(); return true; }
            if (!CanChangeDocument) return false;
            if (!int.TryParse(input.Text.Trim().TrimEnd('%', '°'), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) || number < minimum || number > maximum)
            { input.SetResourceReference(Control.BorderBrushProperty, "WarningTextBrush"); SetStatus($"Enter {key} from {minimum} to {maximum}."); return false; }
            slider.Value = number; CommitHardwoodGesture(); Cancel(); return true;
        }
        _hardwoodNumberCommits.Add(input, Commit); _cancelHardwoodNumbers.Add(Cancel);
        value.Click += (_, _) =>
        {
            if (!CanChangeDocument || !slider.IsEnabled) return;
            try { CommitHardwoodNumberInputs(); }
            catch (InvalidOperationException error) { SetStatus(error.Message); return; }
            CommitHardwoodGesture();
            second = _editingTwoPointHardwood; version = _documentVersion; owner = second ? _preparedTwoPointFloor : _preparedMainFloor;
            editing = true; input.Text = slider.Value.ToString("0", CultureInfo.CurrentCulture);
            value.Visibility = Visibility.Collapsed; input.Visibility = Visibility.Visible; input.Focus(); input.SelectAll();
        };
        input.LostKeyboardFocus += (_, _) => Commit();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (Commit()) value.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); value.Focus(); e.Handled = true; }
        };
        void Update()
        {
            if (editing && !Current()) Cancel();
            var settings = _editingTwoPointHardwood ? _twoPointHardwoodSettings : _mainHardwoodSettings;
            slider.Value = ReadSetting(settings, key); value.Content = slider.Value.ToString("0") + (key == "rotation" ? "°" : "%");
            slider.IsEnabled = CanChangeDocument && (_editingTwoPointHardwood ? _twoPointFloor : _floor) is not null;
            value.IsEnabled = input.IsEnabled = slider.IsEnabled;
        }
        _hardwoodPeers.Add(Update); Update();
        slider.ValueChanged += (_, _) =>
        {
            if (_writingHardwoodValues || !slider.IsEnabled || !CanChangeDocument) return;
            FinishLogoOpacity();
            _hardwoodUndoBefore ??= CreateProject();
            var settings = _editingTwoPointHardwood ? _twoPointHardwoodSettings : _mainHardwoodSettings;
            settings = WriteSetting(settings, key, (int)Math.Round(slider.Value));
            if (_editingTwoPointHardwood) _twoPointHardwoodSettings = settings; else _mainHardwoodSettings = settings;
            ++_hardwoodPreviewRevision; RefreshHardwoodValues();
            _dirty = true; _hardwoodPreviewTimer.Stop(); _hardwoodPreviewTimer.Start();
            if (!slider.IsMouseCaptureWithin) CommitHardwoodGesture();
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitHardwoodGesture()));
        slider.LostKeyboardFocus += (_, _) => CommitHardwoodGesture();
        row.Children.Add(slider); row.Children.Add(numberHost); return row;
    }

    private void CommitHardwoodNumberInputs()
    {
        foreach (var commit in _hardwoodNumberCommits.Values)
            if (!commit()) throw new InvalidOperationException("Correct the hardwood number or press Escape before continuing.");
    }
    private async void ResetHardwoodTextureClick(object sender, RoutedEventArgs e)
    {
        foreach (var cancel in _cancelHardwoodNumbers) cancel();
        await Guard(() => SetHardwoodTextureAsync(new(), _editingTwoPointHardwood));
    }

    private static int ReadSetting(HardwoodTextureSettings settings, string key) => key switch
    { "brightness" => settings.Brightness, "contrast" => settings.Contrast, "saturation" => settings.Saturation, "scale" => settings.Scale, _ => settings.Rotation };
    private static HardwoodTextureSettings WriteSetting(HardwoodTextureSettings settings, string key, int value) => key switch
    { "brightness" => settings with { Brightness = value }, "contrast" => settings with { Contrast = value }, "saturation" => settings with { Saturation = value }, "scale" => settings with { Scale = value }, _ => settings with { Rotation = value } };

    private void CommitHardwoodGesture()
    {
        if (_hardwoodUndoBefore is not { } before) return;
        _hardwoodUndoBefore = null;
        if (before.ToJsonString() != CreateProject().ToJsonString())
        { RecordUndo(before); _dirty = true; if (_recovery is not null && !_closePending) { _recoveryTimer.Stop(); _recoveryTimer.Start(); } }
    }

    private void RefreshHardwoodValues()
    {
        _writingHardwoodValues = true;
        try { foreach (var update in _hardwoodPeers) update(); }
        finally { _writingHardwoodValues = false; }
        HardwoodResetButton.IsEnabled = CanChangeDocument && (_editingTwoPointHardwood ? _twoPointFloor : _floor) is not null;
    }

    public void ShowHardwoodTools(bool twoPoint = false)
    {
        CommitHardwoodGesture(); SwitchSection("paint"); CourtCanvas.Tool = TwoK.Studio.ArtworkTool.Move;
        _hardwoodToolActive = true; _editingTwoPointHardwood = twoPoint; RefreshHardwoodValues(); RefreshToolState();
    }
    private void HardwoodToolClick(object sender, RoutedEventArgs e) => ShowHardwoodTools();

    private void RefreshHardwoodBar()
    {
        if (HardwoodOptions is null) return;
        var visible = _hardwoodToolActive && _section == "paint";
        if (visible && HardwoodOptionsPanel.Visibility != Visibility.Visible && PresentationSource.FromVisual(this) is not null)
        {
            var drop = new TranslateTransform(0, -40); HardwoodOptionsPanel.RenderTransform = drop;
            drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(150)));
        }
        HardwoodOptions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HardwoodOptionsPanel.Visibility = HardwoodOptions.Visibility;
        if (!visible) { HardwoodOptions.CloseOverflow(); foreach (var cancel in _cancelHardwoodNumbers) cancel(); }
        HardwoodOptions.IsEnabled = CanChangeDocument;
        if (visible) { TransformOptionsBar.Visibility = Visibility.Collapsed; PreviewContextLabel.Visibility = Visibility.Collapsed; }
        HardwoodToolButton.SetResourceReference(Control.BackgroundProperty, visible ? "AccentDarkBrush" : "PanelBrush");
        RefreshHardwoodValues();
    }

    private Rect HardwoodRectangle()
    {
        var bounds = _geometry!["gameUv"]!["hardwoodBounds"]!.AsArray();
        return new Rect(Number(bounds[0]), Number(bounds[1]), Number(bounds[2]), Number(bounds[3]));
    }
    private async Task RefreshHardwoodPreviewAsync()
    {
        var revision = _hardwoodPreviewRevision;
        var main = _preparedMainFloor; var secondary = _preparedTwoPointFloor;
        var mainSettings = _mainHardwoodSettings; var secondarySettings = _twoPointHardwoodSettings;
        var rectangle = HardwoodRectangle();
        var drawings = await Task.Run(() => (
            Main: main is null ? null : HardwoodTextureSettings.CreateDrawing(main.Image, rectangle, _courtSurface!, mainSettings),
            Second: secondary is null ? null : HardwoodTextureSettings.CreateDrawing(secondary.Image, rectangle, _twoPointSurface!, secondarySettings)));
        if (_closed || revision != _hardwoodPreviewRevision || !ReferenceEquals(main, _preparedMainFloor) || !ReferenceEquals(secondary, _preparedTwoPointFloor)) return;
        _hardwoodDrawing = drawings.Main; _twoPointDrawing = drawings.Second; RebuildBackground();
    }

    public async Task SetHardwoodTextureAsync(HardwoodTextureSettings settings, bool twoPoint = false)
    {
        if (!CanChangeDocument || (twoPoint ? _preparedTwoPointFloor : _preparedMainFloor) is not { } prepared)
            throw new InvalidOperationException("Choose a hardwood before adjusting its texture.");
        settings = HardwoodTextureSettings.Read(new JsonObject { ["textureSettings"] = settings.ToJson() });
        CommitHardwoodGesture(); var version = _documentVersion; var previewRevision = _hardwoodPreviewRevision;
        var drawing = await Task.Run(() => HardwoodTextureSettings.CreateDrawing(prepared.Image, HardwoodRectangle(), twoPoint ? _twoPointSurface! : _courtSurface!, settings));
        if (!CanChangeDocument || version != _documentVersion || previewRevision != _hardwoodPreviewRevision || !ReferenceEquals(prepared, twoPoint ? _preparedTwoPointFloor : _preparedMainFloor)) return;
        var before = CreateProject(); ++_hardwoodPreviewRevision;
        if (twoPoint) { _twoPointHardwoodSettings = settings; _twoPointDrawing = drawing; }
        else { _mainHardwoodSettings = settings; _hardwoodDrawing = drawing; }
        if (before.ToJsonString() != CreateProject().ToJsonString()) { RecordUndo(before); Changed(); }
        RefreshHardwoodValues();
    }
    internal Task FlushHardwoodPreviewAsync() { CommitHardwoodGesture(); _hardwoodPreviewTimer.Stop(); return RefreshHardwoodPreviewAsync(); }
}
