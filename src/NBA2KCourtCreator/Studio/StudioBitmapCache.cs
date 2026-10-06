using System.Windows.Media.Imaging;

namespace NBA2KCourtCreator.Studio;

internal sealed class StudioBitmapCache(long byteBudget, int maxEntries)
{
    private sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<string> Node);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Lazy<BitmapSource>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recency = new();
    private long _bytes;
    internal (int Count, long Bytes, long Budget) Statistics { get { lock (_gate) return (_entries.Count, _bytes, byteBudget); } }
    public BitmapSource Get(string key, Func<BitmapSource> decode)
    {
        Lazy<BitmapSource> pending;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            { _recency.Remove(existing.Node); _recency.AddFirst(existing.Node); return existing.Image; }
            if (!_pending.TryGetValue(key, out pending!))
            { pending = new Lazy<BitmapSource>(decode, LazyThreadSafetyMode.ExecutionAndPublication); _pending.Add(key, pending); }
        }
        try
        {
            var image = pending.Value;
            // Converted WIC sources can retain their original decoder too; budget conservatively.
            var bytes = checked((long)image.PixelWidth * image.PixelHeight * 8);
            lock (_gate)
            {
                if (!_entries.ContainsKey(key) && bytes <= byteBudget)
                {
                    while (_entries.Count >= maxEntries || _bytes + bytes > byteBudget)
                    { var oldest = _recency.Last!; _bytes -= _entries[oldest.Value].Bytes; _entries.Remove(oldest.Value); _recency.RemoveLast(); }
                    var node = _recency.AddFirst(key); _entries.Add(key, new Entry(image, bytes, node)); _bytes += bytes;
                }
            }
            return image;
        }
        finally
        { lock (_gate) if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key); }
    }
}
