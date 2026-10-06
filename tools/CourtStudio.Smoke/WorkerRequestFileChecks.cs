using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckWorkerRequestFiles(string output)
    {
        var scratch = Path.Combine(Path.GetFullPath(output), "requests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var request = new JsonObject { ["name"] = "Caf\u00e9", ["value"] = new string('x', 150000) };
            using (var owned = await StudioRequestFile.CreateAsync(request, default, scratch))
            {
                using (var reader = new FileStream(owned.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    Assert(JsonNode.Parse(reader)!["name"]!.GetValue<string>() == "Caf\u00e9", "Unicode request did not round trip.");
                try { using var writer = File.Open(owned.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new InvalidOperationException("Active request allowed writing."); }
                catch (IOException) { }
                owned.Dispose(); Assert(!File.Exists(owned.Path), "Ordinary request was not cleaned up.");
                File.WriteAllText(owned.Path, "later owner"); owned.Dispose();
                Assert(File.ReadAllText(owned.Path) == "later owner", "Repeated request cleanup deleted a later file.");
                File.Delete(owned.Path);
            }
            foreach (var mode in new[] { "edited", "replaced" })
            {
                using var owned = await StudioRequestFile.CreateAsync(new JsonObject { ["value"] = "original" }, default, scratch);
                ((FileStream)typeof(StudioRequestFile).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!).Dispose();
                var original = File.ReadAllBytes(owned.Path); var displaced = owned.Path + ".original";
                if (mode == "edited")
                {
                    var stamp = File.GetLastWriteTimeUtc(owned.Path); var edited = original.ToArray(); edited[^2] ^= 1;
                    File.WriteAllBytes(owned.Path, edited); File.SetLastWriteTimeUtc(owned.Path, stamp);
                }
                else { File.Move(owned.Path, displaced); File.WriteAllBytes(owned.Path, original); }
                var expected = File.ReadAllBytes(owned.Path); owned.Dispose();
                Assert(File.Exists(owned.Path) && File.ReadAllBytes(owned.Path).SequenceEqual(expected), "Request cleanup deleted " + mode + " contents.");
                File.Delete(owned.Path); if (File.Exists(displaced)) File.Delete(displaced);
            }
            using (var canceled = new CancellationTokenSource())
            {
                string? partial = null;
                try
                {
                    using var owned = await StudioRequestFile.CreateAsync(request, canceled.Token, scratch, (path, _) => { partial = path; canceled.Cancel(); });
                    throw new InvalidOperationException("Canceled request preparation succeeded.");
                }
                catch (OperationCanceledException) { }
                Assert(partial is not null && !File.Exists(partial), "Canceled partial request was left behind.");
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { using var owned = await StudioRequestFile.CreateAsync(request, canceled.Token, scratch); throw new InvalidOperationException("Pre-canceled request succeeded."); }
                catch (OperationCanceledException) { }
            }
            var oversized = new JsonObject { ["value"] = new string('x', StudioRequestFile.MaximumBytes) };
            try { using var owned = await StudioRequestFile.CreateAsync(oversized, default, scratch); throw new InvalidOperationException("Oversized request succeeded."); }
            catch (InvalidDataException) { }
            JsonObject nested = new(); for (var level = 0; level < 40; level++) nested = new JsonObject { ["nested"] = nested };
            try { using var owned = await StudioRequestFile.CreateAsync(nested, default, scratch); throw new InvalidOperationException("Deep request succeeded."); }
            catch (InvalidDataException) { }
            Assert(!Directory.EnumerateFileSystemEntries(scratch).Any(), "Request preparation left unexpected files.");
        }
        finally { Directory.Delete(scratch); }
        var root = ProjectRoot();
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(Path.Combine(root, "runtime/python/python.exe")) { WorkingDirectory = root };
            foreach (var argument in new[] { "-B", "-u", Path.Combine(root, "tests/worker_fault_fixture.py") }) start.ArgumentList.Add(argument);
            return start;
        }
        using var client = new PythonServiceClient(root, Start);
        var normal = await client.RequestFileAsync("render", new JsonObject { ["value"] = "ordinary" });
        Assert(!File.Exists(normal["requestPath"]!.GetValue<string>()), "Completed worker request was not removed.");
        try { await client.RequestFileAsync("render", new JsonObject(), TimeSpan.Zero); throw new InvalidOperationException("Invalid request deadline succeeded."); }
        catch (ArgumentOutOfRangeException) { }
        var result = await client.RequestFileAsync("render", new JsonObject { ["fixture"] = "share-request" });
        var path = result["requestPath"]!.GetValue<string>(); var alias = result["aliasPath"]!.GetValue<string>();
        var temporaryRoot = StudioFileSafety.ResolvePath(Path.GetTempPath());
        Assert(StudioFileSafety.ResolvePath(Path.GetDirectoryName(path)!) == temporaryRoot && Path.GetFileName(path).StartsWith("court-studio-") && alias == path + ".shared", "Fixture worker returned an unexpected cleanup path.");
        try
        {
            Assert(File.Exists(path) && File.Exists(alias), "Request cleanup deleted a file that became shared after the worker used it.");
            Assert(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(alias)), "Shared request bytes were changed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(alias)) File.Delete(alias); }
        client.Dispose();
        try { await client.RequestFileAsync("render", new JsonObject()); throw new InvalidOperationException("Disposed request client accepted preparation."); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("PASS worker request files: guarded reads, bounded JSON, cancellation cleanup, changed/shared file preservation and disposed clients. No native windows opened.");
    }
}
