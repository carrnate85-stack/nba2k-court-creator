using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NBA2KCourtCreator.Studio;

internal interface IPortableFloorPreparation : IDisposable
{
    Task<(int Code, string Error)> RunAsync(string root, string? game, Func<string, Task> progress);
}

internal sealed class PortableFloorStartup : IPortableFloorPreparation
{
    Process? process;
    readonly CancellationTokenSource lifetime = new();

    internal static bool HasCompleteCatalog(string root)
    {
        try
        {
            var index = Path.Combine(root, "assets/court_floor_templates/nba2k27/nba2k27_floor_templates.json");
            if (!File.Exists(Path.Combine(root, "data/generated/experimental-stock-lines.json")) || new FileInfo(index).Length > 4 * 1024 * 1024) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(index));
            var data = document.RootElement;
            if (data.TryGetProperty("preparationComplete", out var complete) && complete.ValueKind != JsonValueKind.True) return false;
            var templates = data.GetProperty("templates");
            if (templates.ValueKind != JsonValueKind.Array || templates.GetArrayLength() == 0) return false;
            var assetRoot = Path.GetFullPath(Path.Combine(root, "assets")) + Path.DirectorySeparatorChar;
            foreach (var item in templates.EnumerateArray())
                foreach (var key in new[] { "path", "thumbnailPath" })
                {
                    var path = Path.GetFullPath(Path.Combine(assetRoot, item.GetProperty(key).GetString()!));
                    if (!path.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }

    public async Task<(int Code, string Error)> RunAsync(string root, string? game, Func<string, Task> progress)
    {
        var start = new ProcessStartInfo(Path.Combine(root, "runtime/python/python.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-I", "-B", "-u", "tools/prepare_portable.py" }) start.ArgumentList.Add(argument);
        if (game is not null) { start.ArgumentList.Add("--game-root"); start.ArgumentList.Add(game); }
        using var child = Process.Start(start) ?? throw new IOException("The floor preparation runtime could not start.");
        process = child;
        try
        {
            var errors = child.StandardError.ReadToEndAsync(lifetime.Token);
            while (await child.StandardOutput.ReadLineAsync(lifetime.Token) is { } line) await progress(line);
            await child.WaitForExitAsync(lifetime.Token);
            var error = await errors;
            return (child.ExitCode, error.Length > 2000 ? error[^2000..] : error);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (ReferenceEquals(process, child)) process = null;
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
