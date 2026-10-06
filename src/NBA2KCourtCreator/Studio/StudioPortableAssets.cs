using System.IO;
using System.Security.Cryptography;

namespace NBA2KCourtCreator.Studio;

internal static class StudioPortableAssets
{
    internal sealed record Receipt(string Path, string Hash, long Bytes, StudioFileSafety.FileIdentity Identity)
    {
        internal void Validate(long maximumBytes = StudioImages.MaximumAssetBytes)
        {
            StudioFileSafety.EnsureOwnedStaging(Path, Identity);
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (stream.Length != Bytes || HashStream(stream, maximumBytes) != Hash)
                throw new InvalidDataException("Portable artwork changed; the existing file was preserved: " + Path);
            StudioFileSafety.EnsureOwnedStaging(Path, Identity);
        }
    }

    internal static Receipt Bundle(string sourcePath, string assetDirectory, string? expectedRevision, IEnumerable<string> protectedSources,
        Action<string, string>? prepared = null, Action<int>? wait = null, Action<string, long>? copiedChunk = null,
        long maximumBytes = StudioImages.MaximumAssetBytes)
    {
        if (maximumBytes < 1 || maximumBytes > StudioImages.MaximumAssetBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        sourcePath = Path.GetFullPath(sourcePath); assetDirectory = Path.GetFullPath(assetDirectory);
        var sources = protectedSources.ToArray();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var bytes = input.Length;
        var hash = HashStream(input, maximumBytes);
        if (expectedRevision is not null && hash != expectedRevision)
            throw new IOException("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before saving.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension.Length > 16 || extension.Any(character => character != '.' && !char.IsAsciiLetterOrDigit(character))) extension = ".image";
        Directory.CreateDirectory(assetDirectory);
        using var directoryHandle = StudioFileSafety.OpenOwnedDirectory(assetDirectory);
        var directoryIdentity = StudioFileSafety.DirectoryIdentity(directoryHandle);
        void CheckDirectory()
        {
            if (StudioFileSafety.DirectoryIdentity(assetDirectory) != directoryIdentity)
                throw new InvalidDataException("The portable asset directory changed; existing files were preserved: " + assetDirectory);
        }
        var destination = Path.Combine(assetDirectory, hash + extension);
        Receipt? before = null;
        StudioProjectStore.RetryFileAccess(() => { CheckDirectory(); before = Snapshot(destination, maximumBytes); }, wait);
        if (before is not null && before.Hash == hash && before.Bytes == bytes) return before;
        StudioFileSafety.ProtectSources(destination, sources);
        var staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        StudioFileSafety.FileIdentity? identity = null;
        string? stagedHash = null;
        Receipt? result = null;
        void CheckStaged()
        {
            CheckDirectory();
            if (identity is null || stagedHash is null) throw new InvalidDataException("The portable copy has no ownership receipt.");
            StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
            using var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (HashStream(stream, maximumBytes) != stagedHash)
                throw new InvalidDataException("Portable staging bytes changed; the external file was retained: " + staged);
            StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
        }
        try
        {
            using var copyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                identity = StudioFileSafety.StagingIdentity(output.SafeFileHandle);
                try
                {
                    input.Position = 0; var buffer = new byte[65536]; long total = 0;
                    while (true)
                    {
                        var count = input.Read(buffer, 0, buffer.Length); if (count == 0) break;
                        total += count;
                        if (total > maximumBytes) throw new InvalidDataException("Project asset exceeds the 512 MB size limit.");
                        output.Write(buffer, 0, count); copyHash.AppendData(buffer, 0, count); copiedChunk?.Invoke(staged, total);
                    }
                    if (total != bytes) throw new InvalidDataException("Project artwork changed while being copied.");
                    output.Flush(true);
                }
                finally { stagedHash = Convert.ToHexString(copyHash.GetHashAndReset()).ToLowerInvariant(); }
            }
            if (stagedHash != hash) throw new InvalidDataException("Project artwork changed while being copied.");
            CheckStaged();
            prepared?.Invoke(staged, destination);
            StudioProjectStore.RetryFileAccess(() =>
            {
                CheckStaged();
                if (HashStream(input, maximumBytes) != hash) throw new InvalidDataException("Project artwork changed before its portable copy was published.");
                var current = Snapshot(destination, maximumBytes);
                if (current is not null && current.Hash == hash && current.Bytes == bytes) { result = current; return; }
                if (current != before) throw new InvalidDataException("A portable asset changed externally; it was not overwritten: " + destination);
                StudioFileSafety.ProtectSources(destination, sources);
                CheckDirectory(); StudioFileSafety.EnsureOwnedStaging(staged, identity.Value);
                try { File.Move(staged, destination, overwrite: before is not null); }
                catch (IOException error) when (before is null && (uint)error.HResult is 0x80070050u or 0x800700b7u)
                {
                    var concurrent = Snapshot(destination, maximumBytes);
                    if (concurrent is null || concurrent.Hash != hash || concurrent.Bytes != bytes) throw;
                    result = concurrent; return;
                }
                result = new Receipt(destination, hash, bytes, identity.Value);
                result.Validate(maximumBytes);
            }, wait, () => File.Exists(staged), StudioProjectStore.RetryableProjectPublication);
            return result ?? throw new InvalidDataException("Portable artwork was not published.");
        }
        finally
        {
            StudioProjectStore.CleanupStaging(staged, identity, wait, stagedHash is null ? null : CheckStaged);
        }
    }

    private static Receipt? Snapshot(string path, long maximumBytes)
    {
        StudioFileSafety.FileIdentity identity;
        try { identity = StudioFileSafety.StagingIdentity(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var bytes = stream.Length; var hash = HashStream(stream, maximumBytes);
        StudioFileSafety.EnsureOwnedStaging(path, identity);
        return new(path, hash, bytes, identity);
    }

    private static string HashStream(FileStream stream, long maximumBytes)
    {
        if (stream.Length > maximumBytes) throw new InvalidDataException("Project asset exceeds the 512 MB size limit.");
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long total = 0;
            while (true)
            {
                var count = stream.Read(buffer, 0, buffer.Length); if (count == 0) break;
                total += count;
                if (total > maximumBytes) throw new InvalidDataException("Project asset exceeds the 512 MB size limit.");
                hash.AppendData(buffer, 0, count);
            }
            if (total != stream.Length) throw new InvalidDataException("Project artwork changed while being read.");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { stream.Position = position; }
    }
}
