using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckProjectOpen(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "open-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "large.court.json");
        var recoveryPath = Path.Combine(root, "recovery.json");
        var uiThread = Environment.CurrentManagedThreadId;
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        var holdRead = false; var reads = 0;
        JsonObject Read(string path)
        {
            Assert(Environment.CurrentManagedThreadId != uiThread, "Project reading/parsing ran on the UI thread.");
            Interlocked.Increment(ref reads);
            if (Volatile.Read(ref holdRead))
            { readStarted.TrySetResult(); Assert(releaseRead.Wait(TimeSpan.FromSeconds(20)), "Controlled project reader timed out."); }
            return StudioProjectStore.Read(path);
        }
        var writer = new StudioSnapshotWriter(project => StudioProjectStore.WriteRecoveryTo(recoveryPath, project));
        var appRoot = ProjectRoot();
        var window = new StudioWindow(true, new PythonServiceClient(appRoot, null), new PythonServiceClient(appRoot, null), recovery: writer, readProject: Read);
        var closed = false; var didClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => { closed = true; didClose.TrySetResult(); };
        Task? activeOpen = null;
        string History(string field) => string.Join("\n", ((List<JsonObject>)typeof(StudioWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Select(project => project.ToJsonString()));
        void Controls(bool available)
        {
            Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled == available
                && ((Menu)window.FindName("ApplicationMenu")).IsEnabled == available
                && ((FrameworkElement)window.FindName("DocumentChrome")).IsEnabled == available
                && ((Button)window.FindName("SaveToolbarButton")).IsEnabled == available
                && ((Button)window.FindName("ExportTopButton")).IsEnabled == available, "Project open mutation controls disagree.");
        }
        try
        {
            await Expect<InvalidOperationException>(() => window.OpenProjectFromAsync(projectPath));
            Assert(reads == 0, "Uninitialized open read project bytes.");
            await window.InitializeAsync();
            window.SetLayerSettings("paint-left", color: "#112233");
            await window.QueueRecovery()!;
            var before = window.CreateProject().ToJsonString(); var undo = History("_undo"); var redo = History("_redo");
            var recoveryBefore = File.ReadAllBytes(recoveryPath);
            var large = window.CreateProject(); large["projectName"] = "Large opened court"; large["outsideColor"] = "#234567";
            var visibility = large["visibility"]!.AsObject();
            for (var index = 0; index < 100_000; index++) visibility["future-layer-" + index.ToString("D6")] = true;
            StudioProjectStore.Write(projectPath, large);
            Assert(new FileInfo(projectPath).Length > 2 * 1024 * 1024, "Large project fixture is not multi-megabyte.");
            byte[] Hash() { using var stream = File.OpenRead(projectPath); return SHA256.HashData(stream); }
            var sourceHash = Hash(); var sourceTime = File.GetLastWriteTimeUtc(projectPath);
            Volatile.Write(ref holdRead, true);
            var opening = window.OpenProjectFromAsync(projectPath); activeOpen = opening;
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Controls(false);
            Assert(!window.IsVisible && !opening.IsCompleted && window.CreateProject().ToJsonString() == before, "Opening displayed a window or changed the court before preparation.");
            Assert(await window.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Slow project read blocked the dispatcher.");
            window.SetLayerSettings("paint-left", color: "#FFFFFF");
            await Expect<InvalidOperationException>(() => window.OpenProjectFromAsync(projectPath));
            await Expect<InvalidOperationException>(() => window.NewProjectAsync());
            await Expect<InvalidOperationException>(() => window.UndoAsync());
            await Expect<InvalidOperationException>(() => window.RestoreProjectAsync(window.CreateProject()));
            await Expect<InvalidOperationException>(() => window.SelectFloorAsync(window.Floors[1]));
            await Expect<InvalidOperationException>(() => window.AddLogoAsync(Path.Combine(appRoot, "src/NBA2KCourtCreator/Assets/app-icon.png")));
            await Expect<InvalidOperationException>(() => window.SaveProjectToAsync(Path.Combine(root, "must-not-save.json")));
            await Expect<InvalidOperationException>(() => window.ExportToAsync(Path.Combine(root, "must-not-export.png"), false));
            Assert(!closed && window.QueueRecovery() is null && reads == 1
                && window.CreateProject().ToJsonString() == before && History("_undo") == undo && History("_redo") == redo
                && File.ReadAllBytes(recoveryPath).SequenceEqual(recoveryBefore), "Pending open allowed mutation, another reader, history reset or an invalid recovery snapshot.");
            Volatile.Write(ref holdRead, false); releaseRead.Set();
            await opening.WaitAsync(TimeSpan.FromSeconds(20)); Controls(true);
            var opened = window.CreateProject();
            Assert(opened["projectName"]!.GetValue<string>() == "Large opened court" && opened["outsideColor"]!.GetValue<string>() == "#234567"
                && opened["_projectPath"]!.GetValue<string>() == projectPath && History("_undo") == "" && History("_redo") == ""
                && Hash().SequenceEqual(sourceHash) && File.GetLastWriteTimeUtc(projectPath) == sourceTime,
                "Successful open did not commit its own snapshot/path/history or changed source bytes.");
            await writer.FlushAsync();
            Assert(StudioProjectStore.Read(recoveryPath)["projectName"]!.GetValue<string>() == "Large opened court", "Recovery captured the preceding rather than opened document.");

            window.SetLayerSettings("paint-left", color: "#345678");
            await window.QueueRecovery()!;
            var failureBefore = window.CreateProject().ToJsonString(); var failureUndo = History("_undo"); var failureRedo = History("_redo");
            var invalidJson = Path.Combine(root, "invalid-json.court.json"); File.WriteAllText(invalidJson, "{broken json");
            var invalidText = Path.Combine(root, "invalid-utf8.court.json"); File.WriteAllBytes(invalidText, [0xC3, 0x28]);
            var unsupported = Path.Combine(root, "unsupported.court.json"); File.WriteAllText(unsupported, "{\"version\":2,\"buildMode\":\"game-uv\",\"outsideColor\":\"#GGGGGG\"}");
            var deep = Path.Combine(root, "too-deep.court.json"); File.WriteAllText(deep, "{\"future\":" + new string('[', 40) + "0" + new string(']', 40) + "}");
            var oversized = Path.Combine(root, "too-large.court.json"); using (var stream = File.Create(oversized)) stream.SetLength(StudioProjectStore.MaximumJsonBytes + 1);
            var invalidImage = Path.Combine(root, "invalid-floor.png"); File.WriteAllText(invalidImage, "not image bytes");
            var failedAsset = (JsonObject)opened.DeepClone(); failedAsset["outsideColor"] = "#FFFFFF";
            failedAsset["floor"] = new JsonObject { ["id"] = "bad-open-floor", ["name"] = "Invalid opened floor", ["path"] = invalidImage };
            var assetProject = Path.Combine(root, "failed-asset.court.json"); StudioProjectStore.Write(assetProject, failedAsset);
            foreach (var invalidPath in new[] { invalidJson, invalidText, unsupported, deep, oversized, Path.Combine(root, "missing.court.json"), assetProject })
            {
                try { await window.OpenProjectFromAsync(invalidPath); throw new Exception("Invalid project opened: " + invalidPath); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { }
                Controls(true);
                Assert(window.CreateProject().ToJsonString() == failureBefore && History("_undo") == failureUndo && History("_redo") == failureRedo
                    && ((TextBlock)window.FindName("StatusText")).Text.StartsWith("Project could not be loaded:"), "Failed read/validation/asset preparation replaced the court/history or left controls disabled.");
            }
            using (var holder = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.None))
                await Expect<IOException>(() => window.OpenProjectFromAsync(projectPath));
            Controls(true);
            Assert(window.CreateProject().ToJsonString() == failureBefore && History("_undo") == failureUndo, "Locked read changed document or history.");
            await window.UndoAsync(); await window.UndoAsync(true);
            Assert(window.CreateProject().ToJsonString() == failureBefore, "Failed open damaged retained undo/redo snapshots.");
            await window.OpenProjectFromAsync(projectPath); Controls(true);
            Assert(window.CreateProject()["projectName"]!.GetValue<string>() == "Large opened court" && History("_undo") == "", "Opening did not recover after malformed inputs or a file lock.");
        }
        finally
        {
            releaseRead.Set(); Volatile.Write(ref holdRead, false);
            if (activeOpen is not null)
            { try { await activeOpen.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception error) { System.Diagnostics.Trace.WriteLine("Open fixture cleanup observed: " + error.Message); } }
            window.Close();
            await didClose.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.FlushAsync();
            Assert(closed && !window.IsVisible, "Invisible project-open fixture did not close cleanly.");
        }
        await CheckProjectOpenCancellation(output);
        Console.WriteLine("PASS project open: real multi-megabyte parsing/validation off the dispatcher; full-load mutation/overlap/recovery guards; immutable input; source bytes/mtime unchanged; commit-only history reset; JSON/UTF-8/depth/size/missing/locked/asset failures preserve court/undo/redo; retry and final recovery. No native windows opened.");
    }
}
