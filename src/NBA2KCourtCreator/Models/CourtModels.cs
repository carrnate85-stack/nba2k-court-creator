using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace NBA2KCourtCreator.Models;

public sealed class CourtLayerNode : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private bool _visible;
    private string _activeHex = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public int PsdIndex { get; init; }
    public int Depth { get; init; }
    public bool OriginalVisible { get; init; }
    public int Opacity { get; init; }
    public string BlendMode { get; init; } = string.Empty;
    public int[] Bbox { get; init; } = [0, 0, 0, 0];
    public bool IsCustomFloor { get; init; }
    public bool IsTemplateFloor { get; set; }
    public bool IsSectionGroup { get; init; }
    public ObservableCollection<CourtLayerNode> Children { get; } = [];

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisibilityText));
        }
    }

    public string ActiveHex
    {
        get => _activeHex;
        set
        {
            if (_activeHex == value) return;
            _activeHex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SwatchBrush));
        }
    }

    public bool IsGroup => Kind.Equals("group", StringComparison.OrdinalIgnoreCase);
    public string VisibilityText => Visible ? "On" : "Off";
    public string TypeLabel => IsTemplateFloor ? "Template" : IsCustomFloor ? "Floor" : IsGroup ? "Group" : "Layer";
    public string OpacityLabel => IsCustomFloor || IsGroup ? string.Empty : $"{Math.Round(Opacity / 255.0 * 100)}%";
    public System.Windows.Media.Brush SwatchBrush => string.IsNullOrWhiteSpace(ActiveHex)
        ? System.Windows.Media.Brushes.Transparent
        : (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(ActiveHex)!;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CustomFloorImage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("bbox")]
    public int[] Bbox { get; set; } = [0, 0, 0, 0];

    [JsonPropertyName("visible")]
    public bool Visible { get; set; }

    [JsonPropertyName("isTemplate")]
    public bool IsTemplate { get; set; }
}

public sealed class TeamPalette
{
    public string League { get; init; } = string.Empty;
    public string Team { get; init; } = string.Empty;
    public List<TeamColor> Colors { get; init; } = [];
}

public sealed class TeamColor
{
    public string Name { get; init; } = string.Empty;
    public string Hex { get; init; } = string.Empty;
}

public sealed class CourtPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("template_path")]
    public string TemplatePath { get; set; } = string.Empty;

    [JsonPropertyName("visibility")]
    public Dictionary<string, bool> Visibility { get; set; } = [];

    [JsonPropertyName("color_overrides")]
    public Dictionary<string, int[]> ColorOverrides { get; set; } = [];

    [JsonPropertyName("name_overrides")]
    public Dictionary<string, string> NameOverrides { get; set; } = [];

    [JsonPropertyName("selected_layer_id")]
    public string? SelectedLayerId { get; set; }
}
