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

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private PreparedFloor? _preparedMainFloor, _preparedTwoPointFloor;
    private HardwoodTextureSettings _mainHardwoodSettings = new(), _twoPointHardwoodSettings = new();
    private bool _hardwoodToolActive = true;
    private bool _editingTwoPointHardwood, _writingHardwoodValues;
    private readonly List<Action> _hardwoodPeers = [];
    private readonly Dictionary<TextBox, Func<bool>> _hardwoodNumberCommits = [];
    private readonly List<Action> _cancelHardwoodNumbers = [];
    private readonly DispatcherTimer _hardwoodPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private JsonObject? _hardwoodUndoBefore;
    private long _hardwoodPreviewRevision;
    private void ConfigureHardwoodTools()
    {
        HardwoodEditTarget.SelectionChanged += HardwoodEditTargetChanged;
        HardwoodOptionsPanel.SizeChanged += (_, _) => ArrangeHardwoodOptions();
        foreach (var (label, key, minimum, maximum) in new[] {
            ("Brightness", "brightness", -100, 100), ("Contrast", "contrast", -100, 100),
            ("Saturation", "saturation", -100, 100), ("Grain scale", "scale", 50, 200), ("Rotation", "rotation", -180, 180) })
        {
            var column = HardwoodOptions.ColumnDefinitions.Count;
            HardwoodOptions.ColumnDefinitions.Add(new ColumnDefinition());
            var control = CreateHardwoodSlider(label, key, minimum, maximum);
            Grid.SetColumn(control, column); HardwoodOptions.Children.Add(control);
        }
        _hardwoodPreviewTimer.Tick += async (_, _) => { _hardwoodPreviewTimer.Stop(); await Guard(RefreshHardwoodPreviewAsync); };
        Closed += (_, _) => { ++_hardwoodPreviewRevision; _hardwoodPreviewTimer.Stop(); _hardwoodPeers.Clear(); _hardwoodNumberCommits.Clear(); _cancelHardwoodNumbers.Clear(); };
    }

    private void ArrangeHardwoodOptions()
    {
        var compact = HardwoodOptionsPanel.ActualWidth < 640;
        Grid.SetRow(HardwoodEditTarget, compact ? 0 : 1);
        Grid.SetColumn(HardwoodOptions, compact ? 0 : 1);
        Grid.SetColumnSpan(HardwoodOptions, compact ? 2 : 1);
        HardwoodEditTarget.Margin = compact ? new Thickness(8, 6, 8, 0) : new Thickness(8, 0, 8, 0);
    }

    private void HardwoodEditTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_writingHardwoodValues) return;
        var twoPoint = HardwoodEditTarget.SelectedIndex == 1 && _twoPointHardwoodEnabled;
        if (!CanChangeDocument) { RefreshHardwoodValues(); return; }
        try
        {
            CommitHardwoodNumberInputs(); CommitHardwoodGesture();
            _editingTwoPointHardwood = twoPoint; RefreshHardwoodValues();
        }
        catch (InvalidOperationException error) { SetStatus(error.Message); RefreshHardwoodValues(); }
    }

    private void EnableTwoPointHardwoodChecked(object sender, RoutedEventArgs e)
    {
        if (_writingHardwoodValues) return;
        try { SetTwoPointHardwoodEnabled(true); ShowHardwoodTools(true); }
        catch (InvalidOperationException error) { SetStatus(error.Message); RefreshHardwoodValues(); }
    }
    private void DisableTwoPointHardwoodClick(object sender, RoutedEventArgs e)
    {
        try { SetTwoPointHardwoodEnabled(false); ShowHardwoodTools(); }
        catch (InvalidOperationException error) { SetStatus(error.Message); RefreshHardwoodValues(); }
    }
    private async void TwoPointCatalogClick(object sender, RoutedEventArgs e) => await Guard(() => OpenFloorCatalogAsync(true));

    private FrameworkElement CreateHardwoodSlider(string label, string key, int minimum, int maximum)
    {
        var group = new Grid { Margin = new Thickness(5, 4, 5, 4) };
        group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); group.RowDefinitions.Add(new RowDefinition());
        var caption = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(2, 0, 0, 2) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); group.Children.Add(caption);
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(row, 1); group.Children.Add(row);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, MinWidth = 18, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Tag = key };
        slider.SetResourceReference(StyleProperty, "CanvasLayerSlider");
        AutomationProperties.SetName(slider, "Hardwood " + key);
        var value = new Button { Width = 40, Height = 24, Margin = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Click to enter {key} ({minimum} to {maximum})" };
        value.SetResourceReference(StyleProperty, "CanvasActionButton");
        value.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(value, "Enter hardwood " + key);
        var input = new TextBox { Width = 40, Height = 24, MinHeight = 24, Margin = new Thickness(0), Padding = new Thickness(3, 0, 3, 0), FontSize = 11,
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
        Grid.SetColumn(numberHost, 1); row.Children.Add(slider); row.Children.Add(numberHost); return group;
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
        if (!_twoPointHardwoodEnabled) _editingTwoPointHardwood = false;
        _writingHardwoodValues = true;
        try
        {
            HardwoodEditTarget.SelectedIndex = _editingTwoPointHardwood ? 1 : 0;
            TwoPointEditTarget.IsEnabled = _twoPointHardwoodEnabled;
            HardwoodEditTarget.IsEnabled = CanChangeDocument;
            foreach (var update in _hardwoodPeers) update(); RefreshHardwoodSelectionCard();
        }
        finally { _writingHardwoodValues = false; }
        HardwoodResetButton.IsEnabled = CanChangeDocument && (_editingTwoPointHardwood ? _twoPointFloor : _floor) is not null;
    }

    private void RefreshHardwoodSelectionCard()
    {
        FloorThumbnail.Source = _preparedMainFloor?.Thumbnail;
        SelectedCourtText.Text = _floor?.Name ?? "Loading court library...";
        SelectedCourtCard.ToolTip = "Main hardwood: " + SelectedCourtText.Text + "\nBrowse and adjust this texture.";
        TwoPointThumbnail.Source = _preparedTwoPointFloor?.Thumbnail;
        TwoPointCourtText.Text = _twoPointFloor?.Name ?? "Choose 2-point hardwood";
        TwoPointCourtCard.ToolTip = "Left and right two-point areas: " + TwoPointCourtText.Text + "\nBrowse and adjust this texture.";
        TwoPointHardwoodCheckBox.IsChecked = _twoPointHardwoodEnabled;
        TwoPointHardwoodCheckBox.Visibility = _twoPointHardwoodEnabled ? Visibility.Collapsed : Visibility.Visible;
        TwoPointSelector.Visibility = _twoPointHardwoodEnabled ? Visibility.Visible : Visibility.Collapsed;
        DisableTwoPointHardwoodButton.Visibility = TwoPointSelector.Visibility;
        var enabled = CanChangeDocument && PendingLogoImports == 0;
        SelectedCourtCard.IsEnabled = TwoPointCourtCard.IsEnabled = TwoPointHardwoodCheckBox.IsEnabled = DisableTwoPointHardwoodButton.IsEnabled = enabled;
        SelectedCourtCard.SetResourceReference(Control.BorderBrushProperty, _hardwoodToolActive && !_editingTwoPointHardwood ? "AccentBrightBrush" : "BorderBrush");
        TwoPointCourtCard.SetResourceReference(Control.BorderBrushProperty, _hardwoodToolActive && _editingTwoPointHardwood ? "AccentBrightBrush" : "BorderBrush");
    }

    public void ShowHardwoodTools(bool twoPoint = false)
    {
        CommitHardwoodGesture(); CourtCanvas.CancelGesture(); CourtCanvas.Tool = TwoK.Studio.ArtworkTool.Move;
        _hardwoodToolActive = true; _editingTwoPointHardwood = twoPoint; RefreshHardwoodValues(); RefreshToolState();
    }
    private void HardwoodToolClick(object sender, RoutedEventArgs e) => ShowHardwoodTools();

    private void RefreshHardwoodBar()
    {
        if (HardwoodOptions is null) return;
        var visible = _hardwoodToolActive;
        if (visible && HardwoodOptionsPanel.Visibility != Visibility.Visible && PresentationSource.FromVisual(this) is not null)
        {
            var drop = new TranslateTransform(0, -40); HardwoodOptionsPanel.RenderTransform = drop;
            drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(150)));
        }
        HardwoodOptions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HardwoodOptionsPanel.Visibility = HardwoodOptions.Visibility;
        if (!visible) { foreach (var cancel in _cancelHardwoodNumbers) cancel(); }
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
            Second: secondary is null ? null : HardwoodTextureSettings.CreateDrawing(secondary.Image, rectangle, _courtSurface!, secondarySettings)));
        if (_closed || revision != _hardwoodPreviewRevision || !ReferenceEquals(main, _preparedMainFloor) || !ReferenceEquals(secondary, _preparedTwoPointFloor)) return;
        _hardwoodDrawing = drawings.Main; _twoPointDrawing = drawings.Second; RebuildBackground();
    }

    public async Task SetHardwoodTextureAsync(HardwoodTextureSettings settings, bool twoPoint = false)
    {
        if (!CanChangeDocument || (twoPoint ? _preparedTwoPointFloor : _preparedMainFloor) is not { } prepared)
            throw new InvalidOperationException("Choose a hardwood before adjusting its texture.");
        settings = HardwoodTextureSettings.Read(new JsonObject { ["textureSettings"] = settings.ToJson() });
        CommitHardwoodGesture(); var version = _documentVersion; var previewRevision = _hardwoodPreviewRevision;
        var drawing = await Task.Run(() => HardwoodTextureSettings.CreateDrawing(prepared.Image, HardwoodRectangle(), _courtSurface!, settings));
        if (!CanChangeDocument || version != _documentVersion || previewRevision != _hardwoodPreviewRevision || !ReferenceEquals(prepared, twoPoint ? _preparedTwoPointFloor : _preparedMainFloor)) return;
        var before = CreateProject(); ++_hardwoodPreviewRevision;
        if (twoPoint) { _twoPointHardwoodSettings = settings; _twoPointDrawing = drawing; }
        else { _mainHardwoodSettings = settings; _hardwoodDrawing = drawing; }
        if (before.ToJsonString() != CreateProject().ToJsonString()) { RecordUndo(before); Changed(); }
        RefreshHardwoodValues();
    }
    internal Task FlushHardwoodPreviewAsync() { CommitHardwoodGesture(); _hardwoodPreviewTimer.Stop(); return RefreshHardwoodPreviewAsync(); }
}
