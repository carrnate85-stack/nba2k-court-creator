using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            new("choose", "", () => { var button = new Button { Content = "Choose", Height = 28, Padding = new Thickness(8, 2, 8, 2) }; button.Click += async (_, _) => await Guard(() => OpenFloorCatalogAsync(_editingTwoPointHardwood)); return button; }),
            new("brightness", "Brightness", () => CreateHardwoodSlider("brightness", -100, 100)),
            new("contrast", "Contrast", () => CreateHardwoodSlider("contrast", -100, 100)),
            new("saturation", "Saturation", () => CreateHardwoodSlider("saturation", -100, 100)),
            new("scale", "Grain scale", () => CreateHardwoodSlider("scale", 50, 200)),
            new("rotation", "Rotation", () => CreateHardwoodSlider("rotation", -180, 180)),
            new("reset", "", () => { var button = new Button { Content = "Reset", Height = 28, Padding = new Thickness(8, 2, 8, 2) }; button.Click += async (_, _) => await Guard(() => SetHardwoodTextureAsync(new(), _editingTwoPointHardwood)); return button; })
        ]));
        _hardwoodPreviewTimer.Tick += async (_, _) => { _hardwoodPreviewTimer.Stop(); await Guard(RefreshHardwoodPreviewAsync); };
        Closed += (_, _) => { ++_hardwoodPreviewRevision; _hardwoodPreviewTimer.Stop(); HardwoodOptions.Dispose(); _hardwoodPeers.Clear(); };
    }

    private FrameworkElement CreateHardwoodTarget()
    {
        var input = new ComboBox { ItemsSource = new[] { "Main hardwood", "Two-point hardwood" }, Width = 155, Height = 28, MinHeight = 28, Padding = new Thickness(5, 2, 5, 2) };
        _hardwoodPeers.Add(() => input.SelectedIndex = _editingTwoPointHardwood ? 1 : 0);
        input.SelectedIndex = _editingTwoPointHardwood ? 1 : 0;
        input.SelectionChanged += (_, _) => { if (_writingHardwoodValues) return; CommitHardwoodGesture(); _editingTwoPointHardwood = input.SelectedIndex == 1; RefreshHardwoodValues(); };
        return input;
    }

    private FrameworkElement CreateHardwoodSlider(string key, int minimum, int maximum)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Width = 90, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Tag = key };
        slider.SetResourceReference(StyleProperty, "CanvasLayerSlider");
        AutomationProperties.SetName(slider, "Hardwood " + key);
        var value = new TextBlock { Width = 40, FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        void Update()
        {
            var settings = _editingTwoPointHardwood ? _twoPointHardwoodSettings : _mainHardwoodSettings;
            slider.Value = ReadSetting(settings, key); value.Text = slider.Value.ToString("0") + (key == "rotation" ? "°" : "%");
            slider.IsEnabled = CanChangeDocument && (_editingTwoPointHardwood ? _twoPointFloor : _floor) is not null;
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
        row.Children.Add(slider); row.Children.Add(value); return row;
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
        if (visible && HardwoodOptions.Visibility != Visibility.Visible && PresentationSource.FromVisual(this) is not null)
        {
            var drop = new TranslateTransform(0, -40); HardwoodOptions.RenderTransform = drop;
            drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(150)));
        }
        HardwoodOptions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) HardwoodOptions.CloseOverflow();
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
