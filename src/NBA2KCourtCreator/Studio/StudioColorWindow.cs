using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TextureStudio;
using TextureStudio.Services;

namespace NBA2KCourtCreator.Studio;

public static class StudioColorWindow
{
    public static ColorPickerDialog Create(Window owner, string hex, JsonArray teams, string roleName = "Court")
        => Create(owner, hex, teams, false, roleName);
    internal static ColorPickerDialog Create(Window owner, string hex, JsonArray teams, bool testing,
        string roleName = "Court", Func<Window, string?>? chooseTeamColor = null)
    {
        var picker = new ColorPickerDialog(RasterPaintService.ParseHexColor(StudioImages.Hex(hex)
            ?? throw new ArgumentException("Enter a valid court color.", nameof(hex))), roleName, allowAlpha: false);
        var closed = false; var teamPickerOpen = false;
        if (!testing) picker.Owner = owner;
        TextureStudio.StudioTheme.Apply(picker.Resources, TwoK.Studio.StudioTheme.IsDark);
        picker.Style = (Style)picker.FindResource(typeof(Window));
        picker.Tag = nameof(StudioColorWindow);
        if (owner is StudioWindow studio) picker.PaintColorPalette = studio.PickerColors;
        picker.WindowStartupLocation = WindowStartupLocation.Manual;
        picker.Left = owner.Left + 190; picker.Top = owner.Top + Math.Min(300, owner.ActualHeight / 3);
        StudioWindowBounds.Attach(picker);
        ((TextBlock)picker.FindName("RoleDescription")).Visibility = Visibility.Collapsed;
        var heading = (TextBlock)picker.FindName("DialogHeading"); heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.ToolTip = roleName;
        var input = (TextBox)picker.FindName("HexInput");
        var apply = (Button)picker.FindName("AcceptButton"); apply.Content = "Apply"; apply.IsDefault = true;
        var palette = (Button)picker.FindName("TeamColorsButton");
        chooseTeamColor ??= parent =>
        {
            var paletteWindow = new TeamColorWindow(parent, teams);
            return paletteWindow.ShowDialog() == true ? paletteWindow.SelectedHex : null;
        };
        picker.TeamColorChooser = _ =>
        {
            if (closed || teamPickerOpen) return null;
            teamPickerOpen = true; palette.IsEnabled = false;
            try
            {
                var selected = chooseTeamColor(picker);
                if (!closed && StudioImages.Hex(selected) is { } color) return RasterPaintService.ParseHexColor(color);
                return null;
            }
            finally { teamPickerOpen = false; if (!closed) palette.IsEnabled = true; }
        };
        picker.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        picker.Closed += (_, _) => closed = true;
        return picker;
    }
}
