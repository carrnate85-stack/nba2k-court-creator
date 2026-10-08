using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    sealed class ControlledFloorPreparation : IPortableFloorPreparation
    {
        internal readonly TaskCompletionSource FirstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<(int Code, string Error)> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public async Task<(int Code, string Error)> RunAsync(string root, string? game, Func<string, Task> progress)
        {
            await progress("FIRST_FLOOR_READY"); FirstReady.TrySetResult();
            return await Finish.Task;
        }
        public void Dispose() { Disposed = true; Finish.TrySetCanceled(); }
    }

    static async Task CheckPortableStartup(string output)
    {
        Directory.CreateDirectory(output);
        using var locator = new PythonServiceClient();
        var originalRoot = locator.ProjectRoot;
        var fixture = Path.Combine(Path.GetFullPath(output), "partial-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        foreach (var folder in new[] { "court_creator", "tools" })
            foreach (var source in Directory.EnumerateFiles(Path.Combine(originalRoot, folder), "*.py", SearchOption.TopDirectoryOnly))
            { var destination = Path.Combine(fixture, folder, Path.GetFileName(source)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
        Directory.CreateDirectory(Path.Combine(fixture, "data/generated"));
        File.Copy(Path.Combine(originalRoot, "data/generated/experimental-stock-lines.json"), Path.Combine(fixture, "data/generated/experimental-stock-lines.json"));
        File.Copy(Path.Combine(originalRoot, "data/team_palettes.json"), Path.Combine(fixture, "data/team_palettes.json"));
        var catalogName = "assets/court_floor_templates/nba2k27/nba2k27_floor_templates.json";
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(originalRoot, catalogName)))!.AsObject();
        foreach (var item in catalog["templates"]!.AsArray().OfType<JsonObject>())
            foreach (var key in new[] { "path", "thumbnailPath" }) item[key] = Path.Combine(originalRoot, "assets", item[key]!.GetValue<string>());
        var partial = (JsonObject)catalog.DeepClone(); partial["preparationComplete"] = false;
        partial["templates"] = new JsonArray(catalog["templates"]![0]!.DeepClone());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture, catalogName))!);
        File.WriteAllText(Path.Combine(fixture, catalogName), partial.ToJsonString());
        ProcessStartInfo Worker()
        {
            var start = new ProcessStartInfo(Path.Combine(originalRoot, "runtime/python/python.exe"))
            { WorkingDirectory = fixture, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-B", "-u", "-m", "court_creator.service" }) start.ArgumentList.Add(argument);
            return start;
        }
        var window = new StudioWindow(true, new PythonServiceClient(fixture, Worker), new PythonServiceClient(fixture, Worker)); var preparation = new ControlledFloorPreparation();
        var timer = Stopwatch.StartNew();
        try
        {
            var pending = window.PrepareFloorsInBackgroundAsync(preparation);
            await preparation.FirstReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var firstFloorMilliseconds = timer.Elapsed.TotalMilliseconds;
            Assert(!pending.IsCompleted && window.Floors.Count == 1, "The first floor must initialize before the remaining preparation completes.");
            window.SetLayerSettings("stock-outside", color: "#345678");
            var edited = window.CreateProject(); var originalVisibility = edited["visibility"]!.AsObject().DeepClone().AsObject();
            edited.Remove("visibility"); var current = edited.ToJsonString();
            catalog["preparationComplete"] = true;
            File.WriteAllText(Path.Combine(fixture, catalogName), catalog.ToJsonString());
            preparation.Finish.SetResult((0, "")); await pending;
            Assert(window.Floors.Count == catalog["templates"]!.AsArray().Count, "The completed background catalog did not become available.");
            var refreshed = window.CreateProject(); var refreshedVisibility = refreshed["visibility"]!.AsObject();
            Assert(originalVisibility.All(item => JsonNode.DeepEquals(item.Value, refreshedVisibility[item.Key])), "The background refresh changed existing layer visibility.");
            refreshed.Remove("visibility");
            Assert(current == refreshed.ToJsonString(), "The background catalog refresh replaced the user's court edits.");
            await window.UndoAsync();
            Assert(window.CreateProject()["outsideColor"]!.GetValue<string>() != "#345678", "The background refresh discarded undo history.");
            Assert(preparation.Disposed, "The preparation worker was not disposed.");
            Assert(window.FindName("FloorPreparationStatus") is FrameworkElement { Visibility: Visibility.Collapsed }, "Completed preparation status did not collapse.");
            var cached = PortableFloorStartup.HasCompleteCatalog(originalRoot);
            Assert(cached, "The real complete catalog was not recognized by the Python-free warm-start check.");
            File.WriteAllText(Path.Combine(output, "startup.json"), JsonSerializer.Serialize(new { firstFloorMilliseconds, floors = window.Floors.Count, nativeWindowsOpened = false }));
            Console.WriteLine($"PASS portable startup: first-floor initialization {firstFloorMilliseconds:F0} ms; app editing before preparation completes; refreshed catalog preserves court edits; complete cache detected without Python; no native windows opened.");
        }
        finally { preparation.Dispose(); window.Close(); }

        var failingWindow = new StudioWindow(true); var failing = new ControlledFloorPreparation();
        try
        {
            var pending = failingWindow.PrepareFloorsInBackgroundAsync(failing);
            await failing.FirstReady.Task;
            var current = failingWindow.CreateProject().ToJsonString();
            failing.Finish.SetResult((1, "Injected floor failure")); await pending;
            Assert(current == failingWindow.CreateProject().ToJsonString(), "A failed background preparation discarded the current court.");
            Assert(failingWindow.FindName("FloorPreparationRetry") is FrameworkElement { Visibility: Visibility.Visible }, "Preparation failure did not expose Retry.");
            Console.WriteLine("PASS preparation failure retains the current court and offers Retry.");
        }
        finally { failing.Dispose(); failingWindow.Close(); }

        var closingWindow = new StudioWindow(true); var closing = new ControlledFloorPreparation();
        var closingTask = closingWindow.PrepareFloorsInBackgroundAsync(closing);
        await closing.FirstReady.Task; closingWindow.Close(); await closingTask;
        Assert(closing.Disposed, "Closing the app left its preparation worker running.");
        Console.WriteLine("PASS closing the app stops unfinished floor preparation.");
    }
}
