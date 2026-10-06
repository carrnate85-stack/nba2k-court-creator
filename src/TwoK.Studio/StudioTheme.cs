using System.Windows;
using System.Windows.Media;

namespace TwoK.Studio;

/// <summary>Shared semantic colors for editor chrome and dialogs, never texture pixels.</summary>
public static class StudioTheme
{
    public static bool IsDark { get; private set; }
    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("TopChromeBrush", "#F8F9FB", "#141B24"),
        ("DocumentChromeBrush", "#EFF1F4", "#1B2531"),
        ("ChromeDividerBrush", "#D9DDE3", "#344152"),
        ("WindowBrush", "#ECEDEF", "#0B1119"),
        ("PanelBrush", "#F7F7F9", "#101923"),
        ("PanelRaisedBrush", "#FDFDFE", "#172230"),
        ("PanelHoverBrush", "#E7E9EE", "#1D2B3B"),
        ("InputBrush", "#F1F2F5", "#121C28"),
        ("WorkspaceBrush", "#E1E3E7", "#0D141D"),
        ("BorderBrush", "#DCDEE4", "#2A3A4C"),
        ("StrongBorderBrush", "#BFC3CD", "#3B526A"),
        ("TextBrush", "#272830", "#F1F6FC"),
        ("MutedTextBrush", "#686B76", "#9BAEC4"),
        ("AccentBrush", "#6E86AC", "#2593FF"),
        ("AccentBrightBrush", "#4D72AA", "#40B5FF"),
        ("AccentDarkBrush", "#DDE5F1", "#103F5E"),
        ("TealBrush", "#577B88", "#0B7482"),
        ("ActionBrush", "#30313A", "#075A67"),
        ("ActionTextBrush", "#FFFFFF", "#FFFFFF"),
        ("MenuPopupBrush", "#FCFCFD", "#111C28"),
        ("MenuHoverBrush", "#E9ECF3", "#203247"),
        ("MenuOpenBrush", "#E2E6EE", "#18344E"),
        ("MenuSeparatorBrush", "#E2E4E9", "#2A3C50"),
        ("ScrollTrackBrush", "#E9EBEF", "#0C151F"),
        ("ScrollThumbBrush", "#BCC1CC", "#405369"),
        ("SliderRailBrush", "#D5D9E1", "#34475B"),
        ("SliderThumbBrush", "#FFFFFF", "#E8F2FF"),
        ("SafetyBrush", "#ECF4F0", "#153126"),
        ("SafetyBorderBrush", "#C3DACB", "#2A7255"),
        ("SafetyTextBrush", "#3A6550", "#A9E7C9"),
        ("WarningTextBrush", "#8A5A22", "#FFBE78")
    };

    public static void Apply(bool dark)
    {
        var app = Application.Current ?? throw new InvalidOperationException("An application is required to apply a theme.");
        app.Dispatcher.VerifyAccess();
        foreach (var color in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? color.Dark : color.Light));
            brush.Freeze();
            app.Resources[color.Key] = brush;
        }
        var baseBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#151F2B" : "#FAFAFB"));
        var tileBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#1B2734" : "#E4E6EB"));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(tileBrush, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        drawing.Children.Add(new GeometryDrawing(tileBrush, null, new RectangleGeometry(new Rect(12, 12, 12, 12))));
        var checker = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 24, 24), ViewportUnits = BrushMappingMode.Absolute };
        checker.Freeze();
        app.Resources["CheckerBrush"] = checker;
        IsDark = dark;
    }
}
