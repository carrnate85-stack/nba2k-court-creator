using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckRecovery(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "recovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "coalesced.json");
        var uiThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = new List<string>(); var active = 0; var maximumActive = 0;
        Exception? failure = null;
        var writer = new StudioSnapshotWriter(project =>
        {
            Assert(Environment.CurrentManagedThreadId != uiThread, "Recovery disk work ran on the UI thread.");
            var count = Interlocked.Increment(ref active); maximumActive = Math.Max(maximumActive, count);
            try
            {
                if (writes.Count == 0)
                { started.TrySetResult(); Assert(release.Wait(TimeSpan.FromSeconds(20)), "Controlled recovery write timed out."); }
                if (failure is not null) throw failure;
                writes.Add(project["projectName"]!.GetValue<string>());
                StudioProjectStore.WriteRecoveryTo(path, project);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var original = PublicationProject("First");
        var pending = writer.Queue(original);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            original["projectName"] = "Changed caller object";
            for (var index = 0; index < 1000; index++)
            {
                var next = PublicationProject("Latest " + index);
                Assert(ReferenceEquals(writer.Queue(next), pending), "Rapid edits created overlapping recovery workers.");
                next["projectName"] = "Changed queued object";
            }
            Assert(ReferenceEquals(writer.FlushAsync(), pending) && !pending.IsCompleted, "Flush did not await the current writer.");
            Assert(await Application.Current.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Dispatcher did not run during recovery.");
            release.Set();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(result.Error is null && result.Revision == 1001 && writer.LatestRevision == 1001 && maximumActive == 1,
                "Recovery revision or serialization invariant failed.");
            Assert(writes.SequenceEqual(new[] { "First", "Latest 999" })
                && StudioProjectStore.Read(path)["projectName"]!.GetValue<string>() == "Latest 999"
                && StudioProjectStore.Read(path + ".bak")["projectName"]!.GetValue<string>() == "First",
                "Coalescing lost the latest snapshot, mutated a captured snapshot, or damaged the previous recovery.");
            var before = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
            foreach (var error in new Exception[] { new IOException("disk failure"), new UnauthorizedAccessException("permission failure"), new InvalidDataException("snapshot failure") })
            {
                failure = error;
                var failed = await writer.Queue(PublicationProject("Must not publish"));
                Assert(ReferenceEquals(failed.Error, error) && ReferenceEquals((await writer.FlushAsync()).Error, error)
                    && File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup),
                    "Failure was lost or changed recovery files.");
            }
            failure = null;
            var recovered = await writer.Queue(PublicationProject("Recovered"));
            Assert(recovered.Error is null && recovered.Revision == writer.LatestRevision
                && StudioProjectStore.Read(path)["projectName"]!.GetValue<string>() == "Recovered", "Writer did not recover after failure.");
            var lockedPrimary = File.ReadAllBytes(path); var lockedBackup = File.ReadAllBytes(path + ".bak");
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var blocked = writer.Queue(PublicationProject("Must not bypass a Windows lock"));
                Assert(await Application.Current.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Real recovery file retries blocked the dispatcher.");
                var denied = await blocked.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(denied.Error is not null && StudioProjectStore.RetryableFileAccess(denied.Error)
                    && File.ReadAllBytes(path).SequenceEqual(lockedPrimary) && File.ReadAllBytes(path + ".bak").SequenceEqual(lockedBackup),
                    "Background recovery bypassed a real lock or damaged the primary/backup.");
            }
            Assert((await writer.Queue(PublicationProject("Windows lock released"))).Error is null, "Background recovery did not resume after its file lock cleared.");
            for (var index = 0; index < 50; index++)
            {
                var resultAfterRestart = await writer.Queue(PublicationProject("Restart " + index));
                Assert(resultAfterRestart.Error is null && resultAfterRestart.Revision == writer.LatestRevision,
                    $"Idle writer restart {index} failed: completed revision {resultAfterRestart.Revision}, latest {writer.LatestRevision}; {resultAfterRestart.Error}");
            }
        }
        finally { release.Set(); await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10)); }

        await CheckWindowRecovery(root, failClose: false);
        await CheckWindowRecovery(root, failClose: true);
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Recovery staging files were left behind.");
        Console.WriteLine("PASS recovery: single background writer; 1000-edit coalescing; immutable snapshots; exact latest/backup; failure/flush/retry/idle restart; real debounce; responsive dispatcher; final close flush, repeated close and mutation guards; failed close retains files/re-enables editing; fast close does not re-enter Closing. All windows remain invisible.");
    }

    private static async Task CheckWindowRecovery(string root, bool failClose)
    {
        var path = Path.Combine(root, failClose ? "failed-close.json" : "final-close.json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var hold = false; var fail = false;
        var writer = new StudioSnapshotWriter(project =>
        {
            if (Volatile.Read(ref hold))
            { started.TrySetResult(); Assert(release.Wait(TimeSpan.FromSeconds(20)), "Window recovery write timed out."); }
            if (Volatile.Read(ref fail)) throw new IOException("controlled recovery publication failure");
            StudioProjectStore.WriteRecoveryTo(path, project);
        });
        var projectRoot = ProjectRoot();
        var window = new StudioWindow(true, new PythonServiceClient(projectRoot, null), new PythonServiceClient(projectRoot, null), recovery: writer);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await window.InitializeAsync();
            await Task.Delay(600); await writer.FlushAsync();
            Assert(!window.IsVisible, "Recovery test opened a native window.");
            if (failClose)
            {
                await window.QueueRecovery()!;
                var primary = File.ReadAllBytes(path); var backup = File.Exists(path + ".bak") ? File.ReadAllBytes(path + ".bak") : null;
                Volatile.Write(ref fail, true);
                Volatile.Write(ref hold, true);
                window.Close(); var closing = window.PendingCloseRecovery;
                Assert(closing is not null, "Close did not attempt the final recovery.");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                release.Set();
                Assert(!await closing!, "Failed recovery allowed close without consent.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert(!closed.Task.IsCompleted && !window.RecoveryClosePending
                    && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled
                    && ((Button)window.FindName("SaveToolbarButton")).IsEnabled
                    && ((TextBlock)window.FindName("StatusText")).Text.Contains("controlled recovery publication failure")
                    && File.ReadAllBytes(path).SequenceEqual(primary)
                    && (backup is null ? !File.Exists(path + ".bak") : File.ReadAllBytes(path + ".bak").SequenceEqual(backup)),
                    "Failed close lost recovery files, failed to report the error, or left editing disabled.");
                Volatile.Write(ref fail, false);
                Volatile.Write(ref hold, false);
                window.SetLayerSettings("paint-left", color: "#ABCDEF");
                await window.QueueRecovery()!;
                window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(StudioProjectStore.Read(path)["paintSettings"]!["paint-left"]!["color"]!.GetValue<string>() == "#ABCDEF", "Successful retry lost edited state.");
            }
            else
            {
                Volatile.Write(ref hold, true);
                window.SetLayerSettings("paint-left", color: "#123456");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && !writer.FlushAsync().IsCompleted, "Autosave disabled editing or did not run asynchronously.");
                Assert(await window.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Slow autosave blocked the dispatcher.");
                window.SetLayerSettings("paint-left", color: "#234567");
                window.Close(); var closing = window.PendingCloseRecovery;
                Assert(closing is not null && !closing.IsCompleted && window.RecoveryClosePending && !closed.Task.IsCompleted, "Close did not wait for the final recovery snapshot.");
                var revision = writer.LatestRevision; window.Close();
                Assert(writer.LatestRevision == revision && !closed.Task.IsCompleted, "Repeated close queued another snapshot or bypassed flush.");
                Assert(window.QueueRecovery() is null && writer.LatestRevision == revision, "Closing allowed another autosave to replace the final snapshot.");
                var before = window.CreateProject().ToJsonString();
                window.SetLayerSettings("paint-left", color: "#FFFFFF");
                await Expect<InvalidOperationException>(() => window.SelectFloorAsync(window.Floors[1]));
                await Expect<InvalidOperationException>(() => window.SaveProjectToAsync(Path.Combine(root, "should-not-save.json")));
                await Expect<InvalidOperationException>(() => window.RestoreProjectAsync(window.CreateProject()));
                await Expect<InvalidOperationException>(() => window.ExportToAsync(Path.Combine(root, "should-not-export.png"), false));
                Assert(window.CreateProject().ToJsonString() == before && !((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled
                    && !((Button)window.FindName("MoveToolButton")).IsEnabled && !((Button)window.FindName("SelectedCourtCard")).IsEnabled,
                    "Close flush allowed document mutation.");
                Assert(await window.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Close flush blocked the dispatcher.");
                release.Set(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(await closing!, "Completed final recovery did not permit close.");
                var saved = StudioProjectStore.Read(path);
                Assert(saved["paintSettings"]!["paint-left"]!["color"]!.GetValue<string>() == "#234567"
                    && saved["logoImages"]!.AsArray().Count == 0 && window.QueueRecovery() is null, "Final recovery was stale or a closed window queued more writes.");
            }
        }
        finally
        {
            Volatile.Write(ref fail, false); release.Set();
            if (!closed.Task.IsCompleted)
            { if (window.PendingCloseRecovery is { } pendingClose) await pendingClose; window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
