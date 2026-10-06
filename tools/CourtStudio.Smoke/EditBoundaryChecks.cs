using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckEditBoundaries(string output)
    {
        var failures=new List<string>();var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var window=new StudioWindow(true);var source=new OffscreenKeySource();
        var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();
        var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        var file=Path.Combine(output,"edit-boundary-logo.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using(var stream=File.Create(file))encoder.Save(stream);
        int History(string name)=>((ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
        void Verify(string name,Action check){try{check();}catch(Exception error){failures.Add(name+": "+error.Message);}}
        async Task Prepare()
        {
            await window.NewProjectAsync();await window.AddLogoAsync(file,"Boundary logo");window.SwitchSection("logos");
            var layer=window.Canvas.SelectedLayer!;layer.X=1700;layer.Y=1000;layer.Width=900;layer.Height=700;layer.Rotation=23;
            window.SelectCanvasTool(ArtworkTool.Transform);Layout(window,1440,900);
            foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
        }
        void Opacity()
        {
            typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);
            ((Slider)window.FindName("LogoOpacitySlider")).Value=62.5;
        }
        void Nudge()=>window.Canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
        try
        {
            await window.InitializeAsync();
            foreach(var gesture in new[]{"move","resize","rotate","nudge"})
            {
                await Prepare();var baseline=window.CreateProject().ToJsonString();var layer=window.Canvas.SelectedLayer!;
                Opacity();var opacityOnly=window.CreateProject().ToJsonString();
                if(gesture=="nudge")Nudge();
                else
                {
                    var point=gesture switch
                    {
                        "resize"=>layer.Center+TransformGeometry.Rotate(new Vector(layer.Width/2,layer.Height/2),layer.Rotation),
                        "rotate"=>layer.Center+TransformGeometry.Rotate(new Vector(0,-layer.Height/2-28/window.Canvas.Scale),layer.Rotation),
                        _=>layer.Center,
                    };
                    var start=window.Canvas.ToScreen(point);Assert(window.Canvas.BeginArtworkGesture(start),"Boundary gesture did not start.");
                    window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));window.Canvas.CommitArtworkGesture();
                }
                var after=window.CreateProject().ToJsonString();
                Verify("opacity then "+gesture+" grouping",()=>Assert(History("_undo")==2,"Opacity and transform are not two edits."));
                await window.UndoAsync();Verify("opacity then "+gesture+" transform undo",()=>Assert(window.CreateProject().ToJsonString()==opacityOnly,"Transform undo lost or rewound opacity."));
                await window.UndoAsync();Verify("opacity then "+gesture+" opacity undo",()=>Assert(window.CreateProject().ToJsonString()==baseline,"Opacity undo replayed transform data."));
                await window.UndoAsync(true);Verify("opacity then "+gesture+" opacity redo",()=>Assert(window.CreateProject().ToJsonString()==opacityOnly,"Opacity redo differs."));
                await window.UndoAsync(true);Verify("opacity then "+gesture+" transform redo",()=>Assert(window.CreateProject().ToJsonString()==after,"Transform redo differs."));
            }
            await Prepare();var cancelBefore=window.CreateProject().ToJsonString();Opacity();var cancelOpacity=window.CreateProject().ToJsonString();
            var cancelStart=window.Canvas.ToScreen(window.Canvas.SelectedLayer!.Center);Assert(window.Canvas.BeginArtworkGesture(cancelStart),"Canceled boundary gesture did not start.");
            window.Canvas.ContinueArtworkGesture(cancelStart+new Vector(60,30));window.Canvas.CancelGesture();
            Assert(window.CreateProject().ToJsonString()==cancelOpacity && History("_undo")==1,"Cancel discarded committed opacity or recorded the unfinished transform.");
            await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==cancelBefore,"Canceled transform opacity undo differs.");
            await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==cancelOpacity,"Canceled transform opacity redo differs.");
            foreach(var (name,value) in new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true})
            foreach(var nudge in new[]{false,true})
            {
                await Prepare();Opacity();var before=window.CreateProject().ToJsonString();var state=typeof(StudioWindow).GetField(name,flags)!;var previous=state.GetValue(window);
                state.SetValue(window,value);
                try
                {
                    if(nudge)Nudge();else Assert(!window.Canvas.BeginArtworkGesture(window.Canvas.ToScreen(window.Canvas.SelectedLayer!.Center)),"Read-only window accepted a transform start.");
                    window.Canvas.CommitArtworkGesture();
                    Assert(window.CreateProject().ToJsonString()==before && History("_undo")==0 && typeof(StudioWindow).GetField("_logoOpacityBefore",flags)!.GetValue(window) is not null,"Transform start bypassed the window guard or finalized opacity while blocked.");
                }
                finally{state.SetValue(window,previous);}
            }
            foreach(var nudge in new[]{false,true})
            foreach(var transition in new[]{"veto","select","remove","disable","cancel","nested","throw"})
            {
                var canvas=new ArtworkCanvas{EditingEnabled=true};canvas.Measure(new Size(1200,700));canvas.Arrange(new Rect(0,0,1200,700));
                var first=new ArtworkLayer{Image=image,X=1700,Y=1000,Width=900,Height=700};var second=new ArtworkLayer{Image=image,X=5200,Y=2400,Width=400,Height=400};
                canvas.Layers.Add(first);canvas.Layers.Add(second);canvas.SelectedLayer=first;var pose=first.Capture();var other=second.Capture();var starts=0;var commits=0;
                canvas.TransformCommitted+=(_,_)=>++commits;
                EventHandler<System.ComponentModel.CancelEventArgs> starting=(_,args)=>
                {
                    ++starts;
                    switch(transition)
                    {
                        case "veto":args.Cancel=true;break;
                        case "select":canvas.SelectedLayer=second;break;
                        case "remove":canvas.Layers.Remove(first);break;
                        case "disable":canvas.EditingEnabled=false;break;
                        case "cancel":canvas.CancelGesture();break;
                        case "throw":throw new InvalidOperationException("Injected transform-start failure.");
                        default:
                            Assert(!canvas.BeginArtworkGesture(canvas.ToScreen(first.Center)),"Reentrant transform start was accepted.");
                            canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
                            break;
                    }
                };
                canvas.TransformStarting+=starting;var start=canvas.ToScreen(first.Center);var accepted=false;var threw=false;
                try
                {
                    if(nudge)canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
                    else{accepted=canvas.BeginArtworkGesture(start);canvas.ContinueArtworkGesture(start+new Vector(60,30));canvas.CommitArtworkGesture();}
                }
                catch(InvalidOperationException error) when(error.Message=="Injected transform-start failure."){threw=true;}
                Assert(starts==1 && (transition=="nested"?commits==1:commits==0) && second.Capture()==other &&
                    (transition=="nested" || first.Capture()==pose) && (nudge || accepted==(transition=="nested")) && threw==(transition=="throw"),"Transform-start transition left a stale owner, duplicate edit or stuck gesture: "+transition);
                canvas.TransformStarting-=starting;canvas.EditingEnabled=true;if(!canvas.Layers.Contains(first))canvas.Layers.Add(first);canvas.SelectedLayer=first;
                Assert(canvas.BeginArtworkGesture(canvas.ToScreen(first.Center)),"Transform start did not recover after "+transition);canvas.CancelGesture();
            }
            foreach(var locked in new[]{true,false})
            foreach(var size in new[]{new Size(900,700),new Size(1,32768),new Size(32768,1),new Size(1,1),new Size(32768,32768),new Size(30000,20000)})
            foreach(var axis in new[]{"Width","Height"})
            foreach(var requested in new[]{-10d,0,.25,1,2,1000.123456789,32768,1e10,1e308})
            {
                await Prepare();var layer=window.Canvas.SelectedLayer!;layer.Width=size.Width;layer.Height=size.Height;layer.ScaleLocked=locked;
                typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);
                var input=Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Single(box=>Equals(box.Tag,axis));
                var before=window.CreateProject().ToJsonString();input.Text=requested.ToString("R",CultureInfo.InvariantCulture);
                input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,input,window){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                var scale=Math.Clamp(requested/(axis=="Width"?size.Width:size.Height),Math.Max(1/size.Width,1/size.Height),Math.Min(32768/size.Width,32768/size.Height));
                var expectedWidth=locked?size.Width*scale:axis=="Width"?Math.Clamp(requested,1,32768):size.Width;
                var expectedHeight=locked?size.Height*scale:axis=="Height"?Math.Clamp(requested,1,32768):size.Height;
                var name=$"typed {axis} / lock={locked} / {size.Width}x{size.Height} / {requested:R}";
                Verify(name,()=>Assert(Math.Abs(layer.Width-expectedWidth)<=Math.Max(1,expectedWidth)*1e-12 && Math.Abs(layer.Height-expectedHeight)<=Math.Max(1,expectedHeight)*1e-12 &&
                    layer.X==1700 && layer.Y==1000 && layer.Rotation==23,"Dimensions stretched, exceeded their bounds or moved the logo."));
                if(locked && scale==requested/(axis=="Width"?size.Width:size.Height))Verify(name+" precision",()=>Assert((axis=="Width"?layer.Width:layer.Height)==requested,"An unclamped typed dimension lost stored precision."));
                var after=window.CreateProject().ToJsonString();var changed=after!=before;
                Verify(name+" history",()=>Assert(History("_undo")== (changed?1:0),"Resize did not record exactly one real edit."));
                if(changed)
                {
                    await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Typed boundary undo differs.");
                    await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Typed boundary redo differs.");
                }
            }
            var sizes=new[]{new Size(900,700),new Size(1,32768),new Size(32768,1),new Size(1,1),new Size(32768,32768),new Size(30000,20000)};
            void CheckSize(ArtworkState original,ArtworkState result,bool locked)
            {
                Assert(double.IsFinite(result.Width) && double.IsFinite(result.Height) && result.Width>=1 && result.Width<=32768 && result.Height>=1 && result.Height<=32768,"Shared resize exceeded its finite size limits.");
                if(locked)Assert(Math.Abs(result.Width*original.Height-result.Height*original.Width)<=Math.Max(1,result.Width*original.Height)*1e-12,"Shared locked resize stretched proportions.");
                Assert(result.Rotation==original.Rotation,"Shared resize changed rotation.");
            }
            foreach(var size in sizes)
            foreach(var angle in new[]{0d,23,81})
            foreach(var locked in new[]{true,false})
            foreach(var delta in new[]{new Vector(1e6,1e6),new Vector(-1e6,-1e6),new Vector(1e6,-1e6),new Vector(-1e6,1e6)})
            {
                var original=new ArtworkState(1700,1000,size.Width,size.Height,angle);var center=new Point(original.X+original.Width/2,original.Y+original.Height/2);
                var centered=TransformGeometry.Resize(original,center+TransformGeometry.Rotate(delta,angle),locked);CheckSize(original,centered,locked);
                Assert((new Point(centered.X+centered.Width/2,centered.Y+centered.Height/2)-center).Length<1e-7,"Centered resize lost its center.");
                foreach(var corner in Enumerable.Range(0,4))
                {
                    var sx=corner is 0 or 3?-1:1;var sy=corner is 0 or 1?-1:1;
                    var pointer=center+TransformGeometry.Rotate(new Vector(sx*original.Width/2,sy*original.Height/2),angle);
                    var resize=new ArtworkResizeGesture(original,pointer,corner,locked);
                    var result=resize.Update(pointer+TransformGeometry.Rotate(delta,angle),locked,original);CheckSize(original,result,locked);
                    var anchor=center+TransformGeometry.Rotate(new Vector(-sx*original.Width/2,-sy*original.Height/2),angle);
                    var resultAnchor=new Point(result.X+result.Width/2,result.Y+result.Height/2)+TransformGeometry.Rotate(new Vector(-sx*result.Width/2,-sy*result.Height/2),angle);
                    Assert((resultAnchor-anchor).Length<1e-7,"Bounded rotated resize moved the opposite corner.");
                }
            }
            foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,0,-1,32769})
            foreach(var width in new[]{true,false})
            {
                var rejected=false;try{TransformGeometry.ScaleDimensions(width?value:900,width?700:value,1);}catch(ArgumentOutOfRangeException){rejected=true;}
                Assert(rejected,"Invalid source dimensions were accepted by the shared scale helper.");
            }
            var rejectedScale=false;try{TransformGeometry.ScaleDimensions(900,700,double.NaN);}catch(ArgumentOutOfRangeException){rejectedScale=true;}
            Assert(rejectedScale,"NaN scale was accepted.");
            foreach(var scale in new[]{double.PositiveInfinity,double.NegativeInfinity,double.MaxValue,double.MinValue})
            {var size=TransformGeometry.ScaleDimensions(900,700,scale);Assert(size.Width>=1 && size.Width<=32768 && size.Height>=1 && size.Height<=32768 && Math.Abs(size.Width/size.Height-900d/700)<1e-12,"Extreme scale did not saturate proportionally.");}
        }
        finally{window.Close();}
        Assert(failures.Count==0,"Edit boundary failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS edit boundaries: opacity followed by move/resize/rotate/nudge preserves ordered undo/redo; canceled transforms retain opacity; eight window guards veto drag/nudge without committing opacity; start veto/selection/removal/disable/cancel/reentry/failure and retry are safe; 216 typed width/height cases preserve bounds, proportions/stretch, exact input and only real edits; 720 centered/rotated-corner resize cases preserve bounds/anchors and reject invalid source sizes. All windows remained invisible.");
    }
}
