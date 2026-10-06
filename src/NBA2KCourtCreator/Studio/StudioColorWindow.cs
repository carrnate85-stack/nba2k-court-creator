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
        picker.WindowStartupLocation = WindowStartupLocation.Manual;
        picker.Left = owner.Left + 190; picker.Top = owner.Top + Math.Min(300, owner.ActualHeight / 3);
        StudioWindowBounds.Attach(picker);
        ((TextBlock)picker.FindName("RoleDescription")).Visibility = Visibility.Collapsed;
        var heading = (TextBlock)picker.FindName("DialogHeading"); heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.ToolTip = roleName;
        var input = (TextBox)picker.FindName("HexInput");
        var apply = (Button)picker.FindName("AcceptButton"); apply.Content = "Apply"; apply.IsDefault = true;
        var palette = new Button { Content = "Team Colors", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        ((StackPanel)apply.Parent).Children.Insert(0, palette);
        chooseTeamColor ??= parent =>
        {
            var paletteWindow = new TeamColorWindow(parent, teams);
            return paletteWindow.ShowDialog() == true ? paletteWindow.SelectedHex : null;
        };
        palette.Click += (_, _) =>
        {
            if (closed || teamPickerOpen) return;
            teamPickerOpen = true; palette.IsEnabled = false;
            try
            {
                var selected = chooseTeamColor(picker);
                if (!closed && StudioImages.Hex(selected) is { } color) input.Text = color;
            }
            finally { teamPickerOpen = false; if (!closed) palette.IsEnabled = true; }
        };
        picker.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        picker.Closed += (_, _) => closed = true;
        return picker;
    }
}
