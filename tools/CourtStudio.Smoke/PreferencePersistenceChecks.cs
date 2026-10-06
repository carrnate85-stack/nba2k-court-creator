using System.IO;
using System.Text.Json.Nodes;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TwoK.Studio;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckPreferencesPersistence(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "preferences-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "preferences.json");
        var settings = new JsonObject { ["favorites"] = new JsonArray("favorite-court"), ["recent"] = new JsonArray("recent-court"), ["dark"] = true };
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Write(Path.Combine(root, "not-a-project.json"), settings)));
        Assert(!File.Exists(Path.Combine(root, "not-a-project.json")), "Preference fix weakened court-project schema validation.");
        StudioPreferences.Write(path, settings);
        var loaded = StudioPreferences.Read(path);
        Assert(loaded.Dark && loaded.Favorites.SequenceEqual(new[] { "favorite-court" }) && loaded.Recent.SequenceEqual(new[] { "recent-court" }), "Preferences did not round-trip.");
        var old = File.ReadAllBytes(path); var oldTime = File.GetLastWriteTimeUtc(path);
        var foreign = Path.Combine(root, ".preferences-personal-note.tmp"); File.WriteAllText(foreign, "personal staging sentinel");
        foreach (var invalid in new JsonObject[] { new() { ["dark"] = "yes" }, new() { ["favorites"] = new JsonArray(3) },
            new() { ["recent"] = new JsonArray("a\0b") }, new() { ["unrecognized"] = true },
            new() { ["favorites"] = new JsonArray(Enumerable.Range(0, 10001).Select(i => (JsonNode?)JsonValue.Create("id-" + i)).ToArray()) },
            new() { ["favorites"] = new JsonArray(Enumerable.Range(0, 1500).Select(i => (JsonNode?)JsonValue.Create(new string('x', 1000) + i)).ToArray()) } })
        {
            await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Write(path, invalid)));
            Assert(File.ReadAllBytes(path).SequenceEqual(old) && File.GetLastWriteTimeUtc(path) == oldTime, "Invalid settings changed the existing preferences.");
        }
        using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var write = Task.Run(() => StudioPreferences.Write(path, new StudioPreferences(["changed"], ["changed"], false).ToDocument()));
            Assert(await Application.Current.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Preference file retries blocked the dispatcher.");
            try { await write; throw new Exception("Preference sharing lock was bypassed."); }
            catch (Exception error) when (StudioProjectStore.RetryableFileAccess(error)) { }
            Assert(File.ReadAllBytes(path).SequenceEqual(old), "Preference writer bypassed a real sharing lock.");
        }
        var source = Path.Combine(root, "personal-settings.json"); File.WriteAllBytes(source, old);
        var alias = Path.Combine(root, "hardlink.json"); Assert(CreateHardLink(alias, source, IntPtr.Zero), "Could not create preference alias fixture.");
        await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Write(alias, settings)));
        Assert(File.ReadAllBytes(source).SequenceEqual(old) && File.ReadAllBytes(alias).SequenceEqual(old), "Preference write changed a hard-linked source.");
        var malformed = Path.Combine(root, "malformed.json");
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{broken"), new byte[] { 0xC3, 0x28 }, Encoding.UTF8.GetBytes("{\"unknown\":true}"),
            Encoding.UTF8.GetBytes("{\"recent\":" + new string('[', 10) + "0" + new string(']', 10) + "}") })
        {
            File.WriteAllBytes(malformed, bytes);
            await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Read(malformed)));
            await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Write(malformed, settings)));
            Assert(File.ReadAllBytes(malformed).SequenceEqual(bytes), "Malformed private settings were overwritten.");
        }
        using (var stream = File.Create(malformed)) stream.SetLength(StudioPreferences.MaximumBytes + 1L);
        await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Read(malformed)));
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
        { File.WriteAllText(malformed, settings.ToJsonString(), encoding); Assert(StudioPreferences.Read(malformed).Dark, "Existing BOM encoding compatibility was lost."); }
        await CheckPreferenceLinks(root, settings);
        Assert(Directory.GetFiles(root, ".preferences-*.tmp").SequenceEqual(new[] { foreign }), "Preference failure leaked staging or removed a foreign file.");
        await CheckPreferenceCoalescing(root);
        await CheckPreferenceWindow(root, malformed: false);
        await CheckPreferenceWindow(root, malformed: true);
        Console.WriteLine("PASS preference persistence: reproduced project-schema regression; independent bounded settings schema; project validation retained; normalized round-trip/BOM; malformed/oversized/NUL/unknown settings preserved; real lock and hard-link/junction guards; coalesced background writes; production startup/floor/theme/favorites/restart; combined final close flush/failure/retry. No app windows or real user settings touched.");
    }

    private static async Task CheckPreferenceLinks(string root, JsonObject settings)
    {
        var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
        var link = Path.Combine(root, "linked");
        try { Directory.CreateSymbolicLink(link, external); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert(Path.GetFullPath(link).StartsWith(root + Path.DirectorySeparatorChar) && Path.GetFullPath(external).StartsWith(root + Path.DirectorySeparatorChar), "Linked fixture escaped its root.");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["COURT_FIXTURE_LINK"] = link; start.Environment["COURT_FIXTURE_TARGET"] = external;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:COURT_FIXTURE_LINK -Value $env:COURT_FIXTURE_TARGET | Out-Null" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(); await stdout; Assert(process.ExitCode == 0, "Could not create preference junction: " + await stderr);
        }
        try
        {
            await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Write(Path.Combine(link, "nested/preferences.json"), settings)));
            Assert(!Directory.EnumerateFileSystemEntries(external).Any(), "Settings write followed a linked ancestor.");
        }
        finally { Assert(new DirectoryInfo(link).ResolveLinkTarget(true)?.FullName == external, "Preference fixture link changed before cleanup."); Directory.Delete(link); }
    }

    private static async Task CheckPreferenceCoalescing(string root)
    {
        var uiThread = Environment.CurrentManagedThreadId; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var writes = new List<string>(); var count = 0; var active = 0;
        var store = new StudioPreferenceStore(Path.Combine(root, "coalesced"), (path, document) =>
        {
            Assert(Environment.CurrentManagedThreadId != uiThread && Interlocked.Increment(ref active) == 1, "Preference writes ran on the dispatcher or overlapped.");
            try
            {
                if (Interlocked.Increment(ref count) == 1) { started.TrySetResult(); Assert(release.Wait(TimeSpan.FromSeconds(20)), "Preference writer timed out."); }
                StudioPreferences.Write(path, document); writes.Add(document["recent"]![0]!.GetValue<string>());
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var initial = new StudioPreferences([], ["first"], false).ToDocument(); var work = store.Queue(initial);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); initial["dark"] = true;
            for (var index = 0; index < 1000; index++)
            { var next = new StudioPreferences(["favorite"], ["latest-" + index], true).ToDocument(); Assert(ReferenceEquals(work, store.Queue(next)), "Preference updates spawned overlapping workers."); next["recent"]![0] = "changed caller"; }
            Assert(await Application.Current.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background), "Slow settings write blocked UI work.");
            release.Set(); var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(result.Error is null && writes.SequenceEqual(new[] { "first", "latest-999" }) && (await store.ReadAsync())!.Recent[0] == "latest-999", "Settings coalescing or immutable snapshot contract failed.");
        }
        finally { release.Set(); await store.FlushAsync(); }
    }

    private static async Task CheckPreferenceWindow(string root, bool malformed)
    {
        var directory = Path.Combine(root, malformed ? "window-malformed" : "window-valid"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json"); var previousTheme = StudioTheme.IsDark;
        if (malformed) File.WriteAllText(path, "preserve damaged private preferences");
        else StudioPreferences.Write(path, new StudioPreferences(["saved-favorite"], ["saved-recent"], true).ToDocument());
        var damaged = File.ReadAllBytes(path); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var hold = false;
        var store = new StudioPreferenceStore(directory, (destination, settings) =>
        { if (Volatile.Read(ref hold)) { started.TrySetResult(); Assert(release.Wait(TimeSpan.FromSeconds(20)), "Window preferences timed out."); } StudioPreferences.Write(destination, settings); });
        var recoveryPath = Path.Combine(directory, "recovery.json"); var recovery = new StudioSnapshotWriter(project => StudioProjectStore.WriteRecoveryTo(recoveryPath, project));
        var appRoot = ProjectRoot(); var window = new StudioWindow(true, new PythonServiceClient(appRoot, null), new PythonServiceClient(appRoot, null), recovery: recovery, preferences: store);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await window.InitializeAsync(); await store.FlushAsync();
            Assert(!window.IsVisible && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && ((Button)window.FindName("StartupRetryButton")).Visibility == Visibility.Collapsed, "Real preference path failed startup or opened a window.");
            if (malformed)
            {
                Assert(File.ReadAllBytes(path).SequenceEqual(damaged), "Startup overwrote malformed settings.");
                window.Close(); var closeWork = window.PendingCloseRecovery!; Assert(!await closeWork, "Failed preferences allowed close without consent.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert(!closed.Task.IsCompleted && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && File.ReadAllBytes(path).SequenceEqual(damaged), "Failed preference close damaged settings or disabled editing.");
                Assert(File.Exists(recoveryPath), "Preference failure prevented the independent court recovery write.");
                File.Move(path, path + ".preserved"); window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(File.ReadAllBytes(path + ".preserved").SequenceEqual(damaged) && StudioPreferences.Read(path).Recent.Length > 0, "Preference repair/retry failed or lost malformed data.");
            }
            else
            {
                Assert(StudioTheme.IsDark && StudioPreferences.Read(path).Favorites.Contains("saved-favorite"), "Startup ignored saved settings.");
                var favorites = (HashSet<string>)typeof(StudioWindow).GetField("_favorites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                favorites.Add(window.Floors[1].Id); Volatile.Write(ref hold, true);
                await window.SelectFloorAsync(window.Floors[1]); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                ((Button)window.FindName("ThemeToolbarButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.SelectFloorAsync(window.Floors[2]);
                Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && !store.FlushAsync().IsCompleted, "Background settings disabled court editing.");
                window.Close(); var closeWork = window.PendingCloseRecovery!;
                Assert(!closeWork.IsCompleted && !closed.Task.IsCompleted && window.RecoveryClosePending, "Close did not await settings and recovery.");
                var revision = store.LatestRevision; window.Close(); Assert(store.LatestRevision == revision, "Repeated close queued duplicate settings.");
                release.Set(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var settings = StudioPreferences.Read(path);
                Assert(await closeWork && !settings.Dark && settings.Favorites.Contains(window.Floors[1].Id) && settings.Recent[0] == window.Floors[2].Id, "Production floor/theme/favorite persistence lost the final state.");
                var reopened = new StudioWindow(true, new PythonServiceClient(appRoot, null), new PythonServiceClient(appRoot, null), preferences: new StudioPreferenceStore(directory));
                var reopenedClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); reopened.Closed += (_, _) => reopenedClosed.TrySetResult();
                try { await reopened.InitializeAsync(); Assert(!StudioTheme.IsDark && ((HashSet<string>)typeof(StudioWindow).GetField("_favorites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!).Contains(window.Floors[1].Id), "Restart did not restore persisted theme/favorites."); }
                finally { reopened.Close(); await reopenedClosed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
        finally
        {
            release.Set(); Volatile.Write(ref hold, false);
            if (!closed.Task.IsCompleted)
            { if (window.PendingCloseRecovery is { } pending) await pending; if (File.Exists(path) && malformed) File.Move(path, path + ".cleanup-preserved"); window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            await store.FlushAsync(); await recovery.FlushAsync(); StudioTheme.Apply(previousTheme);
        }
    }
}
