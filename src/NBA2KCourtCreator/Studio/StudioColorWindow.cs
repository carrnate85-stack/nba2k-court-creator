using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

public sealed class StudioColorWindow : Window
{
    public string? SelectedHex { get; private set; }
    public StudioColorWindow(Window owner, string hex, JsonArray teams) : this(owner, hex, teams, false) { }
    internal StudioColorWindow(Window owner, string hex, JsonArray teams, bool testing)
    {
        if (!testing) Owner = owner;
        Title = "Color"; Width = 410; Height = 330; ResizeMode = ResizeMode.NoResize;
        Left = owner.Left + 190; Top = owner.Top + Math.Min(300, owner.ActualHeight / 3);
        Resources.MergedDictionaries.Add(FloorCatalogWindow.Styles()); Style = (Style)FindResource(typeof(Window));
        StudioWindowBounds.Attach(this);
        var layout = new Grid { Margin = new Thickness(16) }; Content = layout;
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stack = new StackPanel();
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll);
        stack.Children.Add(new TextBlock { Text = "Hex color", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = hex }; stack.Children.Add(input);
        var swatch = new Border { Height = 42, Background = StudioImages.Brush(hex), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 10, 0, 10) }; stack.Children.Add(swatch);
        var rgb = (Color)ColorConverter.ConvertFromString(hex);
        var sliders = new Slider[3]; var syncing = false;
        for (var index = 0; index < 3; index++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) }; var label = new TextBlock { Text = new[] { "R", "G", "B" }[index], Width = 25 };
            row.Children.Add(label); sliders[index] = new Slider { Minimum = 0, Maximum = 255, Value = new[] { rgb.R, rgb.G, rgb.B }[index], IsSnapToTickEnabled = true, TickFrequency = 1 };
            row.Children.Add(sliders[index]); stack.Children.Add(row);
        }
        input.TextChanged += (_, _) => { var value = StudioImages.Hex(input.Text); if (value is null) return; swatch.Background = StudioImages.Brush(value); var color = (Color)ColorConverter.ConvertFromString(value); syncing = true; sliders[0].Value = color.R; sliders[1].Value = color.G; sliders[2].Value = color.B; syncing = false; };
        foreach (var slider in sliders) slider.ValueChanged += (_, _) => { if (syncing) return; input.Text = $"#{(int)sliders[0].Value:X2}{(int)sliders[1].Value:X2}{(int)sliders[2].Value:X2}"; };
        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(actions, 1); layout.Children.Add(actions);
        var palette = new Button { Content = "Team Colors" }; palette.Click += (_, _) => { var picker = new TeamColorWindow(this, teams); if (picker.ShowDialog() == true && picker.SelectedHex is not null) input.Text = picker.SelectedHex; }; actions.Children.Add(palette);
        var apply = new Button { Content = "Apply", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right }; apply.Click += (_, _) => { SelectedHex = StudioImages.Hex(input.Text); if (SelectedHex is not null) DialogResult = true; }; actions.Children.Add(apply);
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
