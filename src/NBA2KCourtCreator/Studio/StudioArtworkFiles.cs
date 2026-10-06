using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SixLabors.ImageSharp;
using TextureStudio.Services;
using TwoK.Canvas.Hosting;

namespace NBA2KCourtCreator.Studio;

internal sealed class StudioArtworkFiles : IDisposable
{
    private readonly List<StudioPortableAssets.Receipt> _files = [];
    private readonly string _directory;
    private readonly StudioFileSafety.FileIdentity _directoryIdentity;
    private SafeFileHandle? _guard;
    private bool _committed, _disposed;
    internal string ImagePath => Path.Combine(_directory, "artwork.png");
    internal string ProjectPath => Path.Combine(_directory, "artwork.2kstudio");
    internal string? DdsPath { get; private set; }
    internal string ImageRevision => _files.Single(file => file.Path == ImagePath).Hash;
    internal string ProjectRevision => _files.Single(file => file.Path == ProjectPath).Hash;

    private StudioArtworkFiles(string root)
    {
        Directory.CreateDirectory(root); _directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        _guard = StudioFileSafety.CreateOwnedDirectory(_directory);
        _directoryIdentity = StudioFileSafety.DirectoryIdentity(_guard);
    }
    internal static async Task<StudioArtworkFiles> PrepareAsync(CanvasEditResult result, int width, int height,
        CancellationToken cancellation = default, string? root = null)
    {
        if (result.Width != width || result.Height != height)
            throw new InvalidDataException("Keep the original artwork dimensions before applying to the court.");
        var files = new StudioArtworkFiles(root ?? Path.Combine(StudioProjectStore.SettingsDirectory, "artwork"));
        try
        {
            await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                using var image = result.CreateImage();
                files.Write(files.ImagePath, stream => image.SaveAsPng(stream));
                files.Write(files.ProjectPath, stream => stream.Write(result.ProjectData.Span));
                cancellation.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(files.ProjectPath); using var project = ProjectCodec.Load(stream);
                if (project.CanvasWidth != width || project.CanvasHeight != height)
                    throw new InvalidDataException("The editable artwork dimensions do not match its pixels.");
                if (project.DdsMetadata is not null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    files.DdsPath = Path.Combine(files._directory, "artwork.dds");
                    await TextureCodec.ExportDdsAsync(project, files.DdsPath);
                    using var handle = File.OpenHandle(files.DdsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    files._files.Add(new(files.DdsPath, StudioImages.FileRevision(files.DdsPath), new FileInfo(files.DdsPath).Length,
                        StudioFileSafety.StagingIdentity(handle)));
                }
            }, cancellation);
            cancellation.ThrowIfCancellationRequested(); files.Validate(); return files;
        }
        catch { await Task.Run(files.Dispose); throw; }
    }
    private void Write(string path, Action<Stream> write)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var identity = StudioFileSafety.StagingIdentity(stream.SafeFileHandle);
        try { write(stream); stream.Flush(true); }
        finally
        {
            stream.Position = 0;
            _files.Add(new(path, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), stream.Length, identity));
        }
    }
    internal void Validate()
    {
        if (StudioFileSafety.DirectoryIdentity(_directory) != _directoryIdentity) throw new IOException("The artwork directory changed.");
        foreach (var file in _files) file.Validate();
    }
    internal void Commit() { Validate(); _committed = true; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _guard?.Dispose(); _guard = null;
        if (_committed) return;
        foreach (var file in _files) StudioProjectStore.CleanupStaging(file.Path, file.Identity, validateContent: () => file.Validate());
        try
        {
            if (StudioFileSafety.DirectoryIdentity(_directory) == _directoryIdentity) Directory.Delete(_directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Artwork cleanup retained {0}: {1}", _directory, error.Message); }
    }
}
