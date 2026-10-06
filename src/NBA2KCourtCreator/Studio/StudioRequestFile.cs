using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

internal sealed class StudioRequestFile : IDisposable
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private const int BufferBytes = 65536;
    private FileStream? _stream;
    private StudioFileSafety.FileIdentity? _identity;
    private byte[] _hash = SHA256.HashData(Array.Empty<byte>());
    private long _bytes;
    private int _disposed;
    internal string Path { get; }

    private StudioRequestFile(string path) => Path = path;

    internal static async Task<StudioRequestFile> CreateAsync(JsonObject request, CancellationToken cancellationToken,
        string? temporaryRoot = null, Action<string, long>? writtenChunk = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json;
            try { json = request.ToJsonString(new JsonSerializerOptions { MaxDepth = 32 }); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { throw new InvalidDataException("The Python request contains unsupported nested data.", error); }
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("The Python request exceeds the 16 MB size limit.");
            cancellationToken.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetBytes(json);
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var file = new StudioRequestFile(System.IO.Path.Combine(System.IO.Path.GetFullPath(temporaryRoot ?? System.IO.Path.GetTempPath()),
            "court-studio-" + Guid.NewGuid().ToString("N") + ".json"));
        try
        {
            file._stream = new FileStream(file.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                BufferBytes, FileOptions.Asynchronous);
            file._identity = StudioFileSafety.StagingIdentity(file._stream.SafeFileHandle);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var offset = 0; offset < data.Length; offset += BufferBytes)
            {
                var count = Math.Min(BufferBytes, data.Length - offset);
                await file._stream.WriteAsync(data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                hash.AppendData(data, offset, count);
                file._bytes += count; file._hash = hash.GetCurrentHash();
                writtenChunk?.Invoke(file.Path, file._bytes);
            }
            await file._stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            StudioFileSafety.EnsureOwnedStaging(file.Path, file._identity.Value);
            file.ValidateContent(file._stream);
            cancellationToken.ThrowIfCancellationRequested();
            return file;
        }
        catch { await Task.Run(file.Dispose).ConfigureAwait(false); throw; }
    }

    private void ValidateContent(Stream stream)
    {
        if (stream.Length != _bytes || stream.Length > MaximumBytes)
            throw new InvalidDataException("The temporary Python request changed; preserving it: " + Path);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes]; long count = 0; int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            count += read;
            if (count > _bytes) throw new InvalidDataException("The temporary Python request grew; preserving it: " + Path);
            hash.AppendData(buffer, 0, read);
        }
        if (count != _bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), _hash))
            throw new InvalidDataException("The temporary Python request contents changed; preserving it: " + Path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var stream = Interlocked.Exchange(ref _stream, null);
        try { stream?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Temporary request close failed; checking before cleanup: {0}: {1}", Path, error.Message);
            stream?.SafeFileHandle.Dispose();
        }
        StudioProjectStore.CleanupStaging(Path, _identity, validateContent: () =>
        {
            using var content = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
            ValidateContent(content);
        });
    }
}
