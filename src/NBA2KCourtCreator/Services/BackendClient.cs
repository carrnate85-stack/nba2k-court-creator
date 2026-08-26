using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NBA2KCourtCreator.Services;

public sealed class BackendClient
{
    private readonly string _projectRoot;
    private readonly string _pythonExe;
    private readonly string _pythonPrefix;

    public BackendClient()
    {
        _projectRoot = FindProjectRoot();
        (_pythonExe, _pythonPrefix) = FindPython();
    }

    public string ProjectRoot => _projectRoot;

    public async Task<JsonDocument> LoadAsync(string? templatePath = null)
        => string.IsNullOrWhiteSpace(templatePath)
            ? await RunAsync("load")
            : await RunAsync("load", "--template", templatePath);

    public async Task<JsonDocument> RenderAsync(object request)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"nba2k-court-render-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        try
        {
            return await RunAsync("render", "--request", requestPath);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    public async Task<JsonDocument> SampleColorAsync(string layerId)
        => await RunAsync("sample-color", "--layer-id", layerId);

    public async Task<JsonDocument> AddFloorAsync(string sourcePath)
        => await RunAsync("add-floor", "--source", sourcePath);

    private async Task<JsonDocument> RunAsync(params string[] arguments)
    {
        var allArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_pythonPrefix))
        {
            allArgs.Add(_pythonPrefix);
        }
        allArgs.Add("-m");
        allArgs.Add("court_creator.backend");
        allArgs.AddRange(arguments);

        var start = new ProcessStartInfo
        {
            FileName = _pythonExe,
            WorkingDirectory = _projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in allArgs)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Python engine.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = TryReadError(output) ?? error.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "The Python engine failed." : message);
        }

        return JsonDocument.Parse(output);
    }

    private static string? TryReadError(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "court_creator", "backend.py")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static (string Exe, string Prefix) FindPython()
    {
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "codex-runtimes",
            "codex-primary-runtime",
            "dependencies",
            "python",
            "python.exe");
        if (File.Exists(bundled))
        {
            return (bundled, string.Empty);
        }

        return CommandExists("python") ? ("python", string.Empty) : ("py", "-3");
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            process?.WaitForExit(2500);
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
