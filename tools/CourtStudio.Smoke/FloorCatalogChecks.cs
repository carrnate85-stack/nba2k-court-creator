using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckFloorCatalog(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "catalog-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "thumbnail.png"); WriteLogoExample(source);
        var broken = Path.Combine(root, "broken-thumbnail.png"); File.WriteAllText(broken, "Not image bytes.");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source)); var sourceTime = File.GetLastWriteTimeUtc(source);
        var floors = Enumerable.Range(0, 5000).Select(index => new StockFloor("catalog-" + index, "Team " + index.ToString("D5"),
            index % 3 == 0 ? "NBA" : index % 3 == 1 ? "Historic" : "Unknown", source, index == 0 ? broken : source, new JsonObject())).ToArray();
        var favorites = new HashSet<string> { floors[1].Id }; var recent = new[] { floors[^1].Id, floors[2].Id };
        var uiThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var hold = true; var active = 0; var maximumActive = 0; var calls = 0;
        var tokens = new ConcurrentBag<CancellationToken>();
        Task<BitmapSource> Decode(StockFloor floor, CancellationToken cancellation) => Task.Run(() =>
        {
            Assert(Environment.CurrentManagedThreadId != uiThread, "Catalog thumbnail read/decode ran on the UI thread.");
            cancellation.ThrowIfCancellationRequested(); tokens.Add(cancellation); Interlocked.Increment(ref calls);
            var simultaneous = Interlocked.Increment(ref active);
            int old; do { old = Volatile.Read(ref maximumActive); if (simultaneous <= old) break; } while (Interlocked.CompareExchange(ref maximumActive, simultaneous, old) != old);
            if (simultaneous == 2) started.TrySetResult();
            try
            {
                if (Volatile.Read(ref hold)) Assert(release.Wait(TimeSpan.FromSeconds(20)), "Held thumbnail decode timed out.");
                return StudioImages.Load(floor.PreviewPath, 208);
            }
            finally { Interlocked.Decrement(ref active); }
        }, cancellation);
        var watch = Stopwatch.StartNew();
        var catalog = new FloorCatalogWindow(null, floors, floors[0], favorites, recent, Decode, true);
        var constructorMs = watch.Elapsed.TotalMilliseconds;
        var list = (ListBox)catalog.FindName("FloorList"); var content = (FrameworkElement)catalog.Content;
        void LayoutCatalog(int width, int height)
        { content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); }
        void RenderCatalog(string name, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(catalog.Background, null, new Rect(0, 0, width, height));
            bitmap.Render(background); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(root, name)); encoder.Save(stream);
        }
        async Task Drain()
        {
            await catalog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await catalog.WaitForThumbnailsAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await catalog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await catalog.WaitForThumbnailsAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        try
        {
            Assert(calls == 0 && catalog.Rows.Count == 5000 && !catalog.IsVisible, "Constructing the catalog eagerly loaded artwork or showed a window.");
            LayoutCatalog(860, 690); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(calls == 2 && maximumActive == 2 && catalog.Rows.Count == 5000 && Descendants<ListBoxItem>(content).Count() < 60,
                "Catalog bypassed decode concurrency or realized the whole library.");
            Assert(await catalog.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Held thumbnail decoding blocked the dispatcher.");
            catalog.SetSearch("Team 04999"); LayoutCatalog(860, 690);
            Assert(catalog.Rows.Count == 1 && catalog.Rows[0].Floor.Id == floors[^1].Id, "Search missed a far, unrealized court.");
            Volatile.Write(ref hold, false); release.Set(); await Drain();
            var searchedImage = Descendants<Image>(list).Single();
            Assert(searchedImage.Source is BitmapSource { IsFrozen: true } && searchedImage.DataContext is FloorCatalogRow selected && selected.Floor.Id == floors[^1].Id,
                "Late old-row completion replaced the searched thumbnail or no new preview loaded.");
            catalog.SetSearch(""); LayoutCatalog(860, 690); await Drain();
            var realized = Descendants<ListBoxItem>(content).Count(); var initialCalls = calls;
            Assert(realized < 60 && initialCalls < 80 && maximumActive == 2, "Visible preview loading scaled with all 5000 courts.");
            RenderCatalog("catalog-desktop.png", 860, 690);
            var badImage = Descendants<Image>(list).Single(image => image.DataContext is FloorCatalogRow row && row.Floor.Id == floors[0].Id);
            Assert(badImage.Source is null && badImage.ToolTip is string message && message.StartsWith("Preview unavailable:"), "Bad preview aborted the catalog or lacked a fallback.");
            var failedCount = calls; LayoutCatalog(860, 690); await Drain();
            Assert(calls == failedCount, "Layout retried a failed preview indefinitely.");
            var chosenItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(catalog.Rows[1]);
            var star = Descendants<Button>(chosenItem).Single(); var rowsBefore = list.ItemsSource;
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
            Assert(!favorites.Contains(floors[1].Id) && ReferenceEquals(list.ItemsSource, rowsBefore) && catalog.SelectedFloor is null && calls == failedCount,
                "Favorite click accepted a court, rebuilt all rows or reread previews.");
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            catalog.SetCategory("Favorites"); LayoutCatalog(860, 690); await Drain();
            Assert(catalog.Rows.Count == 1 && catalog.Rows[0].Floor.Id == floors[1].Id, "Favorites filter does not reflect edits.");
            catalog.SetCategory("Recent"); LayoutCatalog(860, 690); await Drain();
            Assert(catalog.Rows.Select(row => row.Floor.Id).Order().SequenceEqual(recent.Order()), "Recent filter omitted courts.");
            catalog.SetCategory("All"); catalog.SetSearch("catalog-4999"); LayoutCatalog(860, 690); await Drain();
            Assert(catalog.Rows.Count == 1 && catalog.Rows[0].Floor.Id == floors[^1].Id, "Catalog IDs are not searchable.");
            catalog.SetSearch("unmatched query"); LayoutCatalog(600, 460); await Drain();
            Assert(catalog.Rows.Count == 0 && ((TextBlock)catalog.FindName("EmptyText")).Visibility == Visibility.Visible && !((Button)catalog.FindName("UseSelectedButton")).IsEnabled,
                "Empty results left a stale selectable floor.");
            catalog.SetSearch(""); LayoutCatalog(600, 460); await Drain();
            Assert((list.SelectedItem as FloorCatalogRow)?.Floor.Id == floors[0].Id, "Clearing search lost the selected court.");
            var footer = (Button)catalog.FindName("UseSelectedButton"); var corner = footer.TransformToAncestor(content).Transform(new Point(footer.ActualWidth, footer.ActualHeight));
            Assert(corner.Y <= content.ActualHeight + 1 && corner.X <= content.ActualWidth + 1, "Compact catalog clips its selection action.");
            RenderCatalog("catalog-compact.png", 600, 460);
            var scroll = Descendants<ScrollViewer>(list).Single(); scroll.ScrollToBottom(); LayoutCatalog(600, 460); await Drain();
            Assert(scroll.VerticalOffset > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1 && Descendants<ListBoxItem>(content).Count() < 60 && calls < 150,
                "Scrolling decoded the full library or did not reach its final courts.");
            var search = (TextBox)catalog.FindName("SearchInput");
            for (var index = 0; index < 100; index++) search.Text = "Team " + index.ToString("D5");
            search.Text = "Team 04999"; await Task.Delay(220); LayoutCatalog(600, 460); await Drain();
            Assert(catalog.Rows.Count == 1 && catalog.Rows[0].Floor.Id == floors[^1].Id, "Debounced typing did not apply the latest query.");
            Assert(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash) && File.GetLastWriteTimeUtc(source) == sourceTime, "Catalog changed source artwork.");
            File.WriteAllText(Path.Combine(root, "catalog-metrics.json"), new JsonObject
            { ["floors"] = 5000, ["constructorMs"] = constructorMs, ["initialRealizedRows"] = realized, ["initialDecodeCalls"] = initialCalls,
              ["totalDecodeCalls"] = calls, ["maximumConcurrentDecoders"] = maximumActive, ["nativeWindowsOpened"] = false }.ToJsonString());
        }
        finally { release.Set(); Volatile.Write(ref hold, false); catalog.Close(); await catalog.WaitForThumbnailsAsync().WaitAsync(TimeSpan.FromSeconds(20)); }

        File.Copy(source, broken, overwrite: true);
        var repaired = new FloorCatalogWindow(null, [floors[0]], floors[0], favorites, [], Decode, true);
        try
        {
            var repairedContent = (FrameworkElement)repaired.Content; repairedContent.Measure(new Size(600, 460)); repairedContent.Arrange(new Rect(0, 0, 600, 460)); repairedContent.UpdateLayout();
            await repaired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); await repaired.WaitForThumbnailsAsync();
            Assert(Descendants<Image>(repairedContent).Single().Source is BitmapSource, "Reopening the catalog did not recover a repaired preview.");
        }
        finally { repaired.Close(); await repaired.WaitForThumbnailsAsync(); }

        var lateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken lateToken = default;
        Task<BitmapSource> Late(StockFloor floor, CancellationToken token) { lateToken = token; lateStarted.TrySetResult(); return late.Task; }
        var closing = new FloorCatalogWindow(null, [floors[1]], floors[1], favorites, [], Late, true);
        var closingContent = (FrameworkElement)closing.Content;
        closingContent.Measure(new Size(600, 460)); closingContent.Arrange(new Rect(0, 0, 600, 460)); closingContent.UpdateLayout();
        try
        {
            await lateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); var image = Descendants<Image>(closingContent).Single();
            closing.Close(); Assert(lateToken.IsCancellationRequested, "Closing did not cancel outstanding thumbnail work.");
            late.SetResult(StudioImages.Load(source, 208)); await closing.WaitForThumbnailsAsync();
            Assert(image.Source is null && !closing.IsVisible, "Closed catalog accepted a late preview.");
        }
        finally { late.TrySetCanceled(); closing.Close(); await closing.WaitForThumbnailsAsync(); }
        using (var engine = new PythonServiceClient(ProjectRoot(), null))
        {
            var stock = await engine.RequestAsync(["load-stock"], TimeSpan.FromMinutes(4));
            var realFloors = StockFloor.ReadMany(stock["customFloorImages"]!.AsArray().OfType<JsonObject>(), ProjectRoot()).Where(floor => floor.Category != "Custom").ToArray();
            Assert(realFloors.Length > 0, "Local stock court library is missing.");
            var samplePaths = realFloors.OrderBy(floor => floor.Name).Take(30).Select(floor => floor.PreviewPath).Distinct().Where(File.Exists).ToArray();
            var originalPreviews = samplePaths.ToDictionary(path => path, path => (Hash: SHA256.HashData(File.ReadAllBytes(path)), Time: File.GetLastWriteTimeUtc(path)));
            var realCalls = 0;
            Task<BitmapSource> RealDecode(StockFloor floor, CancellationToken token) => Task.Run(() =>
            { Assert(Environment.CurrentManagedThreadId != uiThread, "Real stock thumbnail decode ran on the dispatcher."); Interlocked.Increment(ref realCalls); return StudioImages.Load(floor.PreviewPath, 208); }, token);
            watch.Restart();
            var realCatalog = new FloorCatalogWindow(null, realFloors, realFloors[0], [], [], RealDecode, true);
            var realConstructorMs = watch.Elapsed.TotalMilliseconds;
            try
            {
                Assert(realCalls == 0, "Real catalog construction read stock preview files.");
                var realContent = (FrameworkElement)realCatalog.Content;
                realContent.Measure(new Size(860, 690)); realContent.Arrange(new Rect(0, 0, 860, 690)); realContent.UpdateLayout();
                await realCatalog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); await realCatalog.WaitForThumbnailsAsync();
                var realImages = Descendants<Image>(realContent).ToArray();
                Assert(realImages.Length > 0 && realImages.Any(image => image.Source is BitmapSource) && realImages.Length < 60 && realCalls < 60,
                    "Real stock previews were blank or the catalog eagerly decoded the whole library.");
                var rendered = new RenderTargetBitmap(860, 690, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(realCatalog.Background, null, new Rect(0, 0, 860, 690));
                rendered.Render(background); rendered.Render(realContent); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using (var stream = File.Create(Path.Combine(root, "catalog-real-stock.png"))) encoder.Save(stream);
                foreach (var (path, saved) in originalPreviews)
                    Assert(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(saved.Hash) && File.GetLastWriteTimeUtc(path) == saved.Time, "Catalog changed a real stock preview.");
                var cache = StudioImages.CacheStatistics; Assert(cache.Count <= 80 && cache.Bytes <= cache.Budget, "Stock thumbnails bypassed the bitmap cache budget.");
                File.WriteAllText(Path.Combine(root, "catalog-real-metrics.json"), new JsonObject
                { ["localStockCourts"] = realFloors.Length, ["constructorMs"] = realConstructorMs, ["realizedImages"] = realImages.Length, ["decodeCalls"] = realCalls,
                  ["boundedCacheBytes"] = cache.Bytes, ["nativeWindowsOpened"] = false, ["coldDiskBenchmark"] = false }.ToJsonString());
            }
            finally { realCatalog.Close(); await realCatalog.WaitForThumbnailsAsync(); }
        }
        Console.WriteLine($"PASS court catalog: 5000 lightweight rows; constructor {constructorMs:0.0}ms with zero image reads; realized-only background thumbnails, two-decoder limit; responsive search, stale-row/close cancellation, malformed-preview fallback/retry, favorites without reload, filters/ID search/debounce, compact render and full scroll. No native windows opened.");
    }
}
