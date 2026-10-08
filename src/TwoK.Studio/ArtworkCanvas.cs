using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TwoK.Studio;

public sealed class ArtworkGestureEventArgs(ArtworkLayer layer, ArtworkState before, ArtworkState after) : EventArgs
{
    public ArtworkLayer Layer { get; } = layer;
    public ArtworkState Before { get; } = before;
    public ArtworkState After { get; } = after;
}

public enum ArtworkTool { Move, Transform, Hand, Zoom, Eyedropper, Bucket, Type }

public sealed class ArtworkCanvas : FrameworkElement
{
    private Drawing? _background;
    private ArtworkLayer? _selected;
    private ArtworkState? _original;
    private Point _start, _panStart;
    private Vector _pan;
    private string? _gesture;
    private MouseButton? _gestureButton;
    private ArtworkResizeGesture? _resize;
    private bool _dragStarted;
    private bool _canceling;
    private bool _startingTransform;
    private Size _gestureRenderSize;
    private double _zoom = 1;
    private ArtworkTool _tool;
    public ArtworkTool Tool
    {
        get => _tool;
        set { if (_tool == value) return; CancelGesture(); _tool = value; Cursor = value switch { ArtworkTool.Hand => Cursors.Hand, ArtworkTool.Type => Cursors.IBeam, ArtworkTool.Zoom or ArtworkTool.Eyedropper or ArtworkTool.Bucket => Cursors.Cross, _ => Cursors.Arrow }; InvalidateVisual(); }
    }
    private readonly byte[] _hitPixel = new byte[4];
    private readonly HashSet<ArtworkLayer> _observed = [];
    public ObservableCollection<ArtworkLayer> Layers { get; } = [];
    private Size _documentSize = new(8192, 4096);
    public Size DocumentSize
    {
        get => _documentSize;
        set
        {
            if(!double.IsFinite(value.Width) || !double.IsFinite(value.Height) || value.Width<=0 || value.Height<=0 || !HasFiniteMapping(Viewport ?? new Rect(new Point(),value)))
                throw new ArgumentOutOfRangeException(nameof(value),"Document dimensions must produce a finite positive canvas mapping.");
            if (_documentSize == value) return; CancelGesture();
            if(!HasFiniteMapping(Viewport ?? new Rect(new Point(),value)))throw new ArgumentOutOfRangeException(nameof(value),"The document mapping changed during gesture cancellation.");
            _documentSize = value; InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private Rect? _viewport;
    public Rect? Viewport
    {
        get => _viewport;
        set
        {
            if(!HasFiniteMapping(value ?? new Rect(new Point(),DocumentSize)))throw new ArgumentOutOfRangeException(nameof(value),"Viewport must produce a finite positive canvas mapping.");
            if (_viewport == value) return; CancelGesture();
            if(!HasFiniteMapping(value ?? new Rect(new Point(),DocumentSize)))throw new ArgumentOutOfRangeException(nameof(value),"The viewport mapping changed during gesture cancellation.");
            _viewport = value; InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool _showArtwork = true;
    public bool ShowArtwork
    {
        get => _showArtwork;
        set { if (_showArtwork == value) return; _showArtwork = value; if (!value) CancelGesture(); InvalidateVisual(); }
    }
    public Drawing? BackgroundDrawing { get => _background; set { _background = value; InvalidateVisual(); } }
    public IReadOnlyList<StudioAnchor> Anchors { get; set; } = [];
    private bool _editingEnabled;
    public bool EditingEnabled
    {
        get => _editingEnabled;
        set { if (_editingEnabled == value) return; _editingEnabled = value; if (!value) CancelGesture(); InvalidateVisual(); }
    }
    public bool ShowGuides { get; set; }
    public bool SnapEnabled { get; set; } = false;
    public ArtworkLayer? SelectedLayer
    {
        get => _selected;
        set { if (_selected == value) return; CancelGesture(); _selected = value; InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
    }
    public double Zoom => _zoom;
    private Rect View => Viewport ?? new Rect(new Point(), DocumentSize);
    private static bool Finite(Point point)=>double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static bool Finite(Vector vector)=>double.IsFinite(vector.X) && double.IsFinite(vector.Y);
    private static bool Finite(ArtworkState state)=>double.IsFinite(state.X) && double.IsFinite(state.Y) && double.IsFinite(state.Width) && double.IsFinite(state.Height) && double.IsFinite(state.Rotation);
    private double ScaleFor(Rect view,double zoom)=>Math.Max(.001,Math.Min(Math.Max(1,ActualWidth-24)/view.Width,Math.Max(1,ActualHeight-24)/view.Height)*zoom);
    private Point OriginFor(Rect view,double scale,Vector pan)=>new((ActualWidth-view.Width*scale)/2-view.X*scale+pan.X,(ActualHeight-view.Height*scale)/2-view.Y*scale+pan.Y);
    private bool HasFiniteMapping(Rect view)
    {
        if(view.IsEmpty || !double.IsFinite(view.X) || !double.IsFinite(view.Y) || !double.IsFinite(view.Width) || !double.IsFinite(view.Height) ||
            view.Width<=0 || view.Height<=0 || !double.IsFinite(view.Right) || !double.IsFinite(view.Bottom))return false;
        var scale=ScaleFor(view,_zoom);return double.IsFinite(scale) && scale>0 && Finite(OriginFor(view,scale,_pan));
    }
    public double Scale => ScaleFor(View,_zoom);
    public Point Origin => OriginFor(View,Scale,_pan);
    public Point ToDocument(Point screen) => new((screen.X - Origin.X) / Scale, (screen.Y - Origin.Y) / Scale);
    public Point ToScreen(Point document) => new(document.X * Scale + Origin.X, document.Y * Scale + Origin.Y);
    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler? DeleteRequested;
    public event EventHandler<ArtworkGestureEventArgs>? TransformCommitted;
    /// <summary>Finish dependent edits or veto before capturing the transform baseline.</summary>
    public event EventHandler<CancelEventArgs>? TransformStarting;
    public event Action<Color>? ColorSampled;
    public event EventHandler? TransformPreviewChanged;
    public event EventHandler? TransformPreviewEnded;

    public ArtworkCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Layers.CollectionChanged += OnLayersChanged;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelGesture(); };
        Unloaded += (_, _) => CancelGesture();
        SizeChanged += (_, _) => { CancelGesture(); InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty); };
    }
    public void Fit() { CancelGesture(); _zoom = 1; _pan = new Vector(); InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty); }
    public void ChangeZoom(double factor, Point? center = null)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor == 1) return;
        var target = center ?? new Point(ActualWidth / 2, ActualHeight / 2);
        if(!Finite(target) || !PrepareZoom(factor,target,out var zoom,out var pan))return;
        CancelGesture();
        if(!PrepareZoom(factor,target,out zoom,out pan))return;
        _zoom=zoom;_pan=pan;
        InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool PrepareZoom(double factor,Point target,out double zoom,out Vector pan)
    {
        zoom=_zoom;pan=_pan;
        var basis=Scale/_zoom;if(!double.IsFinite(basis) || basis<=0)return false;
        var nextZoom=Math.Clamp(_zoom*factor,.05/basis,32/basis);
        if(!double.IsFinite(nextZoom) || nextZoom<=0 || nextZoom==_zoom)return false;
        var document=ToDocument(target);if(!Finite(document))return false;
        var view=View;var scale=ScaleFor(view,nextZoom);var origin=OriginFor(view,scale,_pan);
        if(!double.IsFinite(scale) || scale<=0 || !Finite(origin))return false;
        var after=new Point(document.X*scale+origin.X,document.Y*scale+origin.Y);
        var nextPan=_pan+(target-after);
        if(!Finite(after) || !Finite(nextPan) || !Finite(OriginFor(view,scale,nextPan)))return false;
        zoom=nextZoom;pan=nextPan;return true;
    }
    public void ActualSize() => ChangeZoom(1 / Scale);
    private void OnLayersChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var removed in _observed.Where(layer => !Layers.Contains(layer)).ToArray())
        { removed.PropertyChanged -= OnArtworkChanged; _observed.Remove(removed); }
        foreach (var layer in Layers.Where(layer => !_observed.Contains(layer)))
        { layer.PropertyChanged += OnArtworkChanged; _observed.Add(layer); }
        if (_selected is not null && !Layers.Contains(_selected)) SelectedLayer = null;
        InvalidateVisual();
    }
    private void OnArtworkChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_original is not null && ReferenceEquals(sender, _selected) && _selected is { } layer && (!layer.Visible || layer.Opacity <= 0)) CancelGesture();
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(TryFindResource("WorkspaceBrush") as Brush ?? Brushes.LightGray, null, new Rect(RenderSize));
        dc.PushTransform(new TranslateTransform(Origin.X, Origin.Y));
        dc.PushTransform(new ScaleTransform(Scale, Scale));
        dc.PushClip(new RectangleGeometry(View));
        dc.DrawRectangle(TryFindResource("CheckerBrush") as Brush ?? Brushes.White, null, new Rect(new Point(), DocumentSize));
        if (_background is not null) dc.DrawDrawing(_background);
        foreach (var layer in Layers.Where(layer => ShowArtwork && layer.Visible && layer.Image is not null && layer.Opacity > 0))
        {
            dc.PushOpacity(layer.Opacity / 100);
            dc.PushTransform(new RotateTransform(layer.Rotation, layer.Center.X, layer.Center.Y));
            dc.PushTransform(new ScaleTransform(layer.FlipX ? -1 : 1, layer.FlipY ? -1 : 1, layer.Center.X, layer.Center.Y));
            dc.DrawImage(layer.Image, new Rect(layer.X, layer.Y, layer.Width, layer.Height));
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        if (ShowGuides)
        {
            var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(130, 110, 134, 172)), 1 / Scale);
            foreach (var anchor in Anchors)
            {
                var position = anchor.Position;
                dc.DrawLine(guidePen, new Point(position.X - 14 / Scale, position.Y), new Point(position.X + 14 / Scale, position.Y));
                dc.DrawLine(guidePen, new Point(position.X, position.Y - 14 / Scale), new Point(position.X, position.Y + 14 / Scale));
            }
        }
        dc.Pop();
        if (EditingEnabled && ShowArtwork && Tool is ArtworkTool.Move or ArtworkTool.Transform && _selected is { Visible: true, Opacity: > 0, Image: not null } selected && Layers.Contains(selected))
        {
            var pen = new Pen(TryFindResource("AccentBrightBrush") as Brush ?? Brushes.DodgerBlue, 1.5 / Scale);
            dc.PushTransform(new RotateTransform(selected.Rotation, selected.Center.X, selected.Center.Y));
            var rect = new Rect(selected.X, selected.Y, selected.Width, selected.Height);
            dc.DrawRectangle(null, pen, rect);
            foreach (var point in HandlePoints(selected))
                dc.DrawRectangle(Brushes.White, pen, new Rect(point.X - 4 / Scale, point.Y - 4 / Scale, 8 / Scale, 8 / Scale));
            var rotation = new Point(selected.Center.X, selected.Y - 28 / Scale);
            dc.DrawLine(pen, new Point(selected.Center.X, selected.Y), rotation);
            dc.DrawEllipse(Brushes.White, pen, rotation, 5 / Scale, 5 / Scale);
            dc.Pop();
        }
        dc.Pop(); dc.Pop();
    }
    private static Point[] HandlePoints(ArtworkLayer layer) =>
        [new(layer.X, layer.Y), new(layer.X + layer.Width, layer.Y),
         new(layer.X + layer.Width, layer.Y + layer.Height), new(layer.X, layer.Y + layer.Height)];
    public ArtworkLayer? HitArtwork(Point document)
    {
        if (!ShowArtwork || !Finite(document)) return null;
        foreach (var layer in Layers.Reverse().Where(layer => layer.Visible && layer.Image is not null && layer.Opacity > 0))
        {
            var local = TransformGeometry.Rotate(document - layer.Center, -layer.Rotation);
            if(!Finite(local))continue;
            var u = local.X / layer.Width + .5; var v = local.Y / layer.Height + .5;
            if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
            if (layer.FlipX) u = 1 - u; if (layer.FlipY) v = 1 - v;
            if (layer.Image is BitmapSource bitmap && bitmap.Format.BitsPerPixel == 32)
            {
                var x = Math.Clamp((int)(u * bitmap.PixelWidth), 0, bitmap.PixelWidth - 1);
                var y = Math.Clamp((int)(v * bitmap.PixelHeight), 0, bitmap.PixelHeight - 1);
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), _hitPixel, 4, 0);
                if (_hitPixel[3] * layer.Opacity / 100 < 12) continue;
            }
            return layer;
        }
        return null;
    }
    public bool BeginArtworkGesture(Point screen, bool shift = false)
    {
        if(_canceling || _startingTransform || !Finite(screen) || !Finite(ToDocument(screen)))return false;
        CancelGesture(); _start=screen; _dragStarted=false;
        if(!EditingEnabled || !ShowArtwork || !IsEnabled)return false;
        var point=ToDocument(screen);if(!Finite(point))return false;var corner=-1;
        if(_selected is {Visible:true, Opacity:>0, Image:not null} selected && Layers.Contains(selected))
        {
            var local=TransformGeometry.Rotate(point-selected.Center,-selected.Rotation);
            var unrotated=selected.Center+local;
            if((unrotated-new Point(selected.Center.X,selected.Y-28/Scale)).Length*Scale<=10)_gesture="rotate";
            else {var handles=HandlePoints(selected);corner=Array.FindIndex(handles,handle=>(unrotated-handle).Length*Scale<=10);if(corner>=0)_gesture="resize";}
            if(_gesture is null)
            {
                var hit=HitArtwork(point);
                // Visible artwork above the selected layer wins; otherwise its entire rotated box can move.
                if(hit is not null && Layers.IndexOf(hit)>Layers.IndexOf(selected))SelectedLayer=hit;
                else if(Math.Abs(local.X)<=selected.Width/2 && Math.Abs(local.Y)<=selected.Height/2)_gesture="move";
            }
        }
        if(_gesture is null){SelectedLayer=HitArtwork(point);if(_selected is not null)_gesture="move";}
        if(_selected is null || _gesture is null)return false;
        if(!PrepareArtworkTransform())return false;
        _gestureButton=MouseButton.Left;
        _original=_selected.Capture();
        _gestureRenderSize=RenderSize;
        if(_gesture=="resize")_resize=new ArtworkResizeGesture(_original,point,corner,_selected.ScaleLocked ^ shift);
        return true;
    }
    private bool PrepareArtworkTransform()
    {
        if(_startingTransform || !CanEditSelection)return false;
        var layer=_selected;var gesture=_gesture;var args=new CancelEventArgs();
        _startingTransform=true;
        try{TransformStarting?.Invoke(this,args);}
        catch{CancelGesture();throw;}
        finally{_startingTransform=false;}
        if(args.Cancel || !CanEditSelection || !ReferenceEquals(layer,_selected) || gesture!=_gesture)
        {CancelGesture();return false;}
        return true;
    }
    public void ContinueArtworkGesture(Point screen, bool shift=false, bool alt=false, bool control=false)
    {
        if(_original is not { } original || _selected is not { } layer || !Finite(screen))return;
        // WPF can change RenderSize before delivering the deferred SizeChanged event.
        if(_gestureRenderSize!=RenderSize){CancelGesture();return;}
        var point=ToDocument(screen);
        var distance=screen-_start;var documentDelta=point-ToDocument(_start);
        if(!Finite(point) || !Finite(distance) || !Finite(documentDelta) || (_gesture=="resize" && !Finite(TransformGeometry.Rotate(documentDelta,-original.Rotation))))return;
        if(!_dragStarted && Math.Abs(distance.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(distance.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        if(_gesture=="move")
        {
            var delta=documentDelta;var x=original.X+delta.X;var y=original.Y+delta.Y;
            if(!double.IsFinite(x) || !double.IsFinite(y))return;
            if(SnapEnabled && !alt)
            {
                var center=new Point(x+original.Width/2,y+original.Height/2);
                var nearest=Anchors.OrderBy(anchor=>(anchor.Position-center).LengthSquared).FirstOrDefault();
                if(nearest is not null && (nearest.Position-center).Length*Scale<=8){x=nearest.Position.X-original.Width/2;y=nearest.Position.Y-original.Height/2;}
            }
            if(!double.IsFinite(x) || !double.IsFinite(y))return;
            _dragStarted=true;layer.X=x;if(!CurrentGesture(layer,original))return;
            layer.Y=y;
        }
        else if(_gesture=="resize" && _resize is not null)
        {
            var resized=_resize.Update(point,layer.ScaleLocked ^ shift,layer);if(!Finite(resized))return;
            _dragStarted=true;if(!ApplyGestureState(layer,original,resized))return;
        }
        else if(_gesture=="rotate")
        {
            var center=new Point(original.X+original.Width/2,original.Y+original.Height/2);
            if(!Finite(point-center))return;
            var angle=Math.Atan2(point.Y-center.Y,point.X-center.X)*180/Math.PI+90;
            _dragStarted=true;layer.Rotation=control?Math.Round(angle/15)*15:angle;
        }
        else return;
        if(!CurrentGesture(layer,original))return;
        TransformPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    // Value-equal poses can belong to a new gesture started by a property callback.
    private bool CurrentGesture(ArtworkLayer layer, ArtworkState original)
        => ReferenceEquals(_selected, layer) && ReferenceEquals(_original, original);
    private bool ApplyGestureState(ArtworkLayer layer, ArtworkState original, ArtworkState state)
    {
        layer.X=state.X;if(!CurrentGesture(layer,original))return false;
        layer.Y=state.Y;if(!CurrentGesture(layer,original))return false;
        layer.Width=state.Width;if(!CurrentGesture(layer,original))return false;
        layer.Height=state.Height;if(!CurrentGesture(layer,original))return false;
        layer.Rotation=state.Rotation;return CurrentGesture(layer,original);
    }
    public void CommitArtworkGesture()
    {
        if(_original is not null && _gestureRenderSize!=RenderSize){CancelGesture();return;}
        var layer=_selected;var before=_original;var after=layer?.Capture();_original=null;_gesture=null;_gestureButton=null;_resize=null;_dragStarted=false;
        if(IsMouseCaptured)ReleaseMouseCapture();
        if(before is not null)TransformPreviewEnded?.Invoke(this,EventArgs.Empty);
        if(layer is not null && before is not null && after is not null && before!=after)TransformCommitted?.Invoke(this,new(layer,before,after));
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.ChangedButton is not (MouseButton.Left or MouseButton.Middle) || _canceling || _startingTransform)return;
        var screen=e.GetPosition(this);if(!Finite(screen))return;Focus();
        if(e.ChangedButton==MouseButton.Middle || (e.ChangedButton==MouseButton.Left && (Tool==ArtworkTool.Hand || Keyboard.IsKeyDown(Key.Space))))
        {CancelGesture();_start=screen;_gesture="pan";_gestureButton=e.ChangedButton;_panStart=new Point(_pan.X,_pan.Y);CaptureMouse();e.Handled=true;return;}
        if(e.ChangedButton==MouseButton.Left && Tool==ArtworkTool.Zoom){ChangeZoom(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)?1/1.25:1.25,screen);e.Handled=true;return;}
        if(e.ChangedButton==MouseButton.Left && Tool==ArtworkTool.Eyedropper){var color=SampleArtwork(ToDocument(screen));if(color is {A:>0} sample)ColorSampled?.Invoke(sample);e.Handled=true;return;}
        if(e.ChangedButton!=MouseButton.Left)return;
        if(BeginArtworkGesture(screen,Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))CaptureMouse();e.Handled=EditingEnabled;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(_gesture=="pan"){var point=e.GetPosition(this);var delta=point-_start;var pan=new Vector(_panStart.X+delta.X,_panStart.Y+delta.Y);if(!Finite(point) || !Finite(delta) || !Finite(pan) || !Finite(OriginFor(View,Scale,pan)))return;_pan=pan;InvalidateVisual();return;}
        if(_original is null)return;
        ContinueArtworkGesture(e.GetPosition(this),Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),Keyboard.Modifiers.HasFlag(ModifierKeys.Control));e.Handled=true;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {base.OnMouseUp(e);if(_gesture is null || e.ChangedButton!=_gestureButton)return;CommitArtworkGesture();e.Handled=true;}
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); CancelGesture(); }
    public void CancelGesture()
    {
        if(_canceling)return;
        _canceling=true;
        try
        {
            var original=_original;var layer=_selected;
            _original = null; _gesture = null; _gestureButton=null; _resize=null; _dragStarted=false;
            if(original is not null && layer is not null)layer.Restore(original);
            if (IsMouseCaptured) ReleaseMouseCapture();
            if(original is not null)TransformPreviewEnded?.Invoke(this,EventArgs.Empty);
            InvalidateVisual();
        }
        finally { _canceling=false; }
    }
    /// <summary>Sample artwork at document resolution, excluding editor guides and selection handles.</summary>
    public Color? SampleArtwork(Point document)
    {
        if (!Finite(document) || !new Rect(new Point(), DocumentSize).Contains(document)) return null;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new TranslateTransform(-Math.Floor(document.X), -Math.Floor(document.Y)));
            if (_background is not null) drawing.DrawDrawing(_background);
            foreach (var layer in Layers.Where(layer => ShowArtwork && layer.Visible && layer.Image is not null && layer.Opacity > 0))
            {
                drawing.PushOpacity(layer.Opacity / 100); drawing.PushTransform(new RotateTransform(layer.Rotation, layer.Center.X, layer.Center.Y));
                drawing.PushTransform(new ScaleTransform(layer.FlipX ? -1 : 1, layer.FlipY ? -1 : 1, layer.Center.X, layer.Center.Y));
                drawing.DrawImage(layer.Image, new Rect(layer.X, layer.Y, layer.Width, layer.Height)); drawing.Pop(); drawing.Pop(); drawing.Pop();
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixel = new byte[4]; bitmap.CopyPixels(pixel, 4, 0); var alpha = pixel[3];
        return alpha == 0 ? Colors.Transparent : Color.FromArgb(alpha, (byte)Math.Min(255, pixel[2] * 255 / alpha), (byte)Math.Min(255, pixel[1] * 255 / alpha), (byte)Math.Min(255, pixel[0] * 255 / alpha));
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ChangeZoom(Math.Pow(1.2,e.Delta / 120.0), e.GetPosition(this)); e.Handled = true; }
    private bool CanEditSelection => EditingEnabled && IsEnabled && ShowArtwork &&
        _selected is { Visible: true, Opacity: > 0, Image: not null } layer && Layers.Contains(layer);
    private void NudgeArtwork(Vector delta)
    {
        if(_canceling || _startingTransform || !CanEditSelection)return;
        CancelGesture();
        if(!CanEditSelection)return;
        _gesture="nudge";
        if(!PrepareArtworkTransform())return;
        var layer=_selected!;var original=layer.Capture();
        _original=original;_gesture="nudge";_gestureRenderSize=RenderSize;
        if(!ApplyGestureState(layer,original,original with { X=original.X+delta.X, Y=original.Y+delta.Y }))return;
        CommitArtworkGesture();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { CancelGesture(); e.Handled = true; return; }
        if (!CanEditSelection || _canceling) return;
        if (e.Key == Key.Delete) { CancelGesture(); if(CanEditSelection)DeleteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var delta = e.Key switch { Key.Left => new Vector(-step,0), Key.Right => new Vector(step,0), Key.Up => new Vector(0,-step), Key.Down => new Vector(0,step), _ => new Vector() };
        if(delta==new Vector())return;
        NudgeArtwork(delta);e.Handled=true;
    }
}
