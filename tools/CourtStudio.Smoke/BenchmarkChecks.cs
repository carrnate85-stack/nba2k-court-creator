using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task Benchmark(string output)
    {
        var construction = Stopwatch.StartNew();
        var window = new StudioWindow(true);
        construction.Stop();
        try
        {
            var beforeStartupReads=StudioImages.ReadStatistics;
            var startup = Stopwatch.StartNew(); await window.InitializeAsync(); startup.Stop();
            var startupReads=StudioImages.ReadStatistics;
            Layout(window, 1440, 900);
            var firstSelections = new List<double>(); var cachedSelections = new List<double>();
            var floorReads=new List<object>();
            foreach (var floor in window.Floors.Take(8))
            {
                var beforeReads=StudioImages.ReadStatistics;
                var timer = Stopwatch.StartNew(); await window.SelectFloorAsync(floor); timer.Stop(); firstSelections.Add(timer.Elapsed.TotalMilliseconds);
                var firstReads=StudioImages.ReadStatistics;
                timer.Restart(); await window.SelectFloorAsync(floor); timer.Stop(); cachedSelections.Add(timer.Elapsed.TotalMilliseconds);
                var cachedReads=StudioImages.ReadStatistics;
                floorReads.Add(new{floorId=floor.Id,sameSource=StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(floor.Path),Path.GetFullPath(floor.PreviewPath)),
                    firstSourceOpens=firstReads.SourceReads-beforeReads.SourceReads,firstHashedBytes=firstReads.HashedBytes-beforeReads.HashedBytes,
                    cachedSourceOpens=cachedReads.SourceReads-firstReads.SourceReads,cachedHashedBytes=cachedReads.HashedBytes-firstReads.HashedBytes});
            }
            var colors = Stopwatch.StartNew();
            var colorSamples = new List<double>();
            for (var index = 0; index < 100; index++)
            {
                var edit = Stopwatch.StartNew();
                window.SetLayerSettings("paint-left", color: index % 2 == 0 ? "#19583F" : "#336699");
                colorSamples.Add(edit.Elapsed.TotalMilliseconds);
            }
            var fieldFlush = Stopwatch.StartNew();
            typeof(StudioWindow).GetMethod("FlushLayerHex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            fieldFlush.Stop();
            colors.Stop();
            var export = Stopwatch.StartNew();
            var texture = Path.Combine(Path.GetTempPath(), "court-benchmark-" + Guid.NewGuid().ToString("N") + ".png");
            try { await window.ExportToAsync(texture, false); export.Stop(); }
            finally { File.Delete(texture); }
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var report = new
            {
                description = "Off-screen fresh-process native initialization. OS disk caches are not cleared; no recovery project is loaded. Not an Electron comparison or visible first-frame measurement.",
                measuredAtUtc = DateTime.UtcNow,
                constructionMs = construction.Elapsed.TotalMilliseconds,
                initializeMs = startup.Elapsed.TotalMilliseconds,
                initializeSourceOpens=startupReads.SourceReads-beforeStartupReads.SourceReads,
                initializeHashedBytes=startupReads.HashedBytes-beforeStartupReads.HashedBytes,
                floorImageReads=floorReads,
                imageReadScope="Successfully opened source files and completed full SHA passes only; excludes WIC decoder reads and Python I/O. Not physical-disk traffic or visible startup latency.",
                floorCount = window.Floors.Count,
                firstSelectionMs = firstSelections,
                cachedSelectionMs = cachedSelections,
                colorEditAverageMs = colors.Elapsed.TotalMilliseconds / 100,
                colorEditFirstSamplesMs = colorSamples.Take(5),
                colorEditMedianMs = colorSamples.Order().ElementAt(50),
                colorEditMaximumMs = colorSamples.Max(),
                colorEditCoalescedFieldFlushMs = fieldFlush.Elapsed.TotalMilliseconds,
                colorEditScope = "100 programmatic edits plus one final synthetic field frame; not actual pointer/frame latency.",
                fullResolutionPngMs = export.Elapsed.TotalMilliseconds,
                nativeProcessWorkingSetMiB = process.WorkingSet64 / 1048576.0,
                managedAllocatedMiB = GC.GetTotalMemory(false) / 1048576.0,
                memoryScope = "Smoke harness/native process only; excludes Python workers and includes benchmark undo history."
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(output, json); Console.WriteLine(json);
        }
        finally { window.Close(); }
    }
}
