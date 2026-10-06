using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace TwoK.Studio;

public sealed class ArtworkLayer : INotifyPropertyChanged
{
    private static readonly Dictionary<string, PropertyChangedEventArgs> Notifications = new[] { nameof(Name), nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Rotation), nameof(Opacity), nameof(Visible), nameof(ScaleLocked), nameof(FlipX), nameof(FlipY) }.ToDictionary(name => name, name => new PropertyChangedEventArgs(name));
    private string _name = "Logo";
    private double _x, _y, _width = 400, _height = 400, _rotation, _opacity = 100;
    private bool _visible = true, _scaleLocked = true, _flipX, _flipY;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public ImageSource? Image { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public double X { get => _x; set => Set(ref _x, Finite(value)); }
    public double Y { get => _y; set => Set(ref _y, Finite(value)); }
    public double Width { get => _width; set => Set(ref _width, Math.Clamp(Finite(value), 1, 32768)); }
    public double Height { get => _height; set => Set(ref _height, Math.Clamp(Finite(value), 1, 32768)); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, Finite(value) % 360); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(Finite(value), 0, 100)); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public bool ScaleLocked { get => _scaleLocked; set => Set(ref _scaleLocked, value); }
    public bool FlipX { get => _flipX; set => Set(ref _flipX, value); }
    public bool FlipY { get => _flipY; set => Set(ref _flipY, value); }
    public Point Center => new(X + Width / 2, Y + Height / 2);
    public ArtworkState Capture() => new(X, Y, Width, Height, Rotation);
    public void Restore(ArtworkState state) { X = state.X; Y = state.Y; Width = state.Width; Height = state.Height; Rotation = state.Rotation; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, name is not null && Notifications.TryGetValue(name, out var notification) ? notification : new PropertyChangedEventArgs(name));
    }
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}

public sealed record ArtworkState(double X, double Y, double Width, double Height, double Rotation);
public sealed record StudioAnchor(string Id, string Name, Point Position);

public static class TransformGeometry
{
    public static double ConstrainScale(double width, double height, double scale)
    {
        if(!double.IsFinite(width) || width<1 || width>32768)throw new ArgumentOutOfRangeException(nameof(width));
        if(!double.IsFinite(height) || height<1 || height>32768)throw new ArgumentOutOfRangeException(nameof(height));
        if(double.IsNaN(scale))throw new ArgumentOutOfRangeException(nameof(scale));
        // A single bounded factor preserves proportions when either dimension reaches its limit.
        return Math.Clamp(scale,Math.Max(1/width,1/height),Math.Min(32768/width,32768/height));
    }
    public static Size ScaleDimensions(double width, double height, double scale)
    {
        scale=ConstrainScale(width,height,scale);
        return new Size(Math.Clamp(width*scale,1,32768),Math.Clamp(height*scale,1,32768));
    }
    // Same center-based transform convention used by 2K Canvas LayerTransformService.
    public static Vector Rotate(Vector point, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Vector(point.X * Math.Cos(radians) - point.Y * Math.Sin(radians),
                          point.X * Math.Sin(radians) + point.Y * Math.Cos(radians));
    }
    public static Rect Bounds(ArtworkState state)
    {
        var angle = state.Rotation * Math.PI / 180;
        var w = Math.Abs(Math.Cos(angle)) * state.Width + Math.Abs(Math.Sin(angle)) * state.Height;
        var h = Math.Abs(Math.Sin(angle)) * state.Width + Math.Abs(Math.Cos(angle)) * state.Height;
        return new Rect(state.X + state.Width / 2 - w / 2, state.Y + state.Height / 2 - h / 2, w, h);
    }
    public static ArtworkState Resize(ArtworkState original, Point cursor, bool locked)
    {
        var center = new Point(original.X + original.Width / 2, original.Y + original.Height / 2);
        var local = Rotate(cursor - center, -original.Rotation);
        var width = Math.Clamp(Math.Abs(local.X) * 2, 1, 32768);
        var height = Math.Clamp(Math.Abs(local.Y) * 2, 1, 32768);
        if (locked)
        {
            var size = ScaleDimensions(original.Width,original.Height,Math.Max(width/original.Width,height/original.Height));
            width=size.Width;height=size.Height;
        }
        return new ArtworkState(center.X - width / 2, center.Y - height / 2, width, height, original.Rotation);
    }
}
