using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckHistoryRestore(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var root = ProjectRoot(); var reads = 0; var logoReads = 0; var fail = false;
        using var release = new ManualResetEventSlim(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapSource Load(string path, int width)
        {
            Interlocked.Increment(ref reads);
            if (fail) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); throw new IOException("Held undo asset failure"); }
            return StudioImages.Load(path, width);
        }
        async Task<PreparedLogoAsset> Logo(string path, CancellationToken cancellation)
        { ++logoReads; return new PreparedLogoAsset(await StudioImages.LoadLogoAsync(path, cancellation), path); }
        var window = new StudioWindow(true, new PythonServiceClient(root, null), new PythonServiceClient(root, null), loadFloorImage: Load, prepareLogo: Logo);
        var workspace = (FrameworkElement)window.FindName("WorkspaceRoot");
        var shield = (FrameworkElement)window.FindName("HistoryInputShield");
        void Ready() => Assert(workspace.IsEnabled && shield.Visibility == Visibility.Collapsed, "History did not release the workspace.");
        void Busy()
        {
            Assert(workspace.IsEnabled && ((FrameworkElement)window.FindName("DocumentChrome")).IsEnabled && shield.Visibility == Visibility.Visible,
                "Undo greyed the workspace instead of blocking input without dimming it.");
        }
        try
        {
            await window.InitializeAsync(); Layout(window, 1440, 900);
            var logoPath = Path.Combine(output, "history-logo.bmp"); WriteRevisionBitmap(logoPath, Colors.Gold);
            await window.AddLogoAsync(logoPath); var originalImage = window.Canvas.SelectedLayer!.Image;
            var baseline = window.CreateProject(); reads = logoReads = 0;
            var originalLayers = ((FrameworkElement)window.FindName("LayersHost"));
            var firstGroup = System.Windows.Media.VisualTreeHelper.GetChild(originalLayers, 0);
            var originalLogo = window.Canvas.SelectedLayer;
            window.SetLayerSettings("paint-left", color: "#123456"); var changed = window.CreateProject();
            var beforeReads = StudioImages.ReadStatistics; var times = new List<double>();
            for (var index = 0; index < 8; ++index)
            {
                var timer = Stopwatch.StartNew(); var undo = window.UndoAsync(); Busy(); await undo; times.Add(timer.Elapsed.TotalMilliseconds); Ready();
                Assert(JsonNode.DeepEquals(window.CreateProject(), baseline), "Fast color undo changed the project or source pins.");
                var redo = window.UndoAsync(true); Busy(); await redo; Ready();
                Assert(JsonNode.DeepEquals(window.CreateProject(), changed), "Fast color redo changed the project or source pins.");
                Assert(ReferenceEquals(window.Canvas.SelectedLayer!.Image, originalImage), "Unchanged logo preview was prepared again.");
                Assert(ReferenceEquals(window.Canvas.SelectedLayer, originalLogo) && ReferenceEquals(System.Windows.Media.VisualTreeHelper.GetChild(originalLayers, 0), firstGroup), "Unchanged layers or color controls were recreated during undo.");
            }
            Assert(reads == 0 && logoReads == 0 && StudioImages.ReadStatistics == beforeReads, "Color history reread unchanged assets.");
            await window.SetHardwoodTextureAsync(new(Brightness: 12)); var textured = window.CreateProject(); reads = 0;
            await window.UndoAsync(); await window.UndoAsync(true);
            Assert(reads == 0 && JsonNode.DeepEquals(window.CreateProject(), textured), "Texture history redecoded hardwood or lost its settings.");
            var retained = window.CreateProject(); window.SetLayerSettings("stock-outside", color: "#708090");
            await window.SelectFloorAsync(window.Floors.First(floor => floor.Id != retained["floor"]!["id"]!.GetValue<string>()));
            var priorFailure = window.CreateProject(); var oldDrawing = window.Canvas.BackgroundDrawing;
            fail = true; release.Reset(); var failedUndo = window.UndoAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Busy();
            window.SetLayerSettings("paint-left", color: "#ABCDEF");
            Assert(JsonNode.DeepEquals(window.CreateProject(), priorFailure), "An edit raced a pending history restore.");
            await Expect<InvalidOperationException>(() => window.UndoAsync());
            release.Set(); await Expect<IOException>(() => failedUndo); Ready();
            Assert(JsonNode.DeepEquals(window.CreateProject(), priorFailure) && ReferenceEquals(oldDrawing, window.Canvas.BackgroundDrawing), "Failed undo replaced the visible court.");
            fail = false; await window.UndoAsync(); Ready();
            Assert(JsonNode.DeepEquals(window.CreateProject(), retained), "Failed undo consumed history or prevented retry.");
            Snapshot(window, Path.Combine(output, "workspace-lower.png"), 1440, 900);
            File.WriteAllText(Path.Combine(output, "history-audit.json"), new JsonObject {
                ["unchangedAssetReads"] = 0, ["unchangedLogoPreparations"] = 0,
                ["warmColorUndoMilliseconds"] = new JsonArray(times.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["noWorkspaceDimming"] = true, ["failedUndoRetainsDocumentAndHistory"] = true, ["nativeWindowsOpened"] = false }.ToJsonString());
            Console.WriteLine($"PASS history restore: unchanged hardwood/logo previews reused with zero source reads, exact color/texture undo-redo, no workspace dimming, guarded overlapping edits, failure retains court/history and retry succeeds. Warm color undo median {times.Order().ElementAt(times.Count / 2):F1} ms. No native windows opened.");
        }
        finally { release.Set(); window.Close(); }
    }
}
