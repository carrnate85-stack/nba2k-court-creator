using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace NBA2KCourtCreator.Studio;

internal sealed class PreparedLogoAsset : IDisposable
{
    internal BitmapSource Image { get; }
    internal string Path { get; }
    private readonly StudioFileSafety.FileIdentity? _identity;
    private readonly string? _revision;
    private int _retain;
    internal PreparedLogoAsset(BitmapSource image, string path, bool ownsNewFile = false,
        StudioFileSafety.FileIdentity? identity = null, string? revision = null)
    {
        Image = image; Path = System.IO.Path.GetFullPath(path); _retain = ownsNewFile ? 0 : 1;
        if (!ownsNewFile) return;
        _identity = identity ?? StudioFileSafety.StagingIdentity(Path);
        StudioFileSafety.EnsureOwnedStaging(Path, _identity.Value);
        var actual = StudioImages.FileRevision(Path);
        _revision = revision ?? StudioImages.SourceRevision(image) ?? actual;
        if (_revision != actual || StudioImages.SourceRevision(image) is { } pixels && pixels != _revision)
            throw new InvalidDataException("Managed logo bytes no longer match their prepared pixels.");
        StudioFileSafety.EnsureOwnedStaging(Path, _identity.Value);
    }
    internal void Commit() => Interlocked.Exchange(ref _retain, 1);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retain, 1) != 0) return;
        Cleanup(Path, _identity, _revision);
    }
    private static void Cleanup(string path, StudioFileSafety.FileIdentity? identity, string? revision)
    {
        StudioProjectStore.CleanupStaging(path, identity, validateContent: revision is null ? null : () =>
        {
            if (StudioImages.FileRevision(path) != revision)
                throw new InvalidDataException("Managed logo bytes changed; the external file was retained: " + path);
        });
    }
    internal static PreparedLogoAsset CopyAndLoad(string source, string directory, CancellationToken cancellation,
        Action<string, long>? copiedChunk = null, Action<string>? beforeDecode = null)
    {
        cancellation.ThrowIfCancellationRequested();
        source = System.IO.Path.GetFullPath(source); directory = System.IO.Path.GetFullPath(directory);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (input.Length > StudioImages.MaximumAssetBytes) throw new NotSupportedException("Image file exceeds the 512 MB size limit.");
        Directory.CreateDirectory(directory);
        var owned = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(source));
        StudioFileSafety.FileIdentity? identity = null;
        string? revision = null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using (var output = new FileStream(owned, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                identity = StudioFileSafety.StagingIdentity(output.SafeFileHandle);
                try
                {
                    var buffer = new byte[64 * 1024]; long total = 0;
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var read = input.Read(buffer, 0, buffer.Length); if (read == 0) break;
                        total += read;
                        if (total > StudioImages.MaximumAssetBytes) throw new NotSupportedException("Image file exceeds the 512 MB size limit.");
                        output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); copiedChunk?.Invoke(owned, total);
                    }
                    output.Flush(true);
                }
                finally { revision = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); }
            }
            cancellation.ThrowIfCancellationRequested();
            beforeDecode?.Invoke(owned);
            cancellation.ThrowIfCancellationRequested();
            StudioFileSafety.EnsureOwnedStaging(owned, identity.Value);
            var image = StudioImages.Load(owned);
            cancellation.ThrowIfCancellationRequested();
            return new PreparedLogoAsset(image, owned, true, identity, revision);
        }
        catch
        {
            Cleanup(owned, identity, revision);
            throw;
        }
    }
}
