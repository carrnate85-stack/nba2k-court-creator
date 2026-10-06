using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

public sealed class PythonServiceClient : IDisposable
{
    internal const int MaximumResponseCharacters = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly object _state = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ProcessStartInfo>? _startFactory;
    private readonly int _maximumResponseCharacters;
    private Process? _process;
    private BoundedWorkerLineReader? _responses;
    private CancellationTokenSource? _stderrCancellation;
    private Task? _stderrPump;
    private string _stderr = "";
    private int _revision;
    private volatile bool _disposed;
    public string ProjectRoot { get; }
    internal int? ActiveProcessId { get { lock (_state) return _process is { HasExited: false } ? _process.Id : null; } }
    internal string ErrorTail { get { lock (_state) return _stderr; } }
    public PythonServiceClient() : this(FindProjectRoot(), null) { }
    internal PythonServiceClient(string projectRoot, Func<ProcessStartInfo>? startFactory, int maximumResponseCharacters = MaximumResponseCharacters)
    {
        if (maximumResponseCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumResponseCharacters));
        ProjectRoot = Path.GetFullPath(projectRoot); _startFactory = startFactory; _maximumResponseCharacters = maximumResponseCharacters;
    }

    public async Task<JsonObject> RequestAsync(object[] args, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = timeout ?? TimeSpan.FromMinutes(2);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellation.CancelAfter(deadline);
        var acquired = false; Process? process = null;
        try
        {
            await _queue.WaitAsync(cancellation.Token).ConfigureAwait(false); acquired = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            process = EnsureProcess();
            BoundedWorkerLineReader reader; Task? stderrPump;
            lock (_state) { reader = _responses ?? throw new IOException("The Python engine stopped."); stderrPump = _stderrPump; }
            var id = ++_revision;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, args }).AsMemory(), cancellation.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return await Task.Run(() => ReadResponseAsync(reader, stderrPump, id, cancellation.Token), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (acquired) StopProcess(process);
            if (_disposed) throw new ObjectDisposedException(nameof(PythonServiceClient));
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The Python operation timed out. The engine will restart on the next request.");
        }
        catch (IOException) { if (acquired) StopProcess(process); if (_disposed) throw new ObjectDisposedException(nameof(PythonServiceClient)); throw; }
        catch (InvalidOperationException) when (_disposed) { throw new ObjectDisposedException(nameof(PythonServiceClient)); }
        finally { if (acquired) _queue.Release(); }
    }
    private async Task<JsonObject> ReadResponseAsync(BoundedWorkerLineReader reader, Task? stderrPump, int id, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            if (stderrPump is not null)
            {
                // A closed stdout does not prove the child has exited or closed stderr.
                try { await stderrPump.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            lock (_state) throw new IOException("The Python engine stopped. " + _stderr);
        }
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject response;
        try { response = JsonNode.Parse(line) as JsonObject ?? throw new IOException("Invalid engine response."); }
        catch (JsonException error) { throw new IOException("The Python engine returned invalid JSON.", error); }
        cancellationToken.ThrowIfCancellationRequested();
        if (response["id"] is not JsonValue value || !value.TryGetValue<int>(out var responseId) || responseId != id)
            throw new IOException("Engine response did not match the request.");
        if (response["error"] is JsonNode errorNode)
        {
            if (errorNode is not JsonValue errorValue || !errorValue.TryGetValue<string>(out var error)) throw new IOException("Invalid engine error response.");
            throw new InvalidOperationException(error);
        }
        return response["result"] as JsonObject ?? throw new IOException("The engine returned no result.");
    }
    public async Task<JsonObject> RequestFileAsync(string command, JsonObject request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = timeout ?? TimeSpan.FromMinutes(2);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellation.CancelAfter(deadline);
        var elapsed = Stopwatch.StartNew();
        StudioRequestFile? file = null;
        try
        {
            file = await StudioRequestFile.CreateAsync(request, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            var remaining = deadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The Python request timed out during preparation.");
            return await RequestAsync(command == "render" ? [command, "--request", file.Path] : [command, file.Path], remaining, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PythonServiceClient));
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The Python request timed out during preparation or execution.");
        }
        finally { if (file is not null) await Task.Run(file.Dispose).ConfigureAwait(false); }
    }
    private Process EnsureProcess()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false }) return _process;
            _stderrCancellation?.Cancel(); _stderrCancellation?.Dispose(); _stderrCancellation = null;
            _responses?.Dispose(); _process?.Dispose(); _process = null; _responses = null; _stderrPump = null; _stderr = "";
            var start = _startFactory?.Invoke() ?? CreateStartInfo();
            start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
            start.RedirectStandardError = true; start.CreateNoWindow = true;
            start.StandardOutputEncoding = new UTF8Encoding(false, true);
            start.StandardErrorEncoding = new UTF8Encoding(false, false);
            var process = new Process { StartInfo = start };
            try
            {
                process.Start(); _process = process;
                _responses = new BoundedWorkerLineReader(new StreamReader(process.StandardOutput.BaseStream,
                    new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true), _maximumResponseCharacters);
                _stderrCancellation = new CancellationTokenSource();
                var stderrToken = _stderrCancellation.Token;
                _stderrPump = Task.Run(() => DrainErrorAsync(process, stderrToken));
                return process;
            }
            catch { process.Dispose(); _process = null; throw; }
        }
    }
    private async Task DrainErrorAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var count = await process.StandardError.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return;
                lock (_state)
                {
                    if (!ReferenceEquals(_process, process)) return;
                    var text = _stderr + new string(buffer, 0, count);
                    _stderr = text[^Math.Min(4000, text.Length)..];
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { Trace.TraceInformation("Worker diagnostic stream closed: " + error.Message); }
    }
    private ProcessStartInfo CreateStartInfo()
    {
        var python = new[] { Path.Combine(ProjectRoot, "runtime", "python", "python.exe"),
            Path.Combine(ProjectRoot, "runtime", "python", "Scripts", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "python", "python.exe") }
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Python runtime not found. Run Setup Court Creator first.");
        var start = new ProcessStartInfo(python) { WorkingDirectory = ProjectRoot, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-B", "-u", "-m", "court_creator.service" }) start.ArgumentList.Add(arg);
        return start;
    }
    private void StopProcess(Process? expected = null)
    {
        Process? process; CancellationTokenSource? stderrCancellation; BoundedWorkerLineReader? responses;
        lock (_state)
        {
            if (expected is not null && !ReferenceEquals(_process, expected)) return;
            process = _process; _process = null; responses = _responses; _responses = null; _stderrPump = null;
            stderrCancellation = _stderrCancellation; _stderrCancellation = null;
        }
        stderrCancellation?.Cancel();
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { Trace.TraceWarning("Worker shutdown failed: " + error.Message); }
        finally { responses?.Dispose(); process?.Dispose(); stderrCancellation?.Dispose(); }
    }
    public void Dispose() { lock (_state) { if (_disposed) return; _disposed = true; } _lifetime.Cancel(); StopProcess(); }
    private static string FindProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("COURT_CREATOR_ROOT");
        if (configured is not null && File.Exists(Path.Combine(configured, "court_creator", "service.py"))) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "court_creator", "service.py"))) return directory.FullName;
        throw new DirectoryNotFoundException("Court Creator's Python backend could not be located.");
    }
}

internal sealed class BoundedWorkerLineReader(TextReader reader, int maximumCharacters) : IDisposable
{
    private readonly char[] _buffer = new char[8192];
    private int _position;
    private int _count;
    public void Dispose() => reader.Dispose();

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        try
        {
            while (true)
            {
                if (_position == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0)
                    {
                        if (line.Length != 0) throw new IOException("The Python engine stopped during a response.");
                        return null;
                    }
                }
                var newline = Array.IndexOf(_buffer, '\n', _position, _count - _position);
                var length = (newline < 0 ? _count : newline) - _position;
                if (length > maximumCharacters - line.Length)
                    throw new IOException("The Python engine response exceeds the permitted size. The engine will restart on the next request.");
                line.Append(_buffer, _position, length);
                _position += length;
                if (newline < 0) continue;
                _position++;
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
        }
        catch (DecoderFallbackException error)
        { throw new IOException("The Python engine returned invalid UTF-8.", error); }
    }
}
