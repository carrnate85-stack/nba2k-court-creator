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
    private static async Task CheckInspectorEvents(string output)
    {
        var failures=new List<string>();
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var window=new StudioWindow(true);
        var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();
        var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        var file=Path.Combine(output,"inspector-logo.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using(var stream=File.Create(file))encoder.Save(stream);
        void Verify(string name,Action check){try{check();}catch(Exception error){failures.Add(name+": "+error.Message);}}
        void Blur(TextBox input)=>input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,input,window){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        DependencyObject Properties()=>(DependencyObject)window.FindName("LogoProperties");
        TextBox Field(string tag)=>Descendants<TextBox>(Properties()).Single(input=>Equals(input.Tag,tag));
        int History(string name)=>( (ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
        async Task Prepare()
        {
            await window.NewProjectAsync();await window.AddLogoAsync(file,"First");await window.AddLogoAsync(file,"Second");
            window.Canvas.Layers[0].X=1400;window.Canvas.Layers[0].Y=1000;window.Canvas.Layers[1].X=5200;window.Canvas.Layers[1].Y=2400;
            window.SelectCanvasTool(ArtworkTool.Transform);
            window.Canvas.SelectedLayer=window.Canvas.Layers[0];Layout(window,1440,900);
            foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
        }
        try
        {
            await window.InitializeAsync();
            foreach(var transition in new[]{"select another","rebuild same logo","select away and back","theme rebuild","new document","undo"})
            foreach(var action in new[]{"X","Y","Width","Height","Rotation","lock"})
            {
                await Prepare();var first=window.Canvas.SelectedLayer!;
                var oldFields=Descendants<TextBox>(Properties()).ToArray();
                var oldLock=Descendants<Button>(Properties()).Single(button=>button.Content is Viewbox);
                switch(transition)
                {
                    case "select another":window.Canvas.SelectedLayer=window.Canvas.Layers[1];break;
                    case "rebuild same logo":window.FlipSelectedLogo();break;
                    case "select away and back":window.Canvas.SelectedLayer=window.Canvas.Layers[1];window.Canvas.SelectedLayer=first;break;
                    case "theme rebuild":typeof(StudioWindow).GetMethod("ThemeClick",flags)!.Invoke(window,[window,new RoutedEventArgs()]);break;
                    case "new document":await window.NewProjectAsync();await window.AddLogoAsync(file,"New");window.SwitchSection("logos");break;
                    default:window.FlipSelectedLogo();await window.UndoAsync();window.SwitchSection("logos");break;
                }
                var active=window.Canvas.SelectedLayer!;active.X=1400;active.Y=1000;active.Width=900;active.Height=700;
                foreach(var other in window.Canvas.Layers.Where(layer=>!ReferenceEquals(layer,active))){other.X=5200;other.Y=2400;}
                Layout(window,1440,900);
                var pointer=window.Canvas.ToScreen(active.Center);Assert(window.Canvas.BeginArtworkGesture(pointer),"Fixture could not begin a drag.");
                window.Canvas.ContinueArtworkGesture(pointer+new Vector(60,30));
                var before=window.CreateProject().ToJsonString();var oldPose=first.Capture();var oldLockValue=first.ScaleLocked;
                var undo=History("_undo");var redo=History("_redo");var commits=0;
                EventHandler<ArtworkGestureEventArgs> committed=(_,_)=>++commits;window.Canvas.TransformCommitted+=committed;
                try
                {
                    if(action=="lock")oldLock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else
                    {
                        var input=oldFields.Single(box=>box.Tag is string label && (action=="Rotation"?label.StartsWith("Rotation",StringComparison.Ordinal):label==action));
                        input.Text=action=="Rotation"?"45":"8888.123456";Blur(input);
                    }
                    Verify(transition+" / "+action,()=>Assert(window.CreateProject().ToJsonString()==before && first.Capture()==oldPose && first.ScaleLocked==oldLockValue &&
                        ReferenceEquals(window.Canvas.SelectedLayer,active) && History("_undo")==undo && History("_redo")==redo,"Expired inspector event changed the document, selection or history."));
                    window.Canvas.CommitArtworkGesture();
                    Verify(transition+" / "+action+" drag",()=>Assert(commits==1,"Expired inspector event canceled/committed the active drag."));
                }
                finally{window.Canvas.TransformCommitted-=committed;window.Canvas.CancelGesture();}
            }
            var blocked=new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true};
            foreach(var (name,value) in blocked)
            {
                await Prepare();var selected=window.Canvas.SelectedLayer!;var slider=(Slider)window.FindName("LogoOpacitySlider");
                var before=window.CreateProject().ToJsonString();var undo=History("_undo");var redo=History("_redo");
                var state=typeof(StudioWindow).GetField(name,flags)!;var previous=state.GetValue(window);state.SetValue(window,value);
                try
                {
                    slider.Value=17;
                    Verify("blocked opacity / "+name,()=>Assert(window.CreateProject().ToJsonString()==before && History("_undo")==undo && History("_redo")==redo &&
                        ReferenceEquals(window.Canvas.SelectedLayer,selected) && Equals(state.GetValue(window),value),"Opacity bypassed the document guard or changed readiness/history."));
                }
                finally{state.SetValue(window,previous);}
            }
            foreach(var (name,value) in blocked)
            foreach(var action in new[]{"X","lock","name","paint"})
            {
                await Prepare();var selected=window.Canvas.SelectedLayer!;var before=window.CreateProject().ToJsonString();
                var undo=History("_undo");var redo=History("_redo");var state=typeof(StudioWindow).GetField(name,flags)!;var previous=state.GetValue(window);
                var input=action=="name"?Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(box=>ReferenceEquals(box.Tag,selected)):Field("X");
                var button=action=="lock"?Descendants<Button>(Properties()).Single(item=>item.Content is Viewbox):null;
                state.SetValue(window,value);
                try
                {
                    if(button is not null)button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if(action=="paint")window.SetLayerSettings(window.PaintLayers[0].Id,color:"#123456");
                    else{input.Text=action=="name"?"Blocked rename":"8888.123456";Blur(input);}
                    Verify("blocked "+action+" / "+name,()=>Assert(window.CreateProject().ToJsonString()==before && History("_undo")==undo && History("_redo")==redo &&
                        ReferenceEquals(window.Canvas.SelectedLayer,selected) && Equals(state.GetValue(window),value),"Inspector edit bypassed the document guard."));
                }
                finally{state.SetValue(window,previous);}
            }
            await Prepare();
            var opacity=(Slider)window.FindName("LogoOpacitySlider");var beforeStale=window.CreateProject().ToJsonString();var oldHistory=History("_undo");
            opacity.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(80,17,Slider.ValueChangedEvent));
            Verify("obsolete slider event",()=>Assert(window.CreateProject().ToJsonString()==beforeStale && History("_undo")==oldHistory && opacity.Value==window.Canvas.SelectedLayer!.Opacity,"Obsolete value event overwrote the current opacity."));

            await Prepare();var original=window.Canvas.Layers[0];var second=window.Canvas.Layers[1];original.Opacity=80;second.Opacity=45;
            window.Canvas.SelectedLayer=second;window.Canvas.SelectedLayer=original;
            var start=window.Canvas.ToScreen(original.Center);window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));
            EventHandler select=(_,_)=>window.Canvas.SelectedLayer=second;
            window.Canvas.TransformPreviewEnded+=select;
            try{opacity.Value=17;}
            finally{window.Canvas.TransformPreviewEnded-=select;}
            Verify("opacity cancellation changes selection",()=>Assert(original.Opacity==80 && second.Opacity==45 && opacity.Value==45 && ReferenceEquals(window.Canvas.SelectedLayer,second),"Opacity wrote to the prior selection after cancellation changed its owner."));
            foreach(var action in new[]{"X","lock"})
            {
                await Prepare();original=window.Canvas.SelectedLayer!;second=window.Canvas.Layers[1];
                var input=Field("X");var button=action=="X"?null:Descendants<Button>(Properties()).Single(item=>item.Content is Viewbox);
                var originalPose=original.Capture();var originalLock=original.ScaleLocked;var secondPose=second.Capture();var countBefore=History("_undo");
                start=window.Canvas.ToScreen(original.Center);Assert(window.Canvas.BeginArtworkGesture(start),"Cancellation-owner fixture failed to begin a drag.");window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));
                EventHandler changeOwner=(_,_)=>window.Canvas.SelectedLayer=second;window.Canvas.TransformPreviewEnded+=changeOwner;
                try{if(button is not null)button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));else{input.Text="8888.123456";Blur(input);}}
                finally{window.Canvas.TransformPreviewEnded-=changeOwner;}
                Assert(original.Capture()==originalPose && second.Capture()==secondPose && original.ScaleLocked==originalLock && History("_undo")==countBefore && ReferenceEquals(window.Canvas.SelectedLayer,second),"Current "+action+" edit applied after cancellation changed inspector ownership.");
            }

            await Prepare();var current=window.Canvas.SelectedLayer!;current.X=1700.123456789;var x=Field("X");
            var exact=current.X;typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);x=Field("X");Blur(x);
            Assert(current.X==exact,"Untouched displayed decimals overwrote precision.");
            var beforeEdit=window.CreateProject().ToJsonString();x.Text="1800.987654321";Blur(x);var afterEdit=window.CreateProject().ToJsonString();
            Assert(current.X==1800.987654321,"Current typed edit failed.");await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==beforeEdit,"Typed edit undo differs.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==afterEdit,"Typed edit redo differs.");

            await Prepare();current=window.Canvas.SelectedLayer!;var beforeOpacity=window.CreateProject().ToJsonString();var count=History("_undo");
            typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);
            for(var i=0;i<50;i++)opacity.Value=30+i*.5;
            Assert(current.Opacity==54.5 && History("_undo")==count,"Normal opacity preview failed or generated repeated history.");
            typeof(StudioWindow).GetMethod("FinishLogoOpacity",flags)!.Invoke(window,null);var afterOpacity=window.CreateProject().ToJsonString();
            Assert(History("_undo")==count+1,"Opacity gesture did not create one history entry.");
            await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==beforeOpacity,"Opacity undo differs.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==afterOpacity,"Opacity redo differs.");
            await Prepare();var noOpBefore=window.CreateProject().ToJsonString();
            typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);opacity.Value=62.5;
            var noOpAfter=window.CreateProject().ToJsonString();var paint=window.PaintLayers[0];
            for(var i=0;i<100;i++)window.SetLayerSettings(paint.Id,visible:paint.Visible,color:paint.Color);
            Assert(window.CreateProject().ToJsonString()==noOpAfter && History("_undo")==0 && typeof(StudioWindow).GetField("_logoOpacityBefore",flags)!.GetValue(window) is not null,"No-op paint settings interrupted or committed opacity.");
            typeof(StudioWindow).GetMethod("FinishLogoOpacity",flags)!.Invoke(window,null);await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==noOpBefore,"Opacity undo after no-op paint differs.");
            var keySource=new OffscreenKeySource();
            foreach(var blockedEscape in blocked.Keys.Append("normal"))
            {
                await Prepare();var escapeBefore=window.CreateProject().ToJsonString();
                typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);opacity.Value=62.5;var escapePreview=window.CreateProject().ToJsonString();
                var state=blockedEscape=="normal"?null:typeof(StudioWindow).GetField(blockedEscape,flags)!;var previous=state?.GetValue(window);
                if(state is not null)state.SetValue(window,blocked[blockedEscape]);
                var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                try
                {
                    opacity.RaiseEvent(escape);
                    Assert(window.CreateProject().ToJsonString()==(state is null?escapeBefore:escapePreview) && History("_undo")==0 && History("_redo")==0,"Opacity Escape bypassed readiness or failed to restore the baseline without history.");
                    if(state is null)Assert(escape.Handled && opacity.Value==100 && typeof(StudioWindow).GetField("_logoOpacityBefore",flags)!.GetValue(window) is null,"Normal opacity Escape did not end the preview and restore its readout.");
                }
                finally{if(state is not null)state.SetValue(window,previous);}
            }
            foreach(var action in new[]{"copy","delete","flip","reorder","typed field","project rename","paint color"})
            {
                await Prepare();var baseline=window.CreateProject().ToJsonString();
                typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);opacity.Value=62.5;
                var opacityOnly=window.CreateProject().ToJsonString();
                switch(action)
                {
                    case "copy":((Button)window.FindName("DuplicateLogoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));break;
                    case "delete":window.DeleteSelectedLogo();break;
                    case "flip":window.FlipSelectedLogo();break;
                    case "reorder":((Button)window.FindName("MoveLogoDownButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));break;
                    case "typed field":var input=Field("X");input.Text="1800.987654321";Blur(input);break;
                    case "paint color":window.SetLayerSettings(window.PaintLayers[0].Id,color:"#123456");break;
                    default:window.RenameProject("Opacity then rename");break;
                }
                var afterCommand=window.CreateProject().ToJsonString();
                Verify("opacity then "+action+" grouping",()=>Assert(History("_undo")==2,"Opacity and the command did not produce two ordered edits."));
                await window.UndoAsync();Verify("opacity then "+action+" command undo",()=>Assert(window.CreateProject().ToJsonString()==opacityOnly,"Command undo discarded/interleaved opacity."));
                await window.UndoAsync();Verify("opacity then "+action+" opacity undo",()=>Assert(window.CreateProject().ToJsonString()==baseline,"Opacity undo changed another command's data."));
                await window.UndoAsync(true);Verify("opacity then "+action+" opacity redo",()=>Assert(window.CreateProject().ToJsonString()==opacityOnly,"Opacity redo differs."));
                await window.UndoAsync(true);Verify("opacity then "+action+" command redo",()=>Assert(window.CreateProject().ToJsonString()==afterCommand,"Command redo differs."));
            }
            await Prepare();var liveBefore=window.CreateProject().ToJsonString();
            typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);opacity.Value=62.5;var liveAfter=window.CreateProject().ToJsonString();
            await window.UndoAsync();Verify("undo during opacity",()=>Assert(window.CreateProject().ToJsonString()==liveBefore && History("_undo")==0 && History("_redo")==1,"Undo peeked at history before finalizing opacity."));
            await window.UndoAsync(true);Verify("redo opacity after live undo",()=>Assert(window.CreateProject().ToJsonString()==liveAfter,"Redo lost the finished opacity edit."));

            await Prepare();var nameOwner=window.Canvas.Layers[0];var secondOwner=window.Canvas.Layers[1];
            var nameInput=Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(input=>ReferenceEquals(input.Tag,nameOwner));
            var parent=(Panel)VisualTreeHelper.GetParent(nameInput);parent.Children.Remove(nameInput);nameInput.Tag=nameOwner;nameInput.DataContext=nameOwner;window.Canvas.SelectedLayer=secondOwner;
            var namesBefore=window.CreateProject().ToJsonString();var nameHistory=History("_undo");
            nameInput.Text="Late rename";nameInput.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,window,nameInput){RoutedEvent=Keyboard.GotKeyboardFocusEvent});Blur(nameInput);
            Verify("detached name editor",()=>Assert(window.CreateProject().ToJsonString()==namesBefore && History("_undo")==nameHistory && ReferenceEquals(window.Canvas.SelectedLayer,secondOwner),"Detached name control renamed/selected its previous owner."));
            await Prepare();nameOwner=window.Canvas.SelectedLayer!;
            nameInput=Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(input=>ReferenceEquals(input.Tag,nameOwner));
            var renameBefore=window.CreateProject().ToJsonString();
            nameInput.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,window,nameInput){RoutedEvent=Keyboard.GotKeyboardFocusEvent});
            nameInput.Text="  Revised logo  ";Blur(nameInput);var renameAfter=window.CreateProject().ToJsonString();
            Assert(nameOwner.Name=="Revised logo" && History("_undo")==1,"Current owned name editor did not rename once.");
            await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==renameBefore,"Logo-name undo differs.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==renameAfter,"Logo-name redo differs.");
        }
        finally{StudioTheme.Apply(false);window.Close();}
        Assert(failures.Count==0,"Inspector event failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS logo inspector ownership: 36 stale controls cannot mutate or interrupt a newer drag; numeric/lock/name/paint/opacity edits respect eight guarded states and cancellation-time ownership; obsolete slider events and detached names are ignored; precise current edits, opacity followed by seven commands, live-opacity undo/redo, Escape cancellation and no-op colors preserve ordered history. All windows remained invisible.");
    }
}
