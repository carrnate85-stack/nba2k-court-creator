using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Diagnostics;

namespace NBA2KCourtCreator.Studio;

public static class StudioProjectStore
{
    internal const int MaximumJsonBytes = 16 * 1024 * 1024;
    internal const int MaximumFileAccessAttempts = 4;
    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2K Studio", "Court Creator");
    public static string RecoveryPath => Path.Combine(SettingsDirectory, "recovery.json");
    internal static string LastGoodRecoveryPath(string path) => path + ".last-good.json";
    public static void WriteRecovery(JsonObject project) => WriteRecoveryTo(RecoveryPath, project);
    internal static void WriteRecoveryTo(string path, JsonObject project)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup) || Recovery([backup]) is not null)
        { Write(path, project, keepBackup: true); return; }
        var fallback = LastGoodRecoveryPath(path);
        var previous = Recovery([path, fallback]);
        if (previous is not null) Write(fallback, previous);
        Write(path, project);
    }
    public static JsonObject Read(string path)
    {
        JsonObject project;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (stream.Length > MaximumJsonBytes) throw new InvalidDataException("Court project exceeds the 16 MB size limit.");
            var data = new byte[(int)stream.Length]; stream.ReadExactly(data);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Court project changed while it was being read.");
            using var reader = new StreamReader(new MemoryStream(data, writable: false), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            project = JsonNode.Parse(reader.ReadToEnd(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("Not a Court Creator project.");
        }
        catch (JsonException error) { throw new InvalidDataException("Court project contains invalid JSON.", error); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Court project contains invalid text encoding.", error); }
        StudioProjectValidation.Validate(project);
        return project;
    }
    internal static bool ValidateWriteDestination(string path, bool keepBackup, IEnumerable<string>? protectedSources = null)
    {
        var sources = (protectedSources ?? []).ToArray();
        StudioFileSafety.ProtectSources(path, sources);
        if (keepBackup)
        {
            StudioFileSafety.ProtectSources(path + ".bak", sources);
            if (File.Exists(path + ".bak"))
            {
                try { Read(path + ".bak"); }
                catch (InvalidDataException error) { throw new InvalidDataException("The project backup path contains an unrecognized or damaged file. Choose a different project name to preserve it.", error); }
            }
        }
        if (!keepBackup || !File.Exists(path)) return false;
        try { Read(path); return true; } catch (InvalidDataException) { return false; }
    }
    public static void Write(string path, JsonObject project, bool keepBackup = false, IEnumerable<string>? protectedSources = null, Action? validateAssets = null)
    {
        project = (JsonObject)project.DeepClone();
        StudioProjectValidation.Validate(project);
        string json;
        try { json = project.ToJsonString(new JsonSerializerOptions { MaxDepth = 32 }); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new InvalidDataException("Court project contains unsupported nested data.", error); }
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new InvalidDataException("Court project exceeds the 16 MB size limit.");
        path = Path.GetFullPath(path);
        var sources = (protectedSources ?? []).ToArray();
        RetryFileAccess(() => ValidateWriteDestination(path, keepBackup, sources));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        StudioFileSafety.FileIdentity? identity = null;
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                identity = StudioFileSafety.StagingIdentity(stream.SafeFileHandle);
                using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(json); writer.Flush(); stream.Flush(true);
            }
            PublishProject(staged, path, keepBackup, sources, identity: identity, validateAssets: validateAssets);
        }
        finally
        {
            CleanupStaging(staged, identity);
        }
    }
    internal static void PublishProject(string staged, string path, bool keepBackup, IEnumerable<string> protectedSources,
        Action<int>? wait = null, StudioFileSafety.FileIdentity? identity = null, Action? validateAssets = null)
    {
        var sources = protectedSources.ToArray();
        identity ??= StudioFileSafety.StagingIdentity(staged);
        RetryFileAccess(() =>
        {
            StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
            var validExisting = ValidateWriteDestination(path, keepBackup, sources);
            if (validateAssets is not null)
            {
                validateAssets();
                validExisting = ValidateWriteDestination(path, keepBackup, sources);
            }
            StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
            if (validExisting) File.Replace(staged, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(staged, path, true);
        }, wait, () => File.Exists(staged), RetryableProjectPublication);
    }
    internal static void CleanupStaging(string staged, StudioFileSafety.FileIdentity? identity, Action<int>? wait = null, Action? validateContent = null)
    {
        if (identity is null) return;
        try
        {
            RetryFileAccess(() =>
            {
                try { StudioFileSafety.EnsureOwnedStaging(staged, identity.Value); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
                if (validateContent is not null) { validateContent(); StudioFileSafety.EnsureOwnedStaging(staged, identity.Value); }
                File.Delete(staged);
            }, wait);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Trace.TraceWarning("Temporary-file cleanup failed; retained {0}: {1}", staged, error.Message); }
    }
    internal static bool RetryableFileAccess(Exception error)
    {
        if (!OperatingSystem.IsWindows() || error is not IOException && error is not UnauthorizedAccessException) return false;
        var code = (uint)error.HResult;
        return (code & 0xffff0000) == 0x80070000 && (code & 0xffff) is 5 or 32 or 33;
    }
    internal static bool RetryableProjectPublication(Exception error)
        => RetryableFileAccess(error) || OperatingSystem.IsWindows() && error is IOException && (uint)error.HResult == 0x80070497u;

    internal static void RetryFileAccess(Action operation, Action<int>? wait = null, Func<bool>? mayRetry = null, Func<Exception, bool>? retryable = null)
    {
        retryable ??= RetryableFileAccess;
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception error) when (attempt + 1 < MaximumFileAccessAttempts && retryable(error) && (mayRetry is null || mayRetry()))
            {
                var delay = 25 << attempt;
                if (wait is null) Thread.Sleep(delay); else wait(delay);
            }
        }
    }
    public static JsonObject? Recovery(IEnumerable<string>? candidates = null)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var path in candidates ?? new[] { RecoveryPath, RecoveryPath + ".bak", LastGoodRecoveryPath(RecoveryPath), Path.Combine(roaming, "nba2k-court-creator", "recovery.json"), Path.Combine(roaming, "NBA 2K Court Creator", "recovery.json") })
        {
            if (!File.Exists(path)) continue;
            try { return Read(path); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
