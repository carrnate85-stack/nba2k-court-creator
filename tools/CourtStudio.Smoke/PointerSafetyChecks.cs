using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwoK.Studio;

internal static partial class Program
{
    private static void CheckPointerSafety()
    {
        var failures=new List<string>();var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();
        var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        (ArtworkCanvas Canvas,ArtworkLayer Layer) Fixture()
        {
            var canvas=new ArtworkCanvas{EditingEnabled=true};canvas.Measure(new Size(1200,700));canvas.Arrange(new Rect(0,0,1200,700));
            var layer=new ArtworkLayer{Image=image,X=1700,Y=1000,Width=900,Height=700,Rotation=23};canvas.Layers.Add(layer);canvas.SelectedLayer=layer;return(canvas,layer);
        }
        Point Start(ArtworkCanvas canvas,ArtworkLayer layer,string gesture)=>canvas.ToScreen(gesture switch
        {
            "resize"=>layer.Center+TransformGeometry.Rotate(new Vector(layer.Width/2,layer.Height/2),layer.Rotation),
            "rotate"=>layer.Center+TransformGeometry.Rotate(new Vector(0,-layer.Height/2-28/canvas.Scale),layer.Rotation),
            _=>layer.Center,
        });
        void Verify(string name,Action check){try{check();}catch(Exception error){failures.Add(name+": "+error.Message);}}
        void MouseEvent(ArtworkCanvas canvas,MouseButton button,RoutedEvent routed)=>canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button){RoutedEvent=routed});
        foreach(var gesture in new[]{"move","resize","rotate"})
        foreach(var button in new[]{MouseButton.Right,MouseButton.Middle,MouseButton.XButton1,MouseButton.XButton2})
        Verify(gesture+" / unrelated release "+button,()=>
        {
            var(canvas,layer)=Fixture();var ended=0;var commits=0;canvas.TransformPreviewEnded+=(_,_)=>++ended;canvas.TransformCommitted+=(_,_)=>++commits;
            var start=Start(canvas,layer,gesture);Assert(canvas.BeginArtworkGesture(start),"Gesture did not start.");canvas.ContinueArtworkGesture(start+new Vector(60,30));var preview=layer.Capture();
            MouseEvent(canvas,button,Mouse.MouseUpEvent);
            Assert(layer.Capture()==preview && ended==0 && commits==0,"Unrelated release ended or committed the gesture.");
            canvas.ContinueArtworkGesture(start+new Vector(90,50));MouseEvent(canvas,MouseButton.Left,Mouse.MouseUpEvent);MouseEvent(canvas,MouseButton.Left,Mouse.MouseUpEvent);
            Assert(layer.Capture()!=preview && ended==1 && commits==1,"Owning release did not commit exactly once.");
        });
        foreach(var gesture in new[]{"move","resize","rotate"})
        foreach(var button in new[]{MouseButton.Right,MouseButton.XButton1,MouseButton.XButton2})
        Verify(gesture+" / unrelated press "+button,()=>
        {
            var(canvas,layer)=Fixture();var(control,expected)=Fixture();var start=Start(canvas,layer,gesture);var controlStart=Start(control,expected,gesture);
            Assert(canvas.BeginArtworkGesture(start) && control.BeginArtworkGesture(controlStart),"Gesture did not start.");
            canvas.ContinueArtworkGesture(start+new Vector(60,30));control.ContinueArtworkGesture(controlStart+new Vector(60,30));
            MouseEvent(canvas,button,Mouse.MouseDownEvent);
            canvas.ContinueArtworkGesture(start+new Vector(90,50));control.ContinueArtworkGesture(controlStart+new Vector(90,50));
            Assert(layer.Capture()==expected.Capture(),"Ignored mouse press rebased or replaced the current gesture.");canvas.CancelGesture();control.CancelGesture();
        });
        foreach(var owner in new[]{MouseButton.Middle,MouseButton.Left})
        Verify("pan button ownership / "+owner,()=>
        {
            var(canvas,_)=Fixture();canvas.Tool=owner==MouseButton.Left?ArtworkTool.Hand:ArtworkTool.Move;
            MouseEvent(canvas,owner,Mouse.MouseDownEvent);
            var gesture=typeof(ArtworkCanvas).GetField("_gesture",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            Assert(Equals(gesture.GetValue(canvas),"pan"),"Synthetic pan did not begin.");
            foreach(var other in new[]{MouseButton.Left,MouseButton.Right,MouseButton.Middle,MouseButton.XButton1,MouseButton.XButton2}.Where(button=>button!=owner))
            {MouseEvent(canvas,other,Mouse.MouseUpEvent);Assert(Equals(gesture.GetValue(canvas),"pan"),"Unrelated release ended pan.");}
            MouseEvent(canvas,owner,Mouse.MouseUpEvent);Assert(gesture.GetValue(canvas) is null,"Owning pan release did not finish.");
        });
        var invalidPoints=new[]{new Point(double.NaN,10),new Point(10,double.NaN),new Point(double.PositiveInfinity,10),new Point(10,double.NegativeInfinity),new Point(double.MaxValue,double.MaxValue)};
        foreach(var gesture in new[]{"move","resize","rotate"})
        foreach(var point in invalidPoints)
        foreach(var operation in new[]{"begin","continue","zoom","hit","sample"})
        Verify(gesture+" / "+operation+" / "+point,()=>
        {
            var(canvas,layer)=Fixture();var(control,expected)=Fixture();var start=Start(canvas,layer,gesture);var controlStart=Start(control,expected,gesture);var ended=0;var committed=0;
            canvas.TransformPreviewEnded+=(_,_)=>++ended;canvas.TransformCommitted+=(_,_)=>++committed;
            canvas.BeginArtworkGesture(start);control.BeginArtworkGesture(controlStart);canvas.ContinueArtworkGesture(start+new Vector(60,30));control.ContinueArtworkGesture(controlStart+new Vector(60,30));
            var pose=layer.Capture();var zoom=canvas.Zoom;var scale=canvas.Scale;var origin=canvas.Origin;var events=0;canvas.ViewChanged+=(_,_)=>++events;
            switch(operation)
            {
                case "begin":Assert(!canvas.BeginArtworkGesture(point),"Invalid start was accepted.");break;
                case "continue":canvas.ContinueArtworkGesture(point);break;
                case "zoom":canvas.ChangeZoom(1.25,point);break;
                case "hit":Assert(canvas.HitArtwork(point) is null,"Invalid hit point selected artwork.");break;
                default:Assert(canvas.SampleArtwork(point) is null,"Invalid sample point produced a color.");break;
            }
            Assert(layer.Capture()==pose && canvas.Zoom==zoom && canvas.Scale==scale && canvas.Origin==origin && events==0 && ended==0 && committed==0 && ReferenceEquals(canvas.SelectedLayer,layer),"Rejected pointer input changed the view, selection, history or active drag.");
            canvas.ContinueArtworkGesture(start+new Vector(90,50));control.ContinueArtworkGesture(controlStart+new Vector(90,50));Assert(layer.Capture()==expected.Capture(),"Rejected pointer input changed the next valid drag update.");
            canvas.CommitArtworkGesture();Assert(ended==1 && committed==1,"Valid gesture did not commit after a rejected pointer input.");control.CancelGesture();
        });
        foreach(var gesture in new[]{"move","resize","rotate"})
        Verify("invalid update before threshold / "+gesture,()=>
        {
            var(canvas,layer)=Fixture();var start=Start(canvas,layer,gesture);var before=layer.Capture();var commits=0;canvas.TransformCommitted+=(_,_)=>++commits;
            canvas.BeginArtworkGesture(start);foreach(var point in invalidPoints)canvas.ContinueArtworkGesture(point);
            canvas.ContinueArtworkGesture(start+new Vector(SystemParameters.MinimumHorizontalDragDistance/2,SystemParameters.MinimumVerticalDragDistance/2));
            canvas.CommitArtworkGesture();Assert(layer.Capture()==before && commits==0,"Rejected pointer input bypassed the drag threshold or generated a click-only edit.");
        });
        var invalidSizes=new[]{Size.Empty,new Size(0,4096),new Size(8192,0),new Size(double.NaN,4096),new Size(8192,double.PositiveInfinity),new Size(double.Epsilon,double.Epsilon)};
        var invalidViews=new[]{Rect.Empty,new Rect(0,0,0,4096),new Rect(0,0,8192,0),new Rect(double.NaN,0,4096,4096),new Rect(0,double.PositiveInfinity,4096,4096),new Rect(0,0,double.NaN,4096),new Rect(0,0,4096,double.PositiveInfinity),new Rect(0,0,double.Epsilon,double.Epsilon),new Rect(double.MaxValue,0,double.MaxValue,4096)};
        foreach(var size in invalidSizes)CheckInvalidView("document / "+size,canvas=>canvas.DocumentSize=size);
        foreach(var view in invalidViews)CheckInvalidView("viewport / "+view,canvas=>canvas.Viewport=view);
        Verify("viewport revalidation after cancellation",()=>
        {
            var(canvas,layer)=Fixture();var before=layer.Capture();var start=Start(canvas,layer,"move");canvas.BeginArtworkGesture(start);canvas.ContinueArtworkGesture(start+new Vector(60,30));
            var ended=0;var commits=0;canvas.TransformCommitted+=(_,_)=>++commits;
            canvas.TransformPreviewEnded+=(_,_)=>{++ended;canvas.ChangeZoom(double.MaxValue,new Point(410,250));};
            var rejected=false;try{canvas.Viewport=new Rect(double.MaxValue/4,0,4096,4096);}catch(ArgumentOutOfRangeException){rejected=true;}
            Assert(rejected && canvas.Viewport is null && layer.Capture()==before && ended==1 && commits==0 && double.IsFinite(canvas.Scale) && double.IsFinite(canvas.Origin.X) && double.IsFinite(canvas.Origin.Y),"A mapping validated before cancellation became non-finite after its callback.");
        });
        void CheckInvalidView(string name,Action<ArtworkCanvas> change)=>Verify(name,()=>
        {
            var(canvas,layer)=Fixture();var start=Start(canvas,layer,"move");canvas.BeginArtworkGesture(start);canvas.ContinueArtworkGesture(start+new Vector(60,30));
            var pose=layer.Capture();var document=canvas.DocumentSize;var viewport=canvas.Viewport;var scale=canvas.Scale;var origin=canvas.Origin;var ended=0;var events=0;
            canvas.TransformPreviewEnded+=(_,_)=>++ended;canvas.ViewChanged+=(_,_)=>++events;
            var rejected=false;try{change(canvas);}catch(ArgumentOutOfRangeException){rejected=true;}
            Assert(rejected && canvas.DocumentSize==document && canvas.Viewport==viewport && canvas.Scale==scale && canvas.Origin==origin && layer.Capture()==pose && ended==0 && events==0,"Malformed view replaced the mapping or canceled the drag before rejection.");canvas.CancelGesture();
        });
        Verify("valid fractional viewport and zoom",()=>
        {
            var(canvas,_)=Fixture();var viewport=new Rect(.25,.5,4096.5,2048.25);canvas.Viewport=viewport;
            Assert(canvas.Viewport==viewport && double.IsFinite(canvas.Scale) && double.IsFinite(canvas.Origin.X) && double.IsFinite(canvas.Origin.Y),"Valid fractional viewport was rejected/corrupted.");
            var center=new Point(410,250);var document=canvas.ToDocument(center);canvas.ChangeZoom(double.MaxValue,center);Assert((canvas.ToDocument(center)-document).Length<1e-7 && canvas.Scale<=32,"Clamped zoom lost its pointer center.");
            canvas.Viewport=null;canvas.Fit();Assert(canvas.Viewport is null && canvas.DocumentSize==new Size(8192,4096),"Full-court view did not recover.");
        });
        Assert(failures.Count==0,"Pointer safety failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS pointer safety: move/resize/rotate and middle/left pan finish only on their owning release; ignored presses retain the drag origin; invalid start/update/zoom/hit/sample coordinates and malformed document/viewport mappings preserve a current gesture; valid fractional views and pointer-centered clamped zoom recover. Synthetic routed events only; no native windows opened.");
    }
}
