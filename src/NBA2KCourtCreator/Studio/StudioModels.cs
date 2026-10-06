using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NBA2KCourtCreator.Studio;

public sealed class StockLayer : INotifyPropertyChanged
{
    private bool _visible;
    private string _color = "#FFFFFF";
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public bool DefaultVisible { get; init; }
    public string DefaultColor { get; init; } = "#FFFFFF";
    public bool Visible { get => _visible; set { _visible = value; Changed(); } }
    public string Color { get => _color; set { _color = value; Changed(); } }
    public Geometry Geometry { get; init; } = Geometry.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record StockFloor(string Id, string Name, string Category, string Path, string PreviewPath, JsonObject Source)
{
    private static readonly Regex WoodTag = new(@"\bWood\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string FriendlyName(string originalName, string category)
    {
        if (category.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return originalName;
        var name = Regex.Replace(originalName, @"\s+(?:Court\s+)?Wood\s*\d+\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (category.Equals("NBA", StringComparison.OrdinalIgnoreCase)) name = Regex.Replace(name, @"\s*\((?:00\d|01\d|02\d|03[01])\)", "");
        name = Regex.Replace(name, @"\s{2,}", " ").Trim();
        return name.Length == 0 ? originalName : name;
    }
    public static StockFloor Read(JsonObject item, string root, IReadOnlyList<StockFloor>? library = null)
    {
        string Resolve(string value) => System.IO.Path.IsPathRooted(value) ? value : System.IO.Path.Combine(root, value);
        var category = item["category"]?.GetValue<string>() ?? "Custom";
        var originalName = item["name"]!.GetValue<string>();
        var name = FriendlyName(originalName, category);
        var id = item["id"]!.GetValue<string>();
        if (!category.Equals("Custom", StringComparison.OrdinalIgnoreCase) && WoodTag.IsMatch(originalName)
            && library?.FirstOrDefault(floor => floor.Id == id && !floor.Category.Equals("Custom", StringComparison.OrdinalIgnoreCase)) is { } known
            && name.Equals(FriendlyName(known.Source["name"]?.GetValue<string>() ?? known.Name, known.Category), StringComparison.OrdinalIgnoreCase)) name = known.Name;
        var path = Resolve(item["path"]!.GetValue<string>());
        return new(id, name, category, path,
            Resolve(item["previewPath"]?.GetValue<string>() ?? path), (JsonObject)item.DeepClone());
    }

    public static StockFloor[] ReadMany(IEnumerable<JsonObject> items, string root)
    {
        var floors = items.Select(item => Read(item, root)).ToArray();
        var indexed = floors.Select((floor, index) => (Floor: floor, Index: index)).ToArray();
        foreach (var category in indexed.GroupBy(item => item.Floor.Category, StringComparer.OrdinalIgnoreCase))
        {
            var usedNames = new HashSet<string>(category.Select(item => item.Floor.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var group in category.GroupBy(item => item.Floor.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                int Variant(StockFloor floor) => !floor.Category.Equals("Custom", StringComparison.OrdinalIgnoreCase) && int.TryParse(WoodTag.Match(floor.Source["name"]?.GetValue<string>() ?? "").Groups[1].Value,
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;
                var ordered = group.Select(item => (item.Floor, item.Index, Variant: Variant(item.Floor))).OrderBy(item => item.Variant > 0 ? item.Variant : int.MaxValue)
                    .ThenBy(item => item.Floor.Id, StringComparer.Ordinal).ThenBy(item => item.Floor.Path, StringComparer.Ordinal).ToArray();
                var baseName = ordered[0].Floor.Name;
                foreach (var item in ordered)
                {
                    var suffix = item.Variant;
                    if (suffix <= 0 || usedNames.Contains(baseName + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture))) suffix = 1;
                    while (!usedNames.Add(baseName + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture))) suffix++;
                    floors[item.Index] = item.Floor with { Name = baseName + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                }
            }
        }
        return floors;
    }
}

public static class StudioImages
{
    internal const long MaximumAssetBytes = 512L * 1024 * 1024;
    private sealed record Revision(string Hash);
    private static readonly StudioBitmapCache Cache = new(128L * 1024 * 1024, 80);
    private static readonly ConditionalWeakTable<BitmapSource, Revision> Revisions = new();
    private static long _sourceReads, _hashedBytes;
    internal static (long SourceReads, long HashedBytes) ReadStatistics => (Interlocked.Read(ref _sourceReads),Interlocked.Read(ref _hashedBytes));
    internal static string? SourceRevision(ImageSource? image) => image is BitmapSource bitmap && Revisions.TryGetValue(bitmap, out var revision) ? revision.Hash : null;
    internal static void AttachRevision(BitmapSource image, string hash) => Revisions.GetValue(image, _ => new Revision(hash));
    internal static string FileRevision(string path)
    {
        using var stream=OpenSource(path);
        return HashSource(stream);
    }
    internal static (int Count, long Bytes, long Budget) CacheStatistics => Cache.Statistics;
    public static BitmapSource Load(string path, int decodeWidth = 0)
    {
        ValidateDecodeWidth(decodeWidth,nameof(decodeWidth));
        using var stream=OpenSource(path);
        return LoadFromSource(stream,HashSource(stream),decodeWidth);
    }
    internal static (BitmapSource Image,BitmapSource Thumbnail) LoadPair(string path,int decodeWidth,int thumbnailWidth,CancellationToken cancellation=default)
    {
        ValidateDecodeWidth(decodeWidth,nameof(decodeWidth));ValidateDecodeWidth(thumbnailWidth,nameof(thumbnailWidth));
        cancellation.ThrowIfCancellationRequested();
        using var stream=OpenSource(path);var hash=HashSource(stream);
        cancellation.ThrowIfCancellationRequested();
        var image=LoadFromSource(stream,hash,decodeWidth);
        cancellation.ThrowIfCancellationRequested();
        var thumbnail=LoadFromSource(stream,hash,thumbnailWidth);
        cancellation.ThrowIfCancellationRequested();
        return (image,thumbnail);
    }
    private static void ValidateDecodeWidth(int width,string parameter)
    {if(width<0 || width>16384)throw new ArgumentOutOfRangeException(parameter);}
    private static FileStream OpenSource(string path)
    {
        var stream=new FileStream(Path.GetFullPath(path),FileMode.Open,FileAccess.Read,FileShare.Read);
        Interlocked.Increment(ref _sourceReads);
        try
        {
            if(stream.Length>MaximumAssetBytes)throw new NotSupportedException("Image file exceeds the 512 MB size limit.");
            return stream;
        }
        catch{stream.Dispose();throw;}
    }
    private static string HashSource(FileStream stream)
    {
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Interlocked.Add(ref _hashedBytes,stream.Position);
        return hash;
    }
    private static BitmapSource LoadFromSource(FileStream stream,string hash,int decodeWidth)
    {
        var key = $"{hash}|{decodeWidth}";
        var image = Cache.Get(key, () => { stream.Position = 0; return Decode(stream, decodeWidth); });
        Revisions.GetValue(image, _ => new Revision(hash));
        return image;
    }
    private static BitmapSource Decode(Stream stream, int decodeWidth)
    {
        var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (header.PixelWidth > 16384 || header.PixelHeight > 16384 || (long)header.PixelWidth * header.PixelHeight > 64L * 1024 * 1024)
            throw new NotSupportedException("Image is too large. Use an image up to 16384 pixels per side and 64 megapixels.");
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = Math.Min(decodeWidth, header.PixelWidth);
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        BitmapSource result = image;
        if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
        { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); converted.Freeze(); result = converted; }
        return result;
    }
    public static string? Hex(string? value)
    {
        var hex = value?.Trim().TrimStart('#') ?? "";
        if (Regex.IsMatch(hex, "^[0-9a-fA-F]{3}$")) hex = string.Concat(hex.Select(c => new string(c, 2)));
        return Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$") ? "#" + hex.ToUpperInvariant() : null;
    }
    public static SolidColorBrush Brush(string hex)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
}
