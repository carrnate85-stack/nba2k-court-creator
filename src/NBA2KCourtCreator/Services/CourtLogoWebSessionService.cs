using NBA2KCourtCreator.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NBA2KCourtCreator.Services;

public sealed class CourtLogoWebSessionService : IDisposable
{
    private readonly string _projectRoot;
    private Process? _process;

    public CourtLogoWebSessionService(string projectRoot) => _projectRoot = projectRoot;

    public string? Url { get; private set; }
    public string? StatePath { get; private set; }

    public async Task<string> StartAsync(
        string courtPreviewPath,
        int courtWidth,
        int courtHeight,
        IEnumerable<CourtLogo> logos,
        CancellationToken cancellationToken = default)
    {
        Stop();
        var sessionFolder = Path.Combine(Path.GetTempPath(), "nba2k_court_creator", "logo_web", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        StatePath = Path.Combine(sessionFolder, "state.json");

        var state = new
        {
            revision = 0,
            returnRequested = false,
            project = new
            {
                width = courtWidth,
                height = courtHeight,
                backgroundPath = courtPreviewPath,
                projectRoot = _projectRoot,
                selectedId = logos.FirstOrDefault()?.Id,
                items = logos.Select(logo => new
                {
                    id = logo.Id,
                    name = logo.Name,
                    path = logo.Path,
                    visible = logo.Visible,
                    x = logo.X,
                    y = logo.Y,
                    width = logo.Width,
                    height = logo.Height,
                    rotation = logo.Rotation,
                    opacity = logo.Opacity,
                    flipX = logo.FlipX,
                    flipY = logo.FlipY,
                    scaleLocked = logo.ScaleLocked,
                }).ToList(),
            },
        };
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindPython(),
                WorkingDirectory = _projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(Path.Combine(_projectRoot, "tools", "court_logo_web.py"));
        process.StartInfo.ArgumentList.Add("--state");
        process.StartInfo.ArgumentList.Add(StatePath);
        process.Start();
        _process = process;
        _ = DrainErrorsAsync(process);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("The logo editor did not start.");
        using var startup = JsonDocument.Parse(line);
        Url = startup.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidDataException("The logo editor did not provide an address.");
        Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        return Url;
    }

    public JsonDocument? ReadState()
    {
        if (string.IsNullOrWhiteSpace(StatePath) || !File.Exists(StatePath)) return null;
        try
        {
            return JsonDocument.Parse(File.ReadAllText(StatePath));
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(true);
        }
        catch { }
        _process.Dispose();
        _process = null;
        Url = null;
        StatePath = null;
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        while (!process.HasExited && await process.StandardError.ReadLineAsync() is { } line)
        {
            Debug.WriteLine($"Court logo editor: {line}");
        }
    }

    private static string FindPython()
    {
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "codex-runtimes",
            "codex-primary-runtime",
            "dependencies",
            "python",
            "python.exe");
        return File.Exists(bundled) ? bundled : "python";
    }

    public void Dispose() => Stop();
}
