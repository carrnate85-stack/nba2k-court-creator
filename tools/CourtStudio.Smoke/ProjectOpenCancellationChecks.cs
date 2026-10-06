using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckProjectOpenCancellation(string output)
    {
        foreach (var scenario in new[] { "read", "read-failure", "logo", "logos", "logo-failure", "close-failure" })
        {
            var root = Path.GetFullPath(Path.Combine(output, "cancel-open-" + scenario + "-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var projectPath = Path.Combine(root, "incoming.court.json");
            var recoveryPath = Path.Combine(root, "recovery.json");
            var sourceLogo = Path.Combine(root, "original-logo.png"); WriteLogoExample(sourceLogo);
            var sourceImage = StudioImages.Load(sourceLogo);
            var ownedLogo = Path.Combine(root, "uncommitted-logo.png");
            var preparedLogo = Path.Combine(root, "already-prepared-logo.png");
            var foreign = Path.Combine(root, "personal-note.txt"); File.WriteAllText(foreign, "Preserve this file.");
            var sourceHash = SHA256.HashData(File.ReadAllBytes(sourceLogo));
            var sourceTime = File.GetLastWriteTimeUtc(sourceLogo);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var logoResult = new TaskCompletionSource<PreparedLogoAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseRead = new ManualResetEventSlim();
            using var releaseWrite = new ManualResetEventSlim();
            var holdRead = false; var holdWrite = false; var failWrite = false;
            CancellationToken logoToken = default; var preparationCount = 0;
            JsonObject Read(string path)
            {
                if (Volatile.Read(ref holdRead))
                {
                    readStarted.TrySetResult();
                    Assert(releaseRead.Wait(TimeSpan.FromSeconds(20)), "Cancellation reader timed out.");
                    if (scenario == "read-failure") throw new IOException("Late canceled reader failure.");
                }
                return StudioProjectStore.Read(path);
            }
            Task<PreparedLogoAsset> Prepare(string path, CancellationToken token)
            {
                if (scenario == "logos" && ++preparationCount == 1)
                { File.Copy(sourceLogo, preparedLogo); return Task.FromResult(new PreparedLogoAsset(sourceImage, preparedLogo, true)); }
                logoToken = token; logoStarted.TrySetResult(); return logoResult.Task;
            }
            var writer = new StudioSnapshotWriter(project =>
            {
                if (Volatile.Read(ref holdWrite))
                {
                    writeStarted.TrySetResult();
                    Assert(releaseWrite.Wait(TimeSpan.FromSeconds(20)), "Cancellation recovery writer timed out.");
                    if (Volatile.Read(ref failWrite)) throw new IOException("Controlled final recovery failure.");
                }
                StudioProjectStore.WriteRecoveryTo(recoveryPath, project);
            });
            var preferences = new StudioPreferenceStore(root);
            var appRoot = ProjectRoot();
            var window = new StudioWindow(true, new PythonServiceClient(appRoot, null), new PythonServiceClient(appRoot, null),
                prepareLogo: Prepare, recovery: writer, readProject: Read, preferences: preferences);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            Task? opening = null;
            string History(string field) => string.Join("\n", ((List<JsonObject>)typeof(StudioWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Select(project => project.ToJsonString()));
            try
            {
                await window.InitializeAsync();
                window.SetLayerSettings("paint-left", color: "#112233");
                window.SetLayerSettings("paint-left", color: "#223344");
                await window.UndoAsync();
                var before = window.CreateProject().ToJsonString(); var undo = History("_undo"); var redo = History("_redo");
                await window.QueueRecovery()!; await preferences.FlushAsync();
                var recoveryBefore = File.ReadAllBytes(recoveryPath);
                var incoming = window.CreateProject(); incoming["projectName"] = "Must not be committed"; incoming["outsideColor"] = "#FFFFFF";
                var logoScenario = scenario.StartsWith("logo", StringComparison.Ordinal);
                if (logoScenario) incoming["logoImages"] = new JsonArray(new JsonObject
                { ["id"] = "late-logo", ["name"] = "Late logo", ["path"] = sourceLogo, ["x"] = 4000, ["y"] = 2000, ["width"] = 400, ["height"] = 400 });
                if (scenario == "logos") incoming["logoImages"]!.AsArray().Insert(0, new JsonObject
                { ["id"] = "ready-logo", ["name"] = "Prepared logo", ["path"] = sourceLogo, ["x"] = 3500, ["y"] = 2000, ["width"] = 400, ["height"] = 400 });
                StudioProjectStore.Write(projectPath, incoming);
                var projectHash = SHA256.HashData(File.ReadAllBytes(projectPath)); var projectTime = File.GetLastWriteTimeUtc(projectPath);
                Volatile.Write(ref holdRead, !logoScenario);
                opening = window.OpenProjectFromAsync(projectPath);
                await (logoScenario ? logoStarted.Task : readStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                Assert(!opening.IsCompleted && !window.IsVisible && window.CreateProject().ToJsonString() == before, "Cancellation fixture committed early or showed a window.");
                if (scenario == "logos") Assert(File.Exists(preparedLogo), "Multi-logo cancellation did not prepare the first owned file.");
                Volatile.Write(ref holdWrite, true); Volatile.Write(ref failWrite, scenario == "close-failure");
                window.Close();
                await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(window.RecoveryClosePending && !closed.Task.IsCompleted && !opening.IsCompleted, "Closing waited for the load or bypassed the final recovery flush.");
                if (logoScenario) Assert(logoToken.CanBeCanceled && logoToken.IsCancellationRequested, "Project logo preparation did not receive cancellation.");
                var revision = writer.LatestRevision; window.Close();
                Assert(writer.LatestRevision == revision, "Repeated close queued another final write.");
                releaseWrite.Set();
                if (scenario == "close-failure")
                {
                    var closeWork = window.PendingCloseRecovery;
                    if (closeWork is not null) Assert(!await closeWork.WaitAsync(TimeSpan.FromSeconds(10)), "Failed recovery allowed close.");
                    await window.Dispatcher.InvokeAsync(() => true);
                    Assert(!closed.Task.IsCompleted && !window.RecoveryClosePending && !((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled
                        && File.ReadAllBytes(recoveryPath).SequenceEqual(recoveryBefore), "Failed close bypassed the pending load guard or lost prior recovery.");
                }
                else
                {
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert(!opening.IsCompleted && StudioProjectStore.Read(recoveryPath).ToJsonString() == before,
                        "Close waited for the blocked load or saved the incoming court.");
                    Assert(!File.Exists(preparedLogo), "Closing retained an uncommitted logo until the blocked load resumed.");
                    Assert(StudioPreferences.Read(Path.Combine(root, "preferences.json")).Recent.First() == window.CreateProject()["floor"]!["id"]!.GetValue<string>(), "Closing did not flush current preferences.");
                }
                if (logoScenario)
                {
                    if (scenario == "logo-failure") logoResult.TrySetException(new IOException("Late canceled logo failure."));
                    else { File.Copy(sourceLogo, ownedLogo); logoResult.TrySetResult(new PreparedLogoAsset(sourceImage, ownedLogo, true)); }
                }
                else releaseRead.Set();
                await Expect<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert(window.CreateProject().ToJsonString() == before && History("_undo") == undo && History("_redo") == redo
                    && !File.Exists(ownedLogo) && !File.Exists(preparedLogo) && File.ReadAllText(foreign) == "Preserve this file."
                    && SHA256.HashData(File.ReadAllBytes(sourceLogo)).SequenceEqual(sourceHash) && File.GetLastWriteTimeUtc(sourceLogo) == sourceTime
                    && SHA256.HashData(File.ReadAllBytes(projectPath)).SequenceEqual(projectHash) && File.GetLastWriteTimeUtc(projectPath) == projectTime,
                    "Late load results changed court/history/source files or leaked an owned logo.");
                if (scenario == "close-failure")
                {
                    Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Canceled load did not release editing after failed close.");
                    Volatile.Write(ref holdWrite, false); Volatile.Write(ref failWrite, false);
                    window.SetLayerSettings("paint-left", color: "#445566");
                    var edited = window.CreateProject().ToJsonString(); window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert(StudioProjectStore.Read(recoveryPath).ToJsonString() == edited, "Close retry did not save the retained, edited court.");
                }
            }
            finally
            {
                releaseRead.Set(); releaseWrite.Set(); Volatile.Write(ref holdWrite, false); Volatile.Write(ref failWrite, false);
                logoResult.TrySetCanceled();
                if (opening is not null) { try { await opening.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception error) { System.Diagnostics.Trace.WriteLine("Cancellation fixture cleanup: " + error.Message); } }
                if (!closed.Task.IsCompleted) window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await writer.FlushAsync(); await preferences.FlushAsync();
            }
        }
        Console.WriteLine("PASS project load cancellation: close flushes the preceding court/settings without waiting for a held read or logo decode; late success/failure cannot commit; cancellation reaches artwork preparation; uncommitted owned files removed; source/history preserved; failed final flush keeps the app open, releases editing after cancellation and retries. No native windows opened.");
    }
}
