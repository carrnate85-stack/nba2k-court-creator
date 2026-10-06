using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckFloorRequests(string output)
    {
        var root = ProjectRoot();
        using var release = new ManualResetEventSlim(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false; var fail = false; var active = 0; var maximum = 0; var largeReads = 0; var thumbnailReads = 0;
        BitmapSource Load(string path, int width)
        {
            if (!Volatile.Read(ref hold)) return StudioImages.Load(path, width);
            if (width != 2048) { Interlocked.Increment(ref thumbnailReads); return StudioImages.Load(path, width); }
            Interlocked.Increment(ref largeReads);
            var concurrent = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref maximum); }
            while (concurrent > observed && Interlocked.CompareExchange(ref maximum, concurrent, observed) != observed);
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Held floor decoder was not released.");
                if (Volatile.Read(ref fail)) throw new IOException("Injected stale floor decode failure.");
                return StudioImages.Load(path, width);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var window = new StudioWindow(true, new PythonServiceClient(root, null), new PythonServiceClient(root, null), loadFloorImage: Load);
        void Hold()
        {
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            largeReads = thumbnailReads = maximum = 0; fail = false; release.Reset(); Volatile.Write(ref hold, true);
        }
        string FloorId() => window.CreateProject()["floor"]!["id"]!.GetValue<string>();
        try
        {
            await window.InitializeAsync();
            var floors = window.Floors.Take(4).ToArray();
            Assert(floors.Length == 4, "Floor request checks need four stock floors.");
            var original = FloorId();
            Hold();
            var first = window.SelectFloorAsync(floors[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var burst = Enumerable.Range(0, 100).Select(index => window.SelectFloorAsync(floors[1 + index % 3])).ToArray();
            await Task.Delay(200);
            var bounded = Volatile.Read(ref maximum) == 1 && Volatile.Read(ref largeReads) == 1;
            Assert(FloorId() == original, "A pending hardwood request changed the committed floor.");
            release.Set(); await Task.WhenAll(burst.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(bounded, $"Rapid floor changes started {maximum} concurrent decoders and {largeReads} full image reads.");
            Assert(largeReads == 2 && thumbnailReads == 1, "Obsolete floor requests decoded images/thumbnails instead of being skipped.");
            Assert(FloorId() == floors[1].Id && ((TextBlock)window.FindName("SelectedCourtText")).Text == floors[1].Name, "Latest requested floor did not own the preview/selected label.");

            Hold(); var staleFailure = window.SelectFloorAsync(floors[2]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = window.SelectFloorAsync(floors[3]);
            Volatile.Write(ref fail, true); release.Set();
            await staleFailure.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect<IOException>(() => replacement);
            Assert(FloorId() == floors[1].Id, "Failed latest floor preparation replaced the committed court.");
            Volatile.Write(ref hold, false); Volatile.Write(ref fail, false);
            await window.SelectFloorAsync(floors[3]);
            Assert(FloorId() == floors[3].Id, "A current decode failure left the floor decoder stuck.");

            var originalColor = window.CreateProject()["outsideColor"]!.GetValue<string>();
            window.SetLayerSettings("stock-outside", color: "#506070");
            Hold(); var beforeUndo = window.SelectFloorAsync(floors[2]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var undo = window.UndoAsync();
            await Task.Delay(100);
            Assert(largeReads == 1, "Undo started a concurrent floor decode.");
            release.Set(); await Task.WhenAll(beforeUndo, undo).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(FloorId() == floors[3].Id && window.CreateProject()["outsideColor"]!.GetValue<string>() == originalColor, "Late floor selection changed the undone document.");

            Hold(); var beforeSave = window.SelectFloorAsync(floors[1]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var savedPath = Path.GetFullPath(Path.Combine(output, "floor-request-save-" + Guid.NewGuid().ToString("N") + ".court.json"));
            await window.SaveProjectToAsync(savedPath).WaitAsync(TimeSpan.FromSeconds(10));
            Assert(!beforeSave.IsCompleted && StudioProjectStore.Read(savedPath)["floor"]!["id"]!.GetValue<string>() == floors[3].Id, "Save waited for or included an uncommitted floor request.");
            release.Set(); await beforeSave.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(FloorId() == floors[3].Id && thumbnailReads == 0, "Saved court accepted a canceled floor selection.");

            Hold(); var beforeOpen = window.SelectFloorAsync(floors[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var open = window.OpenProjectFromAsync(savedPath);
            await Task.Delay(100);
            Assert(largeReads == 1, "Open started another decoder while the obsolete floor was still loading.");
            release.Set(); await Task.WhenAll(beforeOpen, open).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(FloorId() == floors[3].Id, "An obsolete floor request replaced the opened portable floor.");

            Hold(); var oldDocument = window.SelectFloorAsync(floors[1]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reset = window.NewProjectAsync();
            await Task.Delay(100);
            Assert(largeReads == 1, "New started another hardwood decoder before the obsolete one finished.");
            release.Set(); await Task.WhenAll(oldDocument, reset).WaitAsync(TimeSpan.FromSeconds(15));
            var resetProject = window.CreateProject().ToJsonString();
            Assert(FloorId() == original && window.Canvas.Layers.Count == 0, "New did not retain the default court after a late floor request.");

            Hold(); var closing = window.SelectFloorAsync(floors[2]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = window.SelectFloorAsync(floors[3]);
            window.Close();
            await queued.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!closing.IsCompleted && largeReads == 1, "Close waited for or launched more synchronous image reads.");
            release.Set(); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(window.CreateProject().ToJsonString() == resetProject && thumbnailReads == 0, "Closed studio accepted late floor artwork or decoded its thumbnail.");
            File.WriteAllText(Path.Combine(output, "floor-request-audit.json"), new JsonObject
            {
                ["burstRequests"] = 101, ["maximumConcurrentDecoders"] = 1,
                ["burstFullImageReads"] = 2, ["burstThumbnailReads"] = 1,
                ["latestSelectionWins"] = true, ["staleFailureSuppressed"] = true,
                ["currentFailureRecoverable"] = true, ["newInvalidatesPendingSelection"] = true,
                ["undoInvalidatesPendingSelection"] = true, ["saveInvalidatesPendingSelection"] = true,
                ["openInvalidatesPendingSelection"] = true,
                ["closeCancelsQueuedWork"] = true, ["nativeWindowsOpened"] = false
            }.ToJsonString());
        }
        finally { release.Set(); Volatile.Write(ref hold, false); window.Close(); }
        Console.WriteLine("PASS floor requests: 101-request burst limited to one decoder/two full reads, stale failures suppressed, current failures recoverable, New/Open/Undo/Save/Close invalidation, no late commit.");
    }
}
