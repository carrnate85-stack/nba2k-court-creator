using System.IO;
using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

internal sealed class StudioPreferenceStore
{
    private readonly string _path;
    private readonly StudioSnapshotWriter _writer;
    internal long LatestRevision => _writer.LatestRevision;

    internal StudioPreferenceStore(string directory, Action<string, JsonObject>? write = null)
    {
        _path = Path.Combine(Path.GetFullPath(directory), "preferences.json");
        _writer = new StudioSnapshotWriter(document => (write ?? StudioPreferences.Write)(_path, document));
    }

    internal Task<StudioPreferences?> ReadAsync() => Task.Run(() =>
    {
        try { return StudioPreferences.Read(_path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    });
    internal Task<StudioWriteResult> Queue(JsonObject document) => _writer.Queue(document);
    internal Task<StudioWriteResult> FlushAsync() => _writer.FlushAsync();
}
