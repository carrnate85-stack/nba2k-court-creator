using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static string ProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("COURT_CREATOR_ROOT"); if (configured is not null) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "court_creator/service.py"))) return directory.FullName;
        throw new DirectoryNotFoundException("Project root not found.");
    }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task CheckWorkerRecovery()
    {
        var root = ProjectRoot();
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(Path.Combine(root, "runtime/python/python.exe")) { WorkingDirectory = root };
            start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(root, "tests/worker_fault_fixture.py")); return start;
        }
        await CheckBoundedWorkerReader();
        using var client = new PythonServiceClient(root, Start, maximumResponseCharacters: 4096);
        foreach (var mode in new[] { "invalid-json", "wrong-id", "invalid-result", "invalid-error", "exit", "output-overflow", "invalid-utf8", "wrong-encoding", "unterminated", "excessive-depth", "closed-stdout" })
        {
            await Expect<IOException>(() => client.RequestAsync([mode], TimeSpan.FromSeconds(5)));
            Assert(client.ActiveProcessId is null, "Broken protocol retained its worker.");
            Assert((await client.RequestAsync(["echo"]))["args"]![0]!.GetValue<string>() == "echo", "Worker did not restart after " + mode);
        }
        Assert((await client.RequestAsync(["unicode"]))["name"]!.GetValue<string>() == "Caf\u00e9 \U0001f3c0", "UTF-8 worker response was damaged.");
        var floodWorker = client.ActiveProcessId;
        await client.RequestAsync(["stderr-flood-echo"], TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 500 && !client.ErrorTail.EndsWith("tail marker", StringComparison.Ordinal); attempt++) await Task.Delay(10);
        Assert(client.ErrorTail.Length == 4000 && client.ErrorTail.EndsWith("tail marker", StringComparison.Ordinal), "Newline-free diagnostics were not drained into a bounded tail.");
        Assert(client.ActiveProcessId == floodWorker, "Diagnostic output unnecessarily restarted the worker.");
        try { await client.RequestAsync(["stderr-flood-exit"], TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Failed worker returned a result."); }
        catch (IOException error) { Assert(error.Message.Contains("tail marker", StringComparison.Ordinal) && error.Message.Length < 4100, "Worker failure lost or exceeded its diagnostic tail."); }
        Assert(client.ActiveProcessId is null, "Flooding failed worker was retained.");
        await client.RequestAsync(["echo"]);
        var before = client.ActiveProcessId;
        await Expect<InvalidOperationException>(() => client.RequestAsync(["business-error"]));
        Assert(client.ActiveProcessId == before, "A normal operation error unnecessarily restarted the worker.");
        await Expect<TimeoutException>(() => client.RequestAsync(["sleep", 2], TimeSpan.FromMilliseconds(100)));
        Assert(client.ActiveProcessId is null, "Timed-out request retained its worker.");
        await client.RequestAsync(["echo"]);
        var running = client.RequestAsync(["sleep", .25]);
        var live = client.ActiveProcessId;
        await Expect<TimeoutException>(() => client.RequestAsync(["echo"], TimeSpan.FromMilliseconds(25)));
        Assert(client.ActiveProcessId == live, "A queued timeout killed a different active request."); await running;
        using (var cancellation = new CancellationTokenSource(40))
            await Expect<OperationCanceledException>(() => client.RequestAsync(["sleep", 2], cancellationToken: cancellation.Token));
        var fileResult = await client.RequestFileAsync("render", new JsonObject { ["test"] = true });
        Assert(!File.Exists(fileResult["requestPath"]!.GetValue<string>()), "Temporary request file was not cleaned up.");
        var pending = client.RequestAsync(["sleep", 2]); var child = client.ActiveProcessId;
        client.Dispose(); await Expect<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await Expect<ObjectDisposedException>(() => client.RequestAsync(["echo"]));
        if (child is not null)
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { using var process = Process.GetProcessById(child.Value); if (process.HasExited) break; if (attempt == 99) throw new InvalidOperationException("Disposed worker is still running."); }
                catch (ArgumentException) { break; }
                await Task.Delay(10);
            }
        Console.WriteLine("PASS worker faults: bounded fragmented/CRLF/UTF-8 responses; overflow, incomplete framing, invalid UTF-8/JSON/depth/IDs/results/errors; newline-free diagnostic floods and retained failure tail; exit/retry, normal errors, active/queued deadlines, cancellation/disposal, request-file cleanup.");
    }
    private static async Task CheckBoundedWorkerReader()
    {
        using var source = new FragmentedWorkerReader("one\r\ntwo\n\n", 2);
        var reader = new BoundedWorkerLineReader(source, 8);
        Assert(await reader.ReadLineAsync(default) == "one" && await reader.ReadLineAsync(default) == "two"
               && await reader.ReadLineAsync(default) == "" && await reader.ReadLineAsync(default) is null,
               "Buffered response fragments or CRLF frames were lost.");
        using var boundarySource = new FragmentedWorkerReader("abcd\n", 1);
        Assert(await new BoundedWorkerLineReader(boundarySource, 4).ReadLineAsync(default) == "abcd", "Exact-limit response was rejected.");
        using var excessSource = new FragmentedWorkerReader(new string('x', 100000), 3);
        await Expect<IOException>(() => new BoundedWorkerLineReader(excessSource, 32).ReadLineAsync(default));
        Assert(excessSource.CharactersRead == 33, "Overflow was detected only after reading the entire response.");
        using var incompleteSource = new StringReader("{\"id\":1}");
        await Expect<IOException>(() => new BoundedWorkerLineReader(incompleteSource, 64).ReadLineAsync(default));
        using var canceledSource = new FragmentedWorkerReader("valid\n", 1);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Expect<OperationCanceledException>(() => new BoundedWorkerLineReader(canceledSource, 64).ReadLineAsync(canceled.Token));
        using var bytes = new MemoryStream([0xff, 0x0a]);
        using var invalidUtf8 = new StreamReader(bytes, new UTF8Encoding(false, true));
        await Expect<IOException>(() => new BoundedWorkerLineReader(invalidUtf8, 64).ReadLineAsync(default));
    }
    private sealed class FragmentedWorkerReader(string text, int fragmentSize) : StringReader(text)
    {
        public int CharactersRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer[..Math.Min(fragmentSize, buffer.Length)], cancellationToken);
            CharactersRead += count; return count;
        }
    }
    private static void CheckBitmapCache()
    {
        var cache = new StudioBitmapCache(32, 2); var decodes = 0;
        BitmapSource Decode() { Interlocked.Increment(ref decodes); var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[8], 8); image.Freeze(); return image; }
        cache.Get("A", Decode); cache.Get("B", Decode); cache.Get("A", Decode); cache.Get("C", Decode); cache.Get("A", Decode);
        Assert(decodes == 3 && cache.Statistics.Bytes == 32, "Cache did not retain the recently used image within budget.");
        cache.Get("B", Decode); Assert(decodes == 4, "Least recently used image was not evicted.");
        var concurrent = new StudioBitmapCache(32, 2); var calls = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => concurrent.Get("shared", () => { Interlocked.Increment(ref calls); Thread.Sleep(40); return Decode(); }))).ToArray();
        Task.WaitAll(tasks); Assert(calls == 1 && tasks.All(task => ReferenceEquals(task.Result, tasks[0].Result)), "Concurrent cache misses decoded multiple images.");
        try { cache.Get("failed", () => throw new IOException("fixture")); } catch (IOException) { }
        cache.Get("failed", Decode); Assert(cache.Statistics.Bytes <= cache.Statistics.Budget, "Retry exceeded the image cache budget.");
        Console.WriteLine("PASS image cache: byte budget, LRU eviction, shared concurrent decode, retry after failure.");
    }
    private static async Task CheckProjectSafety(StudioWindow window, string output)
    {
        var snapshot = window.CreateProject(); var original = snapshot.ToJsonString();
        var mutableInput = (JsonObject)snapshot.DeepClone();
        var restoringSnapshot = window.RestoreProjectAsync(mutableInput);
        mutableInput["outsideColor"] = "#112233";
        await restoringSnapshot;
        Assert(window.CreateProject().ToJsonString() == original, "Project input changed the document after validation while asset reads were running.");
        foreach (var invalid in new Action<JsonObject>[] {
            project => project["version"] = "two",
            project => project["outsideColor"] = "#GGGGGG",
            project => project["lineSettings"]!["college-three"]!["visible"] = "yes",
            project => project["logoImages"] = new JsonArray(new JsonObject { ["width"] = -1 }),
            project => project["logoImages"] = new JsonArray(new JsonObject { ["id"] = "same" }, new JsonObject { ["id"] = "same" }) })
        {
            var project = (JsonObject)snapshot.DeepClone(); invalid(project);
            await Expect<InvalidDataException>(() => window.RestoreProjectAsync(project));
            Assert(window.CreateProject().ToJsonString() == original, "Malformed project changed the open court.");
        }
        var badTexture = Path.Combine(output, "not-a-floor.png"); await File.WriteAllTextAsync(badTexture, "invalid image");
        var badAssets = (JsonObject)snapshot.DeepClone(); badAssets["outsideColor"] = "#112233";
        badAssets["floor"] = new JsonObject { ["id"] = "bad-floor", ["name"] = "Invalid floor", ["path"] = Path.GetFullPath(badTexture) };
        await Expect<NotSupportedException>(() => window.RestoreProjectAsync(badAssets));
        Assert(window.CreateProject().ToJsonString() == original, "Failed floor decode partially changed the open court.");
        Assert(((System.Windows.FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled, "Failed restore left the workspace disabled.");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var revisionField = typeof(StudioWindow).GetField("_geometryRevision", flags)!;
        var geometryRevision = revisionField.GetValue(window);
        var protectedOutput = Path.Combine(output, "geometry-mismatch-export.png");
        await File.WriteAllTextAsync(protectedOutput, "previous valid export");
        try
        {
            revisionField.SetValue(window, new string('0', 64));
            foreach (var iff in new[] { false, true })
            {
                try { await window.ExportToAsync(protectedOutput, iff); throw new Exception("Geometry mismatch was accepted."); }
                catch (InvalidOperationException error) { Assert(error.Message.Contains("geometry changed while", StringComparison.Ordinal), "Export rejected the request for a reason other than mismatched geometry."); }
                Assert(File.ReadAllText(protectedOutput) == "previous valid export", "Geometry mismatch overwrote an existing output.");
                Assert(window.CreateProject().ToJsonString() == original, "Geometry mismatch changed the open project.");
                Assert(((System.Windows.Controls.Button)window.FindName("BuildIffButton")).IsEnabled, "Rejected geometry left export controls disabled.");
            }
        }
        finally { revisionField.SetValue(window, geometryRevision); File.Delete(protectedOutput); }
        var undo = (List<JsonObject>)typeof(StudioWindow).GetField("_undo", flags)!.GetValue(window)!;
        var redo = (List<JsonObject>)typeof(StudioWindow).GetField("_redo", flags)!.GetValue(window)!;
        window.SetLayerSettings("stock-outside", color: "#112233");
        var validUndo = undo[^1]; undo[^1] = badAssets;
        var historyCount = undo.Count; var redoCount = redo.Count; var changed = window.CreateProject().ToJsonString();
        await Expect<NotSupportedException>(() => window.UndoAsync());
        Assert(undo.Count == historyCount && redo.Count == redoCount && window.CreateProject().ToJsonString() == changed, "Failed undo discarded history or changed the document.");
        undo[^1] = validUndo; await window.UndoAsync();
        Assert(window.CreateProject().ToJsonString() == original, "Undo did not recover after a failed restore.");
        var exchangeTask = window.PrepareCanvasExchangeAsync(Path.Combine(output, "canvas-exchange"));
        window.SetLayerSettings("stock-outside", color: "#334455");
        var exchange = await exchangeTask;
        Assert(StudioProjectStore.Read(exchange.ProjectPath).ToJsonString() == original, "Canvas handoff saved a later document instead of the rendered snapshot.");
        var exchangedImage = StudioImages.Load(exchange.TexturePath);
        var outsidePixel = new byte[4]; exchangedImage.CopyPixels(new System.Windows.Int32Rect(80, 80, 1, 1), outsidePixel, 4, 0);
        var expectedColor = (Color)ColorConverter.ConvertFromString(snapshot["outsideColor"]!.GetValue<string>());
        Assert(exchangedImage.PixelWidth == 8192 && exchangedImage.PixelHeight == 4096 && outsidePixel[2] == expectedColor.R && outsidePixel[1] == expectedColor.G && outsidePixel[0] == expectedColor.B, "Canvas handoff texture did not preserve its original full-resolution color snapshot.");
        await window.UndoAsync();
        File.Delete(exchange.TexturePath); File.Delete(exchange.ProjectPath);
        var recovery = Path.Combine(output, "recovery-fixture.json");
        StudioProjectStore.Write(recovery, snapshot, keepBackup: true);
        var newer = (JsonObject)snapshot.DeepClone(); newer["projectName"] = "Newer recovery"; StudioProjectStore.Write(recovery, newer, keepBackup: true);
        await File.WriteAllTextAsync(recovery, "{broken");
        Assert(StudioProjectStore.Recovery([recovery, recovery + ".bak"])!.ToJsonString() == original, "Corrupt recovery did not fall back to the last valid backup.");
        StudioProjectStore.Write(recovery, newer, keepBackup: true);
        Assert(StudioProjectStore.Read(recovery + ".bak").ToJsonString() == original, "Corrupt recovery overwrote a valid backup.");
        var oversized = (JsonObject)snapshot.DeepClone(); oversized["oversizedFixture"] = new string('x', StudioProjectStore.MaximumJsonBytes);
        var beforeOversizedSave = File.ReadAllText(recovery);
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Write(recovery, oversized, keepBackup: true)));
        Assert(File.ReadAllText(recovery) == beforeOversizedSave, "Oversized save replaced a valid project with a file the app cannot open.");
        foreach (var invalidJson in new[] { "[]", "{\"version\":\"two\"}", "{\"version\":2,\"buildMode\":\"game-uv\",\"logoImages\":{}}" })
        { await File.WriteAllTextAsync(badTexture, invalidJson); await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Read(badTexture))); }
        File.Delete(badTexture);
        Console.WriteLine("PASS projects: type/color/range/ID validation, failed asset load leaves document intact, failed undo preserves history, Canvas texture/project share one snapshot despite later edits, recovery backup fallback and preservation.");
    }
    private static async Task CheckPreferences(string output)
    {
        var path = Path.Combine(output, "preferences-fixture.json");
        foreach (var invalid in new[] { "{broken", "[]", "{\"favorites\":{}}", "{\"recent\":[null]}", "{\"favorites\":[3]}", "{\"dark\":\"yes\"}" })
        {
            await File.WriteAllTextAsync(path, invalid);
            await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Read(path)));
        }
        var recent = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode?)JsonValue.Create("floor-" + i)).ToArray());
        await File.WriteAllTextAsync(path, new JsonObject { ["recent"] = recent, ["favorites"] = new JsonArray("a", "a", "b"), ["dark"] = true }.ToJsonString());
        var settings = StudioPreferences.Read(path);
        Assert(settings.Recent.Length == 20 && settings.Favorites.SequenceEqual(new[] { "a", "b" }) && settings.Dark, "Preferences did not bound recents, deduplicate favorites, or retain the theme.");
        File.Delete(path);
        Console.WriteLine("PASS preferences: malformed shapes/types rejected; bounded recents, distinct favorites, theme retained.");
    }
}
