using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NBA2KCourtCreator.Studio;

public sealed record PaletteColor(string Hex, string Name, Brush Brush, string Hint);
public sealed record PaletteTeam(string Key, string League, string Name, string Evidence, IReadOnlyList<PaletteColor> Colors);
public sealed record PaletteHeading(string Key, string Label, string Count, bool Category, bool Expanded)
{
    public double Angle => Expanded ? 90 : 0;
    public Thickness Indent => new(Category ? 0 : 16, 0, 0, 0);
    public FontWeight Weight => Category ? FontWeights.SemiBold : FontWeights.Normal;
    public string Hint => (Expanded ? "Collapse " : "Expand ") + Label;
}
public sealed record PaletteSwatches(IReadOnlyList<PaletteColor> Colors, string Evidence);

public partial class TeamColorWindow : Window
{
    private static int CategoryOrder(string category) => category switch { "NBA" => 0, "WNBA" => 1, "EuroLeague" => 2, "NCAA D1" or "D1" => 3, _ => 4 };
    private static string CategoryLabel(string category) => category == "NCAA D1" ? "D1" : category;
    private readonly IReadOnlyList<PaletteTeam> _teams;
    private readonly HashSet<string> _expanded = [];
    private readonly HashSet<string> _searchCollapsed = [];
    private string _lastSearch = "";
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _ready;
    private double? _wheelOffset;
    private bool _wheelResetScheduled;
    public string? SelectedHex { get; private set; }
    public IReadOnlyList<object> Rows { get; private set; } = [];
    public TeamColorWindow(Window? owner, JsonArray teams, bool testing = false)
    {
        InitializeComponent(); Style = (Style)FindResource(typeof(Window)); if (owner is not null) Owner = owner;
        _teams = teams.OfType<JsonObject>().Select((team, index) =>
        {
            var league = team["league"]?.GetValue<string>() ?? "College"; var name = team["team"]?.GetValue<string>() ?? "Team";
            var evidence = team["paletteNote"]?.GetValue<string>() ?? "Existing team palette";
            var source = team["source"]?.GetValue<string>() ?? "";
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) source = uri.Host;
            var colors = (team["colors"] as JsonArray ?? []).OfType<JsonObject>().Select(color => (Hex: StudioImages.Hex(color["hex"]?.GetValue<string>()), Name: color["name"]?.GetValue<string>() ?? "Color"))
                .Where(c => c.Hex is not null).DistinctBy(c => c.Hex, StringComparer.OrdinalIgnoreCase)
                .Select(c => { var brush = StudioImages.Brush(c.Hex!); if (brush.CanFreeze) brush.Freeze(); return new PaletteColor(c.Hex!, c.Name, brush, c.Name + " · " + c.Hex + "\n" + evidence + (source.Length > 0 ? "\n" + source : "")); }).ToArray();
            return new PaletteTeam("team:" + index, league, name, evidence, colors);
        }).Where(team => team.Colors.Count > 0).ToArray();
        LeagueFilter.Items.Add("All Categories"); foreach (var league in _teams.Select(team => team.League).Distinct().OrderBy(CategoryOrder).ThenBy(league => league)) LeagueFilter.Items.Add(CategoryLabel(league));
        LeagueFilter.SelectedIndex = 0; _ready = true; Rebuild();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Rebuild(); };
        Closed += (_, _) => _searchDelay.Stop(); if (!testing) StudioWindowBounds.Attach(this);
    }
    public void SetSearch(string query) { _searchDelay.Stop(); SearchInput.Text = query; _searchDelay.Stop(); Rebuild(); }
    public void ToggleGroup(string key) { var state = string.IsNullOrWhiteSpace(SearchInput.Text) ? _expanded : _searchCollapsed; if (!state.Add(key)) state.Remove(key); Rebuild(resetScroll: false); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!_ready) return; _searchDelay.Stop(); _searchDelay.Start(); }
    private void LeagueChanged(object sender, SelectionChangedEventArgs e) { if (_ready) Rebuild(); }
    private void GroupClick(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string key }) ToggleGroup(key); }
    private void ColorClick(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string hex }) { SelectedHex = hex; DialogResult = true; } }
    private void Rebuild(bool resetScroll = true)
    {
        var words = SearchInput.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_lastSearch != SearchInput.Text) { _lastSearch = SearchInput.Text; _searchCollapsed.Clear(); }
        var filter = LeagueFilter.SelectedIndex > 0 ? LeagueFilter.SelectedItem as string : null;
        var rows = new List<object>(); int matches = 0;
        foreach (var group in _teams.Where(team => filter is null || CategoryLabel(team.League) == filter).GroupBy(team => team.League).OrderBy(group => CategoryOrder(group.Key)).ThenBy(group => group.Key))
        {
            var matching = group.Select(team => (Team: team, Colors: team.Colors.Where(color => words.All(word => (team.Name + " " + team.League + " " + CategoryLabel(team.League) + " " + color.Name + " " + color.Hex).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray())).Where(item => item.Colors.Length > 0).ToArray();
            if (matching.Length == 0) continue;
            matches += matching.Length;
            var key = "league:" + group.Key; var expanded = words.Length > 0 ? !_searchCollapsed.Contains(key) : _expanded.Contains(key);
            rows.Add(new PaletteHeading(key, CategoryLabel(group.Key), matching.Length + " teams", true, expanded));
            if (!expanded) continue;
            foreach (var (team, colors) in matching.OrderBy(item => item.Team.Name))
            {
                var teamExpanded = words.Length > 0 ? !_searchCollapsed.Contains(team.Key) : _expanded.Contains(team.Key);
                rows.Add(new PaletteHeading(team.Key, team.Name, colors.Length + " colors", false, teamExpanded));
                if (teamExpanded) rows.Add(new PaletteSwatches(colors, team.Evidence));
            }
        }
        var scroll = FindScroll(PaletteList); var offset = scroll?.VerticalOffset ?? 0;
        Rows = rows; PaletteList.ItemsSource = rows; _wheelOffset = null;
        if (scroll is not null) scroll.ScrollToVerticalOffset(resetScroll ? 0 : offset);
        ResultHint.Text = rows.Count == 0 ? "No matching team colors." : words.Length > 0 ? matches + " matching teams. Clear search to restore your expanded categories." : "Expand a category, then a team. Click a swatch to apply it.";
    }
    public ScrollViewer? ScrollHost => FindScroll(PaletteList);
    public void ScrollByWheel(int delta)
    {
        var scroll = ScrollHost; if (scroll is null || delta == 0) return;
        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? scroll.ViewportHeight : Math.Max(1, lines) * 16.0;
        var start = _wheelOffset ?? scroll.VerticalOffset;
        var target = Math.Clamp(start - delta / 120.0 * step, 0, scroll.ScrollableHeight);
        _wheelOffset = target; scroll.ScrollToVerticalOffset(target);
        if (!_wheelResetScheduled) { _wheelResetScheduled = true; Dispatcher.InvokeAsync(() => { _wheelOffset = null; _wheelResetScheduled = false; }, DispatcherPriority.ContextIdle); }
    }
    private void PaletteWheel(object sender, MouseWheelEventArgs e) { if (Keyboard.Modifiers != ModifierKeys.None || e.Delta == 0) return; var scroll = ScrollHost; if (scroll is null || scroll.ScrollableHeight <= 0) return; ScrollByWheel(e.Delta); e.Handled = true; }
    private static ScrollViewer? FindScroll(DependencyObject parent) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } found) return found; } return null; }
}
