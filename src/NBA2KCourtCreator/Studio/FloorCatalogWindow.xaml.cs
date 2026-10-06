using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NBA2KCourtCreator.Studio;

internal sealed class FloorCatalogRow(StockFloor floor, HashSet<string> favorites) : INotifyPropertyChanged
{
    public StockFloor Floor { get; } = floor;
    public string FavoriteGlyph => favorites.Contains(Floor.Id) ? "\uE735" : "\uE734";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void FavoriteChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
}

public sealed partial class FloorCatalogWindow : Window
{
    private readonly FloorCatalogRow[] _allRows;
    private readonly HashSet<string> _favorites;
    private readonly IReadOnlyList<string> _recent;
    private readonly Func<StockFloor, CancellationToken, Task<BitmapSource>> _loadThumbnail;
    private readonly SemaphoreSlim _decoders = new(2);
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<Image, ThumbnailRequest> _requests = [];
    private readonly HashSet<Task> _pending = [];
    private string? _rememberedSelection;
    private bool _ready, _closed, _thumbnailLayoutQueued;
    private sealed record ThumbnailRequest(FloorCatalogRow Row, CancellationTokenSource Cancellation);
    public StockFloor? SelectedFloor { get; private set; }
    public bool AddRequested { get; private set; }
    internal IReadOnlyList<FloorCatalogRow> Rows => FloorList.Items.Cast<FloorCatalogRow>().ToArray();
    internal Task WaitForThumbnailsAsync() => Task.WhenAll(_pending.ToArray());

    public FloorCatalogWindow(Window? owner, IReadOnlyList<StockFloor> floors, StockFloor? selected,
        HashSet<string> favorites, IReadOnlyList<string> recent)
        : this(owner, floors, selected, favorites, recent, LoadThumbnailAsync, false) { }

