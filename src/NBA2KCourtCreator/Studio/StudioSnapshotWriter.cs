using System.Text.Json.Nodes;

namespace NBA2KCourtCreator.Studio;

internal sealed record StudioWriteResult(long Revision, Exception? Error);

internal sealed class StudioSnapshotWriter(Action<JsonObject> write)
{
    private readonly object _gate = new();
    private JsonObject? _pending;
    private Task<StudioWriteResult>? _worker;
    private long _revision;
    private StudioWriteResult _last = new(0, null);

    internal long LatestRevision { get { lock (_gate) return _revision; } }

    internal Task<StudioWriteResult> Queue(JsonObject project)
    {
        var snapshot = (JsonObject)project.DeepClone();
        lock (_gate)
        {
            _pending = snapshot;
            ++_revision;
            return _worker ??= Task.Run(Drain);
        }
    }

    internal Task<StudioWriteResult> FlushAsync()
    {
        lock (_gate) return _worker ?? Task.FromResult(_last);
    }

    private StudioWriteResult Drain()
    {
        while (true)
        {
            JsonObject snapshot;
            long revision;
            lock (_gate)
            {
                if (_pending is null) { _worker = null; return _last; }
                snapshot = _pending; revision = _revision; _pending = null;
            }
            Exception? failure = null;
            try { write(snapshot); }
            catch (Exception error) { failure = error; }
            lock (_gate) _last = new(revision, failure);
        }
    }
}
