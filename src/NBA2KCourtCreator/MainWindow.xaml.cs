using NBA2KCourtCreator.Models;
using NBA2KCourtCreator.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfButton = System.Windows.Controls.Button;
using WpfBrush = System.Windows.Media.Brush;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace NBA2KCourtCreator;

public partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private readonly ObservableCollection<CourtLayerNode> _layerRoots = [];
    private readonly ObservableCollection<CourtLayerNode> _sectionLayerRoots = [];
    private readonly Dictionary<string, CourtLayerNode> _layersById = [];
    private readonly Dictionary<string, bool> _visibility = [];
    private readonly Dictionary<string, int[]> _colorOverrides = [];
    private readonly Dictionary<string, string> _nameOverrides = [];
    private readonly Dictionary<string, string> _templateColors = [];
    private readonly List<CustomFloorImage> _customFloorImages = [];
    private readonly List<TeamPalette> _teamPalettes = [];
    private readonly List<CourtPreset?> _presets = [];
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private string _templatePath = string.Empty;
    private string _previewPath = string.Empty;
    private string _presetsPath = string.Empty;
    private string _currentSection = "floors";
    private CourtLayerNode? _selectedLayer;
    private bool _syncing;
    private long _renderVersion;

    public ObservableCollection<CourtLayerNode> LayerRoots => _layerRoots;
    public ObservableCollection<CourtLayerNode> SectionLayerRoots => _sectionLayerRoots;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await LoadWorkspaceAsync();
    }

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            SetStatus("Loading court template...");
            using var response = await _backend.LoadAsync();
            ReadLoadResponse(response.RootElement);
            BuildLayerTree();
            RefreshSection();
            BuildPresetButtons();
            BuildPalettePanel();
            RefreshSelectionText();
            await RefreshPreviewAsync();
            SetStatus("Court workspace ready.");
        }
        catch (Exception ex)
        {
            ShowError("Startup", ex);
        }
    }

    private void ReadLoadResponse(JsonElement root)
    {
        _layersById.Clear();
        _visibility.Clear();
        _colorOverrides.Clear();
        _nameOverrides.Clear();
        _templateColors.Clear();
        _customFloorImages.Clear();
        _teamPalettes.Clear();
        _presets.Clear();

        _templatePath = root.GetProperty("templatePath").GetString() ?? string.Empty;
        _previewPath = root.GetProperty("previewPath").GetString() ?? string.Empty;
        _presetsPath = Path.Combine(_backend.ProjectRoot, "data", "court_presets.json");
        ProjectNameText.Text = Path.GetFileName(_templatePath);
        ProjectStateText.Text = "Loaded template";

        foreach (var layerJson in root.GetProperty("document").GetProperty("layers").EnumerateArray())
        {
            AddLayer(ReadLayer(layerJson, isCustomFloor: false));
        }

        if (root.TryGetProperty("customFloorLayers", out var customLayers))
        {
            foreach (var layerJson in customLayers.EnumerateArray())
            {
                AddLayer(ReadLayer(layerJson, isCustomFloor: true));
            }
        }

        if (root.TryGetProperty("visibility", out var visibility))
        {
            foreach (var item in visibility.EnumerateObject())
            {
                _visibility[item.Name] = item.Value.GetBoolean();
            }
        }

        if (root.TryGetProperty("customFloorImages", out var floorImages))
        {
            foreach (var imageJson in floorImages.EnumerateArray())
            {
                _customFloorImages.Add(new CustomFloorImage
                {
                    Id = imageJson.GetProperty("id").GetString() ?? string.Empty,
                    Name = imageJson.GetProperty("name").GetString() ?? string.Empty,
                    Path = imageJson.GetProperty("path").GetString() ?? string.Empty,
                    Bbox = imageJson.TryGetProperty("bbox", out var bbox) ? bbox.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [0, 0, 0, 0],
                    IsTemplate = imageJson.TryGetProperty("isTemplate", out var isTemplate) && isTemplate.GetBoolean(),
                });
            }
        }

        foreach (var floor in _customFloorImages.Where(image => image.IsTemplate))
        {
            if (_layersById.TryGetValue(floor.Id, out var layer))
            {
                layer.IsTemplateFloor = true;
            }
        }
        foreach (var layer in _layersById.Values.Where(IsFloorTemplateCategory))
        {
            layer.IsTemplateFloor = true;
        }

        if (root.TryGetProperty("teamPalettes", out var palettes))
        {
            foreach (var paletteJson in palettes.EnumerateArray())
            {
                var colors = new List<TeamColor>();
                if (paletteJson.TryGetProperty("colors", out var colorsJson))
                {
                    foreach (var colorJson in colorsJson.EnumerateArray())
                    {
                        colors.Add(new TeamColor
                        {
                            Name = colorJson.GetProperty("name").GetString() ?? string.Empty,
                            Hex = NormalizeHex(colorJson.GetProperty("hex").GetString() ?? string.Empty) ?? string.Empty,
                        });
                    }
                }

                _teamPalettes.Add(new TeamPalette
                {
                    League = paletteJson.GetProperty("league").GetString() ?? string.Empty,
                    Team = paletteJson.GetProperty("team").GetString() ?? string.Empty,
                    Colors = colors,
                });
            }
        }

        if (root.TryGetProperty("presets", out var presetsJson))
        {
            foreach (var presetJson in presetsJson.EnumerateArray())
            {
                _presets.Add(presetJson.ValueKind == JsonValueKind.Null
                    ? null
                    : JsonSerializer.Deserialize<CourtPreset>(presetJson.GetRawText()));
            }
        }

        while (_presets.Count < 5)
        {
            _presets.Add(null);
        }

        foreach (var layer in _layersById.Values)
        {
            layer.Visible = _visibility.GetValueOrDefault(layer.Id, layer.OriginalVisible);
            layer.DisplayName = FriendlyLayerName(layer);
        }
    }

    private CourtLayerNode ReadLayer(JsonElement layerJson, bool isCustomFloor)
    {
        return new CourtLayerNode
        {
            Id = layerJson.GetProperty("id").GetString() ?? string.Empty,
            Name = layerJson.GetProperty("name").GetString() ?? string.Empty,
            Kind = layerJson.GetProperty("kind").GetString() ?? string.Empty,
            ParentId = layerJson.TryGetProperty("parent_id", out var parent) && parent.ValueKind != JsonValueKind.Null ? parent.GetString() : null,
            PsdIndex = layerJson.GetProperty("psd_index").GetInt32(),
            Depth = layerJson.GetProperty("depth").GetInt32(),
            OriginalVisible = layerJson.GetProperty("visible").GetBoolean(),
            Opacity = layerJson.GetProperty("opacity").GetInt32(),
            BlendMode = layerJson.TryGetProperty("blend_mode", out var blendMode) ? blendMode.GetString() ?? string.Empty : string.Empty,
            Bbox = layerJson.TryGetProperty("bbox", out var bbox) ? bbox.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [0, 0, 0, 0],
            IsCustomFloor = isCustomFloor,
        };
    }

    private void AddLayer(CourtLayerNode layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Id)) return;
        _layersById[layer.Id] = layer;
        _visibility.TryAdd(layer.Id, layer.OriginalVisible);
    }

    private void BuildLayerTree()
    {
        _layerRoots.Clear();
        foreach (var layer in _layersById.Values)
        {
            layer.Children.Clear();
        }

        foreach (var layer in _layersById.Values.OrderBy(l => l.PsdIndex))
        {
            if (!string.IsNullOrWhiteSpace(layer.ParentId) && _layersById.TryGetValue(layer.ParentId, out var parent))
            {
                parent.Children.Add(layer);
            }
            else
            {
                _layerRoots.Add(layer);
            }
        }

        SortChildren(_layerRoots);
    }

    private void SortChildren(IList<CourtLayerNode> nodes)
    {
        var ordered = nodes.OrderBy(CourtSortKey).ThenBy(l => l.PsdIndex).ToList();
        nodes.Clear();
        foreach (var item in ordered)
        {
            nodes.Add(item);
            SortChildren(item.Children);
        }
    }

    private void RefreshSection()
    {
        _sectionLayerRoots.Clear();
        UpdateSectionUi();

        if (_currentSection == "floors")
        {
            var floorGroup = _layersById.Values.FirstOrDefault(IsCourtFloorGroup);
            if (floorGroup is null) return;
            var query = FloorSearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                foreach (var layer in floorGroup.Children)
                {
                    _sectionLayerRoots.Add(layer);
                }
                return;
            }

            foreach (var layer in Descendants(floorGroup).Where(layer => !layer.IsGroup && FloorMatches(layer, query)).OrderBy(CourtSortKey).ThenBy(l => l.DisplayName))
            {
                _sectionLayerRoots.Add(layer);
            }
            return;
        }

        if (_currentSection == "paint")
        {
            foreach (var layer in PaintAndLineRoots())
            {
                _sectionLayerRoots.Add(layer);
            }
            return;
        }
    }

    private void UpdateSectionUi()
    {
        var floors = _currentSection == "floors";
        var paint = _currentSection == "paint";
        var team = _currentSection == "team";
        var presets = _currentSection == "presets";
        var export = _currentSection == "export";

        WorkspaceTitle.Text = _currentSection switch
        {
            "paint" => "Paint & Lines",
            "team" => "Team Colors",
            "presets" => "Presets",
            "export" => "Export",
            _ => "Court Floors",
        };
        WorkspaceSubtitle.Text = _currentSection switch
        {
            "paint" => "Choose paint and line layers, then apply exact colors or team palette swatches.",
            "team" => "Search NBA and college palettes, then apply colors to the selected paint or line layer.",
            "presets" => "Load saved layouts or right click a preset to save the current court.",
            "export" => "Refresh, save, and export the current court preview.",
            _ => "Choose one court floor at a time, including custom and NBA 2K26 templates.",
        };
        LayerPanel.Header = paint ? "Paint & Lines" : "Court Floors";
        FloorSearchBox.Visibility = floors ? Visibility.Visible : Visibility.Collapsed;
        FloorActions.Visibility = floors ? Visibility.Visible : Visibility.Collapsed;
        ExportActions.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
        LayerPanel.Visibility = floors || paint ? Visibility.Visible : Visibility.Collapsed;
        SelectedLayerPanel.Visibility = floors || paint || team ? Visibility.Visible : Visibility.Collapsed;
        PalettePanel.Visibility = paint || team ? Visibility.Visible : Visibility.Collapsed;
        PresetPanel.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
        ExportPanel.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<CourtLayerNode> PaintAndLineRoots()
    {
        var wantedGroups = new[] { "paint colors", "lines" };
        foreach (var name in wantedGroups)
        {
            var group = _layersById.Values.FirstOrDefault(layer => layer.IsGroup && NormalizeName(layer.Name) == name);
            if (group is not null)
            {
                yield return group;
            }
        }

        foreach (var layer in _layersById.Values.Where(layer => !layer.IsGroup && NormalizeName(layer.Name) == "outside color").OrderBy(layer => layer.PsdIndex))
        {
            yield return layer;
        }
    }

    private bool FloorMatches(CourtLayerNode layer, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = string.Join(" ", new[]
        {
            layer.DisplayName,
            layer.Name,
            ParentOf(layer)?.DisplayName ?? string.Empty,
            ParentOf(layer)?.Name ?? string.Empty,
        }).ToLowerInvariant();
        return words.All(word => haystack.Contains(word.ToLowerInvariant()));
    }

    private object CourtSortKey(CourtLayerNode layer)
    {
        var parent = ParentOf(layer);
        if (parent is not null && IsCourtFloorGroup(parent))
        {
            return layer.IsCustomFloor ? 10000 : FloorNumber(layer.DisplayName);
        }

        if (parent is not null && NormalizeName(parent.Name) == "lines")
        {
            return NormalizeName(layer.Name) switch
            {
                "3 point lines" => -30,
                "college three" => -20,
                "high school three" => -10,
                _ => layer.PsdIndex,
            };
        }

        return layer.PsdIndex;
    }

    private static int FloorNumber(string value)
    {
        var match = Regex.Match(value, @"#\s*(\d+)|(\d+)");
        if (!match.Success) return 9999;
        return int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
    }

    private async void OnLayerSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedLayer = e.NewValue as CourtLayerNode;
        RefreshSelectionText();
        await LoadSelectedLayerColorAsync();
    }

    private void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string section }) return;
        if (!IsLoaded) return;
        _currentSection = section;
        RefreshSection();
    }

    private void OnFloorSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentSection == "floors")
        {
            RefreshSection();
        }
    }

    private void RefreshSelectionText()
    {
        _syncing = true;
        SelectedLayerText.Text = _selectedLayer is null ? "No layer selected." : _selectedLayer.DisplayName;
        VisibleCheck.IsEnabled = _selectedLayer is not null;
        VisibleCheck.IsChecked = _selectedLayer?.Visible ?? false;
        var colorable = _selectedLayer is not null && IsColorableLayer(_selectedLayer);
        HexBox.IsEnabled = colorable;
        SelectedColorBox.IsEnabled = colorable;
        SelectedColorBox.Background = HexToBrush(_selectedLayer?.ActiveHex);
        HexBox.Text = colorable ? _selectedLayer?.ActiveHex ?? string.Empty : string.Empty;
        _syncing = false;
    }

    private async Task LoadSelectedLayerColorAsync()
    {
        if (_selectedLayer is null || !IsColorableLayer(_selectedLayer)) return;
        if (_colorOverrides.TryGetValue(_selectedLayer.Id, out var overrideColor))
        {
            SetLayerHex(_selectedLayer, RgbToHex(overrideColor), rememberAsTemplate: false);
            RefreshSelectionText();
            return;
        }

        if (_templateColors.TryGetValue(_selectedLayer.Id, out var cached))
        {
            SetLayerHex(_selectedLayer, cached, rememberAsTemplate: false);
            RefreshSelectionText();
            return;
        }

        try
        {
            using var response = await _backend.SampleColorAsync(_selectedLayer.Id);
            if (response.RootElement.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.Array)
            {
                var rgb = color.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (rgb.Length >= 3)
                {
                    SetLayerHex(_selectedLayer, RgbToHex(rgb), rememberAsTemplate: true);
                    RefreshSelectionText();
                }
            }
        }
        catch
        {
            // Color sampling is helpful, but layer editing can continue without it.
        }
    }

    private async void OnLayerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var layer = _selectedLayer;
        if (FindParent<TreeViewItem>((DependencyObject)e.OriginalSource) is { DataContext: CourtLayerNode clickedLayer })
        {
            layer = clickedLayer;
            _selectedLayer = clickedLayer;
        }

        if (layer is null) return;
        await SetLayerVisibilityAsync(layer, !layer.Visible);
        e.Handled = true;
    }

    private void OnLayerRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<TreeViewItem>((DependencyObject)e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            e.Handled = true;
        }
    }

    private async void OnVisibleChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedLayer is null) return;
        await SetLayerVisibilityAsync(_selectedLayer, VisibleCheck.IsChecked == true);
    }

    private async Task SetLayerVisibilityAsync(CourtLayerNode layer, bool visible)
    {
        if (IsInsideCourtFloor(layer) && visible)
        {
            ShowOnlyCourtFloor(layer);
        }
        else
        {
            ApplyVisibility(layer, visible, includeChildren: layer.IsGroup);
            if (visible)
            {
                ShowAncestors(layer);
            }
        }

        RefreshSelectionText();
        await RefreshPreviewAsync();
    }

    private void ApplyVisibility(CourtLayerNode layer, bool visible, bool includeChildren)
    {
        _visibility[layer.Id] = visible;
        layer.Visible = visible;
        if (!includeChildren) return;
        foreach (var child in Descendants(layer))
        {
            _visibility[child.Id] = visible;
            child.Visible = visible;
        }
    }

    private void ShowOnlyCourtFloor(CourtLayerNode layer)
    {
        var group = CourtFloorGroupFor(layer);
        var selectedRoot = FloorRootFor(layer, group);
        if (group is null || selectedRoot is null)
        {
            ApplyVisibility(layer, true, includeChildren: layer.IsGroup);
            return;
        }
        if (IsFloorTemplateCategory(selectedRoot))
        {
            var selectedTemplate = layer.Id == selectedRoot.Id
                ? selectedRoot.Children.FirstOrDefault(child => child.Visible) ?? selectedRoot.Children.FirstOrDefault()
                : layer;

            ApplyVisibility(group, true, includeChildren: false);
            ShowAncestors(group);
            foreach (var option in group.Children)
            {
                ApplyVisibility(option, false, includeChildren: true);
            }

            ApplyVisibility(selectedRoot, true, includeChildren: false);
            if (selectedTemplate is not null && selectedTemplate.Id != selectedRoot.Id)
            {
                ApplyVisibility(selectedTemplate, true, includeChildren: selectedTemplate.IsGroup);
                ShowAncestors(selectedTemplate);
            }
            else
            {
                ShowAncestors(selectedRoot);
            }
            return;
        }

        ApplyVisibility(group, true, includeChildren: false);
        ShowAncestors(group);
        foreach (var option in group.Children)
        {
            ApplyVisibility(option, false, includeChildren: true);
        }
        ApplyVisibility(selectedRoot, true, includeChildren: selectedRoot.IsGroup);
        ShowAncestors(selectedRoot);
    }

    private void ShowAncestors(CourtLayerNode layer)
    {
        var parent = ParentOf(layer);
        while (parent is not null)
        {
            _visibility[parent.Id] = true;
            parent.Visible = true;
            parent = ParentOf(parent);
        }
    }

    private async void OnApplyHex(object sender, RoutedEventArgs e) => await ApplyHexAsync(HexBox.Text);

    private async void OnHexKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ApplyHexAsync(HexBox.Text);
            e.Handled = true;
        }
    }

    private async Task ApplyHexAsync(string hex)
    {
        if (_selectedLayer is null || !IsColorableLayer(_selectedLayer)) return;
        var normalized = NormalizeHex(hex);
        if (normalized is null)
        {
            SetStatus("Enter a 3 or 6 digit hex color.");
            return;
        }

        _colorOverrides[_selectedLayer.Id] = HexToRgb(normalized);
        SetLayerHex(_selectedLayer, normalized, rememberAsTemplate: false);
        RefreshSelectionText();
        await RefreshPreviewAsync();
    }

    private async void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null || !IsColorableLayer(_selectedLayer)) return;
        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (NormalizeHex(_selectedLayer.ActiveHex) is { } current)
        {
            var rgb = HexToRgb(current);
            dialog.Color = System.Drawing.Color.FromArgb(rgb[0], rgb[1], rgb[2]);
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await ApplyHexAsync($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private async void OnResetColor(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null) return;
        _colorOverrides.Remove(_selectedLayer.Id);
        if (_templateColors.TryGetValue(_selectedLayer.Id, out var templateHex))
        {
            SetLayerHex(_selectedLayer, templateHex, rememberAsTemplate: false);
        }
        else
        {
            _selectedLayer.ActiveHex = string.Empty;
        }
        RefreshSelectionText();
        await RefreshPreviewAsync();
    }

    private async void OnLoadPsd(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Load court PSD template",
            Filter = "Photoshop PSD (*.psd)|*.psd|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SetStatus("Loading selected court PSD...");
            using var response = await _backend.LoadAsync(dialog.FileName);
            ReadLoadResponse(response.RootElement);
            BuildLayerTree();
            RefreshSection();
            BuildPresetButtons();
            BuildPalettePanel();
            RefreshSelectionText();
            await RefreshPreviewAsync();
            SetStatus("Selected court PSD loaded.");
        }
        catch (Exception ex)
        {
            ShowError("Load PSD", ex);
        }
    }

    private async void OnRefreshPreview(object sender, RoutedEventArgs e) => await RefreshPreviewAsync();

    private async void OnResetDefault(object sender, RoutedEventArgs e)
    {
        _colorOverrides.Clear();
        _nameOverrides.Clear();
        foreach (var layer in _layersById.Values)
        {
            layer.DisplayName = FriendlyLayerName(layer);
            layer.ActiveHex = _templateColors.GetValueOrDefault(layer.Id, string.Empty);
            layer.Visible = layer.IsCustomFloor ? false : layer.OriginalVisible;
            _visibility[layer.Id] = layer.Visible;
        }

        RefreshSelectionText();
        BuildPalettePanel();
        await RefreshPreviewAsync();
        SetStatus("Court reset to the template default.");
    }

    private async void OnAddCustomFloor(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Add custom court floor",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SetStatus("Adding custom floor...");
            using var response = await _backend.AddFloorAsync(dialog.FileName);
            var root = response.RootElement;
            var layer = ReadLayer(root.GetProperty("layer"), isCustomFloor: true);
            AddLayer(layer);
            var imageJson = root.GetProperty("image");
            _customFloorImages.Add(new CustomFloorImage
            {
                Id = imageJson.GetProperty("id").GetString() ?? string.Empty,
                Name = imageJson.GetProperty("name").GetString() ?? string.Empty,
                Path = imageJson.GetProperty("path").GetString() ?? string.Empty,
                Bbox = imageJson.GetProperty("bbox").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                IsTemplate = false,
            });
            layer.DisplayName = FriendlyLayerName(layer);
            layer.Visible = false;
            _visibility[layer.Id] = false;
            BuildLayerTree();
            RefreshSection();
            SetStatus("Custom floor added.");
        }
        catch (Exception ex)
        {
            ShowError("Add Floor", ex);
        }
    }

    private async void OnRemoveCustomFloor(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null || !_selectedLayer.IsCustomFloor)
        {
            SetStatus("Select a custom floor to remove.");
            return;
        }
        if (_selectedLayer.IsTemplateFloor)
        {
            SetStatus("Template floors stay in the library.");
            return;
        }

        var layer = _selectedLayer;
        _layersById.Remove(layer.Id);
        _visibility.Remove(layer.Id);
        _colorOverrides.Remove(layer.Id);
        _nameOverrides.Remove(layer.Id);
        RemoveCustomFloorMetadata(layer.Id);
        BuildLayerTree();
        RefreshSection();
        _selectedLayer = null;
        RefreshSelectionText();
        await RefreshPreviewAsync();
        SetStatus("Custom floor removed.");
    }

    private void OnRenameLayer(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is null) return;
        var renamed = Prompt("Rename layer", "Layer name", _selectedLayer.DisplayName);
        if (string.IsNullOrWhiteSpace(renamed)) return;
        _nameOverrides[_selectedLayer.Id] = renamed.Trim();
        _selectedLayer.DisplayName = renamed.Trim();
        RefreshSelectionText();
    }

    private void OnOpenPsd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_templatePath) || !File.Exists(_templatePath))
        {
            SetStatus("Template PSD was not found.");
            return;
        }

        Process.Start(new ProcessStartInfo(_templatePath) { UseShellExecute = true });
    }

    private async void OnExportPng(object sender, RoutedEventArgs e)
    {
        await RefreshPreviewAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
        {
            SetStatus("Preview is not ready to export.");
            return;
        }

        var dialog = new WpfSaveFileDialog
        {
            Title = "Export court preview PNG",
            FileName = "court-preview.png",
            Filter = "PNG image (*.png)|*.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.Copy(_previewPath, dialog.FileName, overwrite: true);
        SetStatus("PNG exported.");
    }

    private void OnSaveState(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "Save court state",
            FileName = "court-state.json",
            Filter = "Court state (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(CurrentPreset("State"), _jsonOptions));
        SetStatus("State saved.");
    }

    private async void OnLoadState(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Load court state",
            Filter = "Court state (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        var preset = JsonSerializer.Deserialize<CourtPreset>(File.ReadAllText(dialog.FileName));
        if (preset is null) return;
        await ApplyPresetAsync(preset);
        SetStatus("State loaded.");
    }

    private void BuildPresetButtons()
    {
        PresetHost.Children.Clear();
        PresetSectionHost.Children.Clear();
        for (var index = 0; index < 5; index++)
        {
            var slot = index;
            var button = PresetButton(slot, new Thickness(0, 0, slot == 4 ? 0 : 7, 0));
            PresetHost.Children.Add(button);
            PresetSectionHost.Children.Add(PresetButton(slot, new Thickness(0, 0, 0, 8)));
        }
    }

    private WpfButton PresetButton(int slot, Thickness margin)
    {
        var button = new WpfButton
            {
                Content = PresetName(slot),
            Margin = margin,
            };
        button.Click += async (_, _) => await LoadPresetAsync(slot);
        button.ContextMenu = PresetMenu(slot);
        button.PreviewMouseRightButtonDown += (_, args) =>
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            args.Handled = true;
        };
        return button;
    }

    private ContextMenu PresetMenu(int slot)
    {
        var menu = new ContextMenu();
        var save = new MenuItem { Header = "Save" };
        save.Click += (_, _) => SavePreset(slot);
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => RenamePreset(slot);
        menu.Items.Add(save);
        menu.Items.Add(rename);
        return menu;
    }

    private async Task LoadPresetAsync(int slot)
    {
        if (slot < 0 || slot >= _presets.Count || _presets[slot] is null)
        {
            SetStatus("That preset slot is empty.");
            return;
        }

        await ApplyPresetAsync(_presets[slot]!);
        SetStatus($"{PresetName(slot)} loaded.");
    }

    private async Task ApplyPresetAsync(CourtPreset preset)
    {
        _visibility.Clear();
        foreach (var layer in _layersById.Values)
        {
            var visible = preset.Visibility.GetValueOrDefault(layer.Id, layer.OriginalVisible);
            layer.Visible = visible;
            _visibility[layer.Id] = visible;
        }

        _colorOverrides.Clear();
        foreach (var item in preset.ColorOverrides)
        {
            _colorOverrides[item.Key] = item.Value;
            if (_layersById.TryGetValue(item.Key, out var layer))
            {
                SetLayerHex(layer, RgbToHex(item.Value), rememberAsTemplate: false);
            }
        }

        _nameOverrides.Clear();
        foreach (var item in preset.NameOverrides)
        {
            _nameOverrides[item.Key] = item.Value;
        }

        foreach (var layer in _layersById.Values)
        {
            layer.DisplayName = FriendlyLayerName(layer);
            if (!_colorOverrides.ContainsKey(layer.Id))
            {
                layer.ActiveHex = _templateColors.GetValueOrDefault(layer.Id, string.Empty);
            }
        }

        if (!string.IsNullOrWhiteSpace(preset.SelectedLayerId) && _layersById.TryGetValue(preset.SelectedLayerId, out var selected))
        {
            _selectedLayer = selected;
            SelectTreeItem(selected);
        }

        RefreshSelectionText();
        await RefreshPreviewAsync();
    }

    private void SavePreset(int slot)
    {
        while (_presets.Count <= slot)
        {
            _presets.Add(null);
        }

        var name = _presets[slot]?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Preset {slot + 1}";
        }

        _presets[slot] = CurrentPreset(name);
        SavePresetsFile();
        BuildPresetButtons();
        SetStatus($"{name} saved.");
    }

    private void RenamePreset(int slot)
    {
        var current = PresetName(slot);
        var renamed = Prompt("Rename preset", "Preset name", current);
        if (string.IsNullOrWhiteSpace(renamed)) return;
        while (_presets.Count <= slot)
        {
            _presets.Add(null);
        }
        _presets[slot] ??= CurrentPreset(renamed.Trim());
        _presets[slot]!.Name = renamed.Trim();
        SavePresetsFile();
        BuildPresetButtons();
        SetStatus("Preset renamed.");
    }

    private CourtPreset CurrentPreset(string name)
    {
        return new CourtPreset
        {
            Name = name,
            TemplatePath = _templatePath,
            Visibility = new Dictionary<string, bool>(_visibility),
            ColorOverrides = _colorOverrides.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
            NameOverrides = new Dictionary<string, string>(_nameOverrides),
            SelectedLayerId = _selectedLayer?.Id,
        };
    }

    private void SavePresetsFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_presetsPath)!);
        var payload = new PresetFile { Presets = _presets.Take(5).ToList() };
        File.WriteAllText(_presetsPath, JsonSerializer.Serialize(payload, _jsonOptions));
    }

    private async Task RefreshPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(_templatePath)) return;
        var version = Interlocked.Increment(ref _renderVersion);
        SetStatus("Refreshing preview...");
        try
        {
            var request = new
            {
                templatePath = _templatePath,
                visibility = _visibility,
                colorOverrides = _colorOverrides,
                customFloorImages = _customFloorImages.Select(image => new
                {
                    id = image.Id,
                    name = image.Name,
                    path = image.Path,
                    bbox = image.Bbox,
                    visible = _visibility.GetValueOrDefault(image.Id, false),
                }).ToList(),
            };
            using var response = await _backend.RenderAsync(request);
            if (version != _renderVersion) return;
            _previewPath = response.RootElement.GetProperty("previewPath").GetString() ?? _previewPath;
            LoadPreviewImage();
            SetStatus("Preview refreshed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Preview failed: {ex.Message}");
        }
    }

    private void LoadPreviewImage()
    {
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
        {
            PreviewImage.Source = null;
            PreviewEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(_previewPath);
        bitmap.EndInit();
        bitmap.Freeze();
        PreviewImage.Source = bitmap;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
    }

    private void OnPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
        {
            SetStatus("Preview is not ready yet.");
            return;
        }

        var image = new System.Windows.Controls.Image
        {
            Source = LoadBitmap(_previewPath),
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var window = new Window
        {
            Title = "Court Preview",
            Owner = this,
            Width = 1180,
            Height = 760,
            MinWidth = 800,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (WpfBrush)new BrushConverter().ConvertFromString("#20242B")!,
            Content = image,
        };
        window.Show();
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void BuildPalettePanel()
    {
        PaletteHost.Children.Clear();
        var query = PaletteSearchBox.Text.Trim();
        var shown = 0;
        foreach (var palette in _teamPalettes)
        {
            var colors = palette.Colors
                .Where(color => PaletteMatches(palette, color, query))
                .ToList();
            if (colors.Count == 0) continue;

            var expander = new Expander
            {
                Header = $"{palette.Team}  {palette.League}",
                IsExpanded = !string.IsNullOrWhiteSpace(query),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
            foreach (var color in colors)
            {
                wrap.Children.Add(PaletteButton(color));
            }
            expander.Content = wrap;
            PaletteHost.Children.Add(expander);
            shown++;
            if (shown >= 90) break;
        }
    }

    private WpfButton PaletteButton(TeamColor color)
    {
        var button = new WpfButton
        {
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(6, 4, 7, 4),
            Tag = color.Hex,
            ToolTip = $"{color.Name} {color.Hex}",
        };
        var stack = new StackPanel { Orientation = WpfOrientation.Horizontal };
        stack.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            Background = HexToBrush(color.Hex),
            BorderBrush = (WpfBrush)System.Windows.Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = color.Hex,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (WpfBrush)System.Windows.Application.Current.Resources["InkBrush"],
        });
        button.Content = stack;
        button.Click += async (_, _) => await ApplyHexAsync((string)button.Tag);
        return button;
    }

    private void OnPaletteSearchChanged(object sender, TextChangedEventArgs e) => BuildPalettePanel();

    private static bool PaletteMatches(TeamPalette palette, TeamColor color, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = $"{palette.Team} {palette.League} {color.Name} {color.Hex}".ToLowerInvariant();
        return haystack.Contains(query.ToLowerInvariant());
    }

    private void RemoveCustomFloorMetadata(string layerId)
    {
        var image = _customFloorImages.FirstOrDefault(item => item.Id == layerId);
        if (image is null) return;
        _customFloorImages.Remove(image);
        var metaPath = Path.Combine(_backend.ProjectRoot, "custom_floors", "custom_floors.json");
        var payload = new { floors = _customFloorImages.Where(item => !item.IsTemplate).Select(item => new { id = item.Id, name = item.Name, path = item.Path, bbox = item.Bbox }).ToList() };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(payload, _jsonOptions));
        var localPath = Path.IsPathRooted(image.Path) ? image.Path : Path.Combine(_backend.ProjectRoot, image.Path);
        try
        {
            if (File.Exists(localPath)) File.Delete(localPath);
        }
        catch
        {
            SetStatus("Removed the custom floor from the list; the image file could not be deleted.");
        }
    }

    private bool IsColorableLayer(CourtLayerNode layer)
    {
        if (layer.IsGroup) return false;
        if (NormalizeName(layer.Name) == "outside color") return true;
        return Ancestors(layer).Any(parent => NormalizeName(parent.Name) is "paint colors" or "lines");
    }

    private bool IsInsideCourtFloor(CourtLayerNode layer) => CourtFloorGroupFor(layer) is not null;

    private CourtLayerNode? CourtFloorGroupFor(CourtLayerNode layer)
    {
        if (IsCourtFloorGroup(layer)) return layer;
        return Ancestors(layer).FirstOrDefault(IsCourtFloorGroup);
    }

    private CourtLayerNode? FloorRootFor(CourtLayerNode layer, CourtLayerNode? floorGroup)
    {
        if (floorGroup is null || layer.Id == floorGroup.Id) return null;
        if (layer.IsCustomFloor || layer.IsTemplateFloor || IsFloorTemplateCategory(layer)) return layer;
        var root = layer;
        while (root.ParentId is not null && root.ParentId != floorGroup.Id)
        {
            var parent = ParentOf(root);
            if (parent is null) return null;
            root = parent;
        }
        return root.ParentId == floorGroup.Id ? root : null;
    }

    private bool IsCourtFloorGroup(CourtLayerNode layer)
        => NormalizeName(layer.Name) is "court floors" or "court floor" or "floor options" or "floors";

    private static bool IsFloorTemplateCategory(CourtLayerNode layer)
        => layer.Id.StartsWith("floor_template_category_", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<CourtLayerNode> Ancestors(CourtLayerNode layer)
    {
        var parent = ParentOf(layer);
        while (parent is not null)
        {
            yield return parent;
            parent = ParentOf(parent);
        }
    }

    private IEnumerable<CourtLayerNode> Descendants(CourtLayerNode layer)
    {
        foreach (var child in layer.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private CourtLayerNode? ParentOf(CourtLayerNode layer)
        => !string.IsNullOrWhiteSpace(layer.ParentId) && _layersById.TryGetValue(layer.ParentId, out var parent) ? parent : null;

    private string FriendlyLayerName(CourtLayerNode layer)
    {
        if (_nameOverrides.TryGetValue(layer.Id, out var renamed)) return renamed;
        return NormalizeName(layer.Name) switch
        {
            "3 point lines" => "NBA Three",
            "college three" => "College Three",
            "high school three" => "High School Three",
            _ => layer.Name,
        };
    }

    private void SetLayerHex(CourtLayerNode layer, string hex, bool rememberAsTemplate)
    {
        layer.ActiveHex = hex;
        if (rememberAsTemplate)
        {
            _templateColors[layer.Id] = hex;
        }
    }

    private void SelectTreeItem(CourtLayerNode layer)
    {
        var item = FindTreeViewItem(LayersTree, layer);
        if (item is null) return;
        item.IsSelected = true;
        item.BringIntoView();
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl container, object item)
    {
        if (container.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem found)
        {
            return found;
        }

        foreach (var child in container.Items)
        {
            if (container.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childContainer) continue;
            var result = FindTreeViewItem(childContainer, item);
            if (result is not null) return result;
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed) return typed;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private string PresetName(int slot)
        => slot >= 0 && slot < _presets.Count && !string.IsNullOrWhiteSpace(_presets[slot]?.Name)
            ? _presets[slot]!.Name
            : $"Preset {slot + 1}";

    private static string NormalizeName(string value)
        => string.Join(" ", value.ToLowerInvariant().Replace("_", " ").Replace("-", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? NormalizeHex(string value)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        }
        if (hex.Length != 6 || hex.Any(ch => !Uri.IsHexDigit(ch))) return null;
        return $"#{hex.ToUpperInvariant()}";
    }

    private static int[] HexToRgb(string hex)
    {
        var normalized = NormalizeHex(hex) ?? "#FFFFFF";
        return [Convert.ToInt32(normalized.Substring(1, 2), 16), Convert.ToInt32(normalized.Substring(3, 2), 16), Convert.ToInt32(normalized.Substring(5, 2), 16)];
    }

    private static string RgbToHex(IReadOnlyList<int> rgb)
        => $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";

    private static WpfBrush HexToBrush(string? hex)
    {
        var normalized = string.IsNullOrWhiteSpace(hex) ? null : NormalizeHex(hex);
        return normalized is null ? System.Windows.Media.Brushes.Transparent : (WpfBrush)new BrushConverter().ConvertFromString(normalized)!;
    }

    private static string? Prompt(string title, string label, string value)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) };
        var input = new WpfTextBox { Text = value };
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new WpfButton { Content = "Save", IsDefault = true };
        var cancel = new WpfButton { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(input, 1);
        Grid.SetRow(buttons, 2);
        root.Children.Add(text);
        root.Children.Add(input);
        root.Children.Add(buttons);
        dialog.Content = root;
        input.SelectAll();
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        ProjectStateText.Text = message;
    }

    private static void ShowError(string title, Exception ex)
        => System.Windows.MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed class PresetFile
    {
        [JsonPropertyName("presets")]
        public List<CourtPreset?> Presets { get; set; } = [];
    }
}