    internal FloorCatalogWindow(Window? owner, IReadOnlyList<StockFloor> floors, StockFloor? selected,
        HashSet<string> favorites, IReadOnlyList<string> recent,
        Func<StockFloor, CancellationToken, Task<BitmapSource>> loadThumbnail, bool testing)
    {
        _favorites = favorites; _recent = recent; _loadThumbnail = loadThumbnail;
        _allRows = floors.Select(floor => new FloorCatalogRow(floor, favorites)).ToArray();
        _rememberedSelection = selected?.Id;
        InitializeComponent(); Style = (Style)FindResource(typeof(Window)); if (owner is not null) Owner = owner;
        foreach (var category in new[] { "All", "Favorites", "Recent" }.Concat(floors.Select(floor => floor.Category).Distinct().OrderBy(category => category == "Unknown" ? "zzzz" : category))) CategoryInput.Items.Add(category);
        CategoryInput.SelectedItem = "All";
        SortInput.Items.Add("A - Z"); SortInput.Items.Add("Z - A"); SortInput.SelectedIndex = 0;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Rebuild(); };
        _ready = true; Rebuild();
        Loaded += (_, _) => { if (!_closed && FloorList.SelectedItem is FloorCatalogRow row) FloorList.ScrollIntoView(row); };
        FloorList.LayoutUpdated += FloorLayoutUpdated;
        Closed += (_, _) => { _closed = true; _searchDelay.Stop(); FloorList.LayoutUpdated -= FloorLayoutUpdated; foreach (var image in _requests.Keys.ToArray()) CancelThumbnail(image); };
        if (!testing) StudioWindowBounds.Attach(this);
    }

    private static Task<BitmapSource> LoadThumbnailAsync(StockFloor floor, CancellationToken cancellation)
        => Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return StudioImages.Load(floor.PreviewPath, 208); }, cancellation);

    internal void SetSearch(string query) { _searchDelay.Stop(); SearchInput.Text = query; _searchDelay.Stop(); Rebuild(); }
    internal void SetCategory(string category) { CategoryInput.SelectedItem = category; }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!_ready || _closed) return; _searchDelay.Stop(); _searchDelay.Start(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_closed) { _searchDelay.Stop(); Rebuild(); } }

    private void Rebuild()
    {
        if (_closed) return;
        foreach (var image in _requests.Keys.ToArray()) CancelThumbnail(image);
        var words = SearchInput.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var category = CategoryInput.SelectedItem as string ?? "All";
        var rows = _allRows.Where(row => words.All(word => (row.Floor.Name + " " + row.Floor.Category + " " + row.Floor.Id).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Where(row => category == "All" || category == row.Floor.Category || category == "Favorites" && _favorites.Contains(row.Floor.Id) || category == "Recent" && _recent.Contains(row.Floor.Id));
        rows = SortInput.SelectedIndex == 1 ? rows.OrderByDescending(row => row.Floor.Name) : rows.OrderBy(row => row.Floor.Name);
        var visible = rows.ToArray(); FloorList.ItemsSource = visible;
        FloorList.SelectedItem = visible.FirstOrDefault(row => row.Floor.Id == _rememberedSelection);
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = visible.Length + (visible.Length == 1 ? " court" : " courts");
        UseSelectedButton.IsEnabled = FloorList.SelectedItem is FloorCatalogRow;
    }

    private void FloorSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (FloorList.SelectedItem is FloorCatalogRow row) _rememberedSelection = row.Floor.Id; UseSelectedButton.IsEnabled = FloorList.SelectedItem is FloorCatalogRow; }
    private void AddClick(object sender, RoutedEventArgs e) { AddRequested = true; DialogResult = false; }
    private void UseSelectedClick(object sender, RoutedEventArgs e) => Accept(FloorList.SelectedItem as FloorCatalogRow);
    private void Accept(FloorCatalogRow? row) { if (row is null) return; SelectedFloor = row.Floor; DialogResult = true; }
    private void FloorKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && e.OriginalSource is not Button) { e.Handled = true; Accept(FloorList.SelectedItem as FloorCatalogRow); } }
    private void FloorPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && IsButton(target)) return;
        if (sender is FrameworkElement { DataContext: FloorCatalogRow row }) { e.Handled = true; Accept(row); }
    }
    private void FavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FloorCatalogRow row }) return;
        if (!_favorites.Add(row.Floor.Id)) _favorites.Remove(row.Floor.Id);
        row.FavoriteChanged(); e.Handled = true;
        if (Equals(CategoryInput.SelectedItem, "Favorites")) Rebuild();
    }
    private static bool IsButton(DependencyObject target)
    {
        for (DependencyObject? parent = target; parent is not null;)
        {
            if (parent is Button) return true;
            parent = parent is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(parent)
                : parent is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(parent);
        }
        return false;
    }

    private void ThumbnailLoaded(object sender, RoutedEventArgs e) => StartThumbnail((Image)sender);
    private void ThumbnailUnloaded(object sender, RoutedEventArgs e) => CancelThumbnail((Image)sender);
    private void ThumbnailContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    { var image = (Image)sender; CancelThumbnail(image); if (image.IsLoaded) StartThumbnail(image); }
    private void FloorLayoutUpdated(object? sender, EventArgs e)
    {
        if (_closed || _thumbnailLayoutQueued) return;
        _thumbnailLayoutQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _thumbnailLayoutQueued = false;
            if (_closed) return;
            foreach (var image in ThumbnailImages(FloorList)) StartThumbnail(image);
        }, DispatcherPriority.Background);
    }
    private static IEnumerable<Image> ThumbnailImages(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Image image) yield return image;
            else foreach (var nested in ThumbnailImages(child)) yield return nested;
        }
    }
    private void StartThumbnail(Image image)
    {
        if (_closed || image.DataContext is not FloorCatalogRow row) return;
        if (ReferenceEquals(image.Tag, row)) return;
        if (_requests.TryGetValue(image, out var existing) && ReferenceEquals(existing.Row, row)) return;
        CancelThumbnail(image); image.ToolTip = null;
        var request = new ThumbnailRequest(row, new CancellationTokenSource()); _requests[image] = request;
        var work = PopulateThumbnailAsync(image, request); _pending.Add(work); _ = RemoveCompletedAsync(work);
    }
    private async Task RemoveCompletedAsync(Task work) { try { await work; } finally { _pending.Remove(work); } }
    private void CancelThumbnail(Image image)
    {
        if (_requests.Remove(image, out var request)) request.Cancellation.Cancel();
        image.Source = null; image.Tag = null;
    }
    private async Task PopulateThumbnailAsync(Image image, ThumbnailRequest request)
    {
        var token = request.Cancellation.Token; var entered = false;
        bool Current() => !_closed && !token.IsCancellationRequested && ReferenceEquals(image.DataContext, request.Row)
            && _requests.TryGetValue(image, out var current) && ReferenceEquals(current, request);
        try
        {
            await _decoders.WaitAsync(token); entered = true; token.ThrowIfCancellationRequested();
            var bitmap = await _loadThumbnail(request.Row.Floor, token);
            token.ThrowIfCancellationRequested();
            if (Current()) image.Source = bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current()) image.ToolTip = "Preview unavailable: " + error.Message;
            System.Diagnostics.Trace.TraceWarning("Court catalog preview failed: {0}: {1}", request.Row.Floor.PreviewPath, error.Message);
        }
        finally
        {
            if (entered) _decoders.Release();
            if (Current()) image.Tag = request.Row;
            if (_requests.TryGetValue(image, out var current) && ReferenceEquals(current, request)) _requests.Remove(image);
            request.Cancellation.Dispose();
        }
    }

    internal static ResourceDictionary Styles() => new() { Source = new Uri("/NBA2KCourtCreator;component/Studio/StudioStyles.xaml", UriKind.Relative) };
}
