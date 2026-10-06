using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace NBA2KCourtCreator.Studio;

internal sealed class StudioImportPreviewFiles : IDisposable
{
    private readonly SafeFileHandle _directoryHandle;
    private readonly StudioFileSafety.FileIdentity _directoryIdentity;
    private StudioFileSafety.FileIdentity? _fileIdentity;
    private string? _revision;
    private long _bytes;
    private int _disposed;
    internal string DirectoryPath { get; }
    internal string OutputPath => Path.Combine(DirectoryPath, "preview.png");

    internal StudioImportPreviewFiles(string? temporaryRoot = null)
    {
        var root = StudioFileSafety.ResolvePath(temporaryRoot ?? Path.GetTempPath());
        DirectoryPath = Path.Combine(root, "court-import-" + Guid.NewGuid().ToString("N"));
        // Create-new and a non-delete-shared handle avoid adopting an existing folder.
        _directoryHandle = StudioFileSafety.CreateOwnedDirectory(DirectoryPath);
        _directoryIdentity = StudioFileSafety.DirectoryIdentity(_directoryHandle);
    }

    internal void AcceptReceipt(JsonNode? node)
    {
        if (_fileIdentity is not null) throw new InvalidOperationException("The preview receipt was already accepted.");
        try
        {
            if (node is not JsonObject receipt || receipt["version"]?.Deserialize<int>() != 1
                || receipt["identity"] is not JsonObject identity)
                throw new InvalidDataException("The import engine returned no supported preview receipt.");
            var revision = receipt["sha256"]?.Deserialize<string>();
            var bytes = receipt["bytes"]?.Deserialize<long>() ?? 0;
            if (revision is null || !System.Text.RegularExpressions.Regex.IsMatch(revision, "\\A[0-9a-f]{64}\\z")
                || bytes < 1 || bytes > StudioImages.MaximumAssetBytes)
                throw new InvalidDataException("The import engine returned an invalid preview receipt.");
            uint Field(string name) => identity[name]?.Deserialize<uint>() ?? throw new InvalidDataException("Incomplete preview identity.");
            _fileIdentity = new(Field("volume"), Field("high"), Field("low"));
            _revision = revision; _bytes = bytes;
            Validate();
        }
        catch (JsonException error) { throw new InvalidDataException("The import engine returned an invalid preview receipt.", error); }
    }

    internal void Validate()
    {
        EnsureDirectory();
        if (_fileIdentity is null || _revision is null) throw new InvalidDataException("The preview has no ownership receipt.");
        StudioFileSafety.EnsureOwnedStaging(OutputPath, _fileIdentity.Value);
        if (new FileInfo(OutputPath).Length != _bytes || StudioImages.FileRevision(OutputPath) != _revision)
            throw new InvalidDataException("The import preview changed after it was produced.");
        EnsureDirectory();
        StudioFileSafety.EnsureOwnedStaging(OutputPath, _fileIdentity.Value);
    }

    internal void ValidatePixels(System.Windows.Media.ImageSource image)
    {
        if (_revision is null || StudioImages.SourceRevision(image) != _revision)
            throw new InvalidDataException("The decoded preview does not match the produced PNG.");
        Validate();
    }

    internal System.Windows.Media.Imaging.BitmapSource Decode(JsonNode? receipt, CancellationToken cancellation)
    {
        AcceptReceipt(receipt);
        cancellation.ThrowIfCancellationRequested();
        var image = StudioImages.Load(OutputPath);
        ValidatePixels(image);
        cancellation.ThrowIfCancellationRequested();
        return image;
    }

    private void EnsureDirectory()
    {
        if (StudioFileSafety.DirectoryIdentity(DirectoryPath) != _directoryIdentity)
            throw new InvalidDataException("The temporary preview directory identity changed: " + DirectoryPath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_fileIdentity is not null)
                StudioProjectStore.CleanupStaging(OutputPath, _fileIdentity, validateContent: Validate);
        }
        finally
        {
            _directoryHandle.Dispose();
            try
            {
                StudioProjectStore.RetryFileAccess(() =>
                {
                    EnsureDirectory();
                    Directory.Delete(DirectoryPath, recursive: false);
                });
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { Trace.TraceWarning("Import preview cleanup retained {0}: {1}", DirectoryPath, error.Message); }
        }
    }
}
