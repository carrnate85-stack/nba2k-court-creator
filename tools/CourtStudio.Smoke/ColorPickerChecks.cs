using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckColorPickers(string output)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        Func<StockLayer,bool,string?> choose=(_,_)=>"#123456";
        var calls=0;
        var window=new StudioWindow(true,new PythonServiceClient(),new PythonServiceClient(),
            pickLayerColor:(layer,team)=>{calls++;return choose(layer,team);});
        var blocked=new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true};
        int History(string name)=>((ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
        Grid Row(string id)=>((StackPanel)window.FindName("LayersHost")).Children.OfType<Expander>()
            .SelectMany(group=>((StackPanel)group.Content).Children.OfType<Grid>()).Single(row=>Equals(row.Tag,id));
        Button Action(string mode,string id="paint-left")=>mode=="pinned"?(Button)window.FindName("PinnedColorButton"):
            Descendants<Button>(Row(id)).Single(button=>Equals(button.Tag,(mode=="team"?"TeamColors:":"PickColor:")+id));
        void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Flush()=>typeof(StudioWindow).GetMethod("FlushLayerHex",flags)!.Invoke(window,null);
        void Pump(Task task)
        {
            if(!task.IsCompleted)
            {
                var frame=new DispatcherFrame();
                _=task.ContinueWith(_=>window.Dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        async Task Prepare()
        {
            await window.NewProjectAsync();window.SetLayerSettings("paint-left",visible:true);Flush();Layout(window,1000,680);
            foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
            calls=0;
        }
        try
        {
            await window.InitializeAsync();Layout(window,1000,680);
            Assert(Descendants<Button>(Row("paint-left")).Any(button=>Equals(button.Tag,"TeamColors:paint-left")),
                "Visible paint row has no direct Team Colors action beside its hex field.");
            foreach(var layer in window.PaintLayers.Concat(window.LineLayers).Append((StockLayer)typeof(StudioWindow).GetField("_outside",flags)!.GetValue(window)!))
            {
                window.SetLayerSettings(layer.Id,visible:true);Flush();var row=Row(layer.Id);var before=layer.Color;var undo=History("_undo");
                choose=(target,team)=>{Assert(ReferenceEquals(target,layer)&&team,"Direct palette did not receive its row's exact layer.");return "#A25B36";};
                Click(Action("team",layer.Id));Flush();
                Assert(layer.Color=="#A25B36" && Descendants<TextBox>(row).Single().Text=="#A25B36" && History("_undo")==undo+(before=="#A25B36"?0:1),"Inline palette lost the target, normalized field, or one-edit history.");
                Assert(ReferenceEquals(row,Row(layer.Id)),"Palette application rebuilt the active layer controls.");
            }
            foreach(var mode in new[]{"color","team","pinned"})
            {
                await Prepare();var target=window.PaintLayers.First(layer=>layer.Id=="paint-left");var before=window.CreateProject();var drawing=window.Canvas.BackgroundDrawing;
                choose=(layer,team)=>{Assert(ReferenceEquals(layer,target)&&team==(mode=="team"),"Picker mode/target was incorrect.");return "abc";};
                Click(Action(mode));Flush();
                Assert(calls==1 && target.Color=="#AABBCC" && History("_undo")==1 && !ReferenceEquals(drawing,window.Canvas.BackgroundDrawing),"Accepted color failed to update preview once.");
                await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before.ToJsonString(),"Picker undo lost document data.");
                var retainedRedo=History("_redo");var retainedUndo=History("_undo");
                choose=(_,_)=>target.Color.ToLowerInvariant();Click(Action(mode));Flush();
                Assert(retainedRedo==1&&History("_redo")==retainedRedo&&History("_undo")==retainedUndo,"No-op picker cleared a pending redo action.");
                await window.UndoAsync(true);Assert(target.Color=="#AABBCC","Picker redo failed.");
                var redo=History("_redo");var undo=History("_undo");drawing=window.Canvas.BackgroundDrawing;
                choose=(_,_)=>target.Color.ToLowerInvariant();Click(Action(mode));Flush();
                Assert(History("_undo")==undo&&History("_redo")==redo&&ReferenceEquals(drawing,window.Canvas.BackgroundDrawing),"No-op picker changed preview/history.");
                foreach(var result in new string?[]{null,"invalid","#12"})
                {
                    var state=window.CreateProject().ToJsonString();choose=(_,_)=>result;Click(Action(mode));
                    Assert(window.CreateProject().ToJsonString()==state&&History("_undo")==undo&&History("_redo")==redo,"Canceled/invalid picker mutated the document.");
                }
            }
            foreach(var mode in new[]{"color","team","pinned"})
            foreach(var (name,value) in blocked)
            {
                await Prepare();var action=Action(mode);var state=window.CreateProject().ToJsonString();var field=typeof(StudioWindow).GetField(name,flags)!;var prior=field.GetValue(window);
                choose=(_,_)=>"#654321";field.SetValue(window,value);
                try{Click(action);Assert(calls==0&&window.CreateProject().ToJsonString()==state&&History("_undo")==0,"Blocked picker opened or changed the document: "+mode+" / "+name);}
                finally{field.SetValue(window,prior);}
            }
            foreach(var mode in new[]{"color","team","pinned"})
            foreach(var transition in new[]{"new","open","undo","theme","selection","color","hidden","busy","nested"})
            {
                await Prepare();var action=Action(mode);string? after=null;var undo=0;var redo=0;
                choose=(_,_)=>
                {
                    switch(transition)
                    {
                        case "new":Pump(window.NewProjectAsync());break;
                        case "open":var file=Path.Combine(output,"picker-open.court.json");window.SaveProjectTo(file);Pump(window.OpenProjectFromAsync(file));break;
                        case "undo":window.SetLayerSettings("paint-left",color:"#010203");Pump(window.UndoAsync());break;
                        case "theme":typeof(StudioWindow).GetMethod("ThemeClick",flags)!.Invoke(window,[window,new RoutedEventArgs()]);break;
                        case "selection":window.SetLayerSettings("paint-right",color:"#123123");break;
                        case "color":window.SetLayerSettings("paint-left",color:"#234234");break;
                        case "hidden":window.SetLayerSettings("paint-left",visible:false);break;
                        case "busy":typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,true);break;
                        case "nested":Click(Action("team"));break;
                    }
                    after=window.CreateProject().ToJsonString();undo=History("_undo");redo=History("_redo");return "#654321";
                };
                try
                {
                    Click(action);Flush();
                    if(transition=="nested")Assert(calls==1&&window.PaintLayers.First(layer=>layer.Id=="paint-left").Color=="#654321"&&History("_undo")==1,"Nested picker opened twice or lost outer acceptance.");
                    else if(transition=="theme"&&mode=="pinned")Assert(calls==1&&window.PaintLayers.First(layer=>layer.Id=="paint-left").Color=="#654321"&&History("_undo")==undo+1,"Pinned picker unnecessarily expired after a visual-only rebuild.");
                    else Assert(calls==1&&window.CreateProject().ToJsonString()==after&&History("_undo")==undo&&History("_redo")==redo,"Stale picker applied after "+mode+" / "+transition);
                }
                finally{typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,false);StudioTheme.Apply(false);}
            }
            foreach(var mode in new[]{"color","team","pinned"})
            {
                await Prepare();var old=Action(mode);window.SetLayerSettings("paint-left",visible:false);Flush();var state=window.CreateProject().ToJsonString();var undo=History("_undo");
                Click(old);Assert(calls==0&&window.CreateProject().ToJsonString()==state&&History("_undo")==undo,"Hidden layer opened a picker or changed settings.");
                window.SetLayerSettings("paint-left",visible:true);await window.NewProjectAsync();calls=0;state=window.CreateProject().ToJsonString();undo=History("_undo");
                if(mode!="pinned"){Click(old);Assert(calls==0&&window.CreateProject().ToJsonString()==state&&History("_undo")==undo,"Retired row opened a picker.");}
            }
            await Prepare();var doubleRow=Row("paint-left");var doubleLayer=window.PaintLayers.First(layer=>layer.Id=="paint-left");
            void DoubleClick(UIElement source,MouseButton button=MouseButton.Left)
            {
                var mouse=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,button){RoutedEvent=Mouse.MouseDownEvent};
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(mouse,2);
                source.RaiseEvent(mouse);
            }
            var nameText=doubleRow.Children.OfType<TextBlock>().Single();
            foreach(var source in new UIElement[]{Descendants<TextBox>(doubleRow).Single(),Action("team"),Descendants<TextBlock>(Action("team")).Single()})
            {
                var undo=History("_undo");DoubleClick(source);Assert(doubleLayer.Visible&&History("_undo")==undo,"Double-clicking hex/palette text toggled the layer.");
            }
            DoubleClick(nameText,MouseButton.Right);Assert(doubleLayer.Visible&&History("_undo")==0,"Right double-click toggled the layer.");
            DoubleClick(nameText);Assert(!doubleLayer.Visible&&History("_undo")==1,"Left name double-click no longer toggles the layer.");
            DoubleClick(nameText);Assert(doubleLayer.Visible&&History("_undo")==2,"Left name double-click did not restore the layer.");
            var logoPath=Path.Combine(output,"picker-gesture-logo.png");WriteLogoExample(logoPath);
            foreach(var mode in new[]{"color","team","pinned"})
            foreach(var result in new[]{"cancel","unchanged","invalid"})
            {
                await Prepare();await window.AddLogoAsync(logoPath);window.SwitchSection("logos");Layout(window,1000,680);
                var logo=window.Canvas.Layers.Single();window.Canvas.SelectedLayer=logo;
                var point=window.Canvas.ToScreen(logo.Center);Assert(window.Canvas.BeginArtworkGesture(point),"Picker no-op fixture could not start a drag.");
                window.Canvas.ContinueArtworkGesture(point+new Vector(40,20));
                var before=window.CreateProject().ToJsonString();var undo=History("_undo");var commits=0;
                EventHandler<ArtworkGestureEventArgs> committed=(_,_)=>commits++;window.Canvas.TransformCommitted+=committed;
                choose=(layer,_)=>result switch{"cancel"=>null,"unchanged"=>layer.Color,_=>"invalid"};
                try
                {
                    Click(Action(mode));Assert(window.CreateProject().ToJsonString()==before&&History("_undo")==undo,"Canceled/no-op picker disturbed an active logo drag.");
                    window.Canvas.CommitArtworkGesture();Assert(commits==1&&History("_undo")==undo+1,"Canceled/no-op picker consumed the logo gesture/history.");
                }
                finally{window.Canvas.TransformCommitted-=committed;window.Canvas.CancelGesture();}
            }
            foreach(var mode in new[]{"color","team","pinned"})
            foreach(var transition in new[]{"new","selection","busy"})
            {
                await Prepare();await window.AddLogoAsync(logoPath);window.SwitchSection("logos");Layout(window,1000,680);
                var logo=window.Canvas.Layers.Single();window.Canvas.SelectedLayer=logo;
                var point=window.Canvas.ToScreen(logo.Center);Assert(window.Canvas.BeginArtworkGesture(point),"Picker ownership fixture could not start a drag.");
                window.Canvas.ContinueArtworkGesture(point+new Vector(40,20));
                string? after=null;var undo=0;var redo=0;var ended=0;
                EventHandler cancel=(_,_)=>
                {
                    if(ended++!=0)return;
                    if(transition=="new")Pump(window.NewProjectAsync());
                    else if(transition=="selection")window.SetLayerSettings("paint-right",color:"#321321");
                    else typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,true);
                    after=window.CreateProject().ToJsonString();undo=History("_undo");redo=History("_redo");
                };
                window.Canvas.TransformPreviewEnded+=cancel;choose=(_,_)=>"#654321";
                try
                {
                    Click(Action(mode));Assert(ended==1&&window.CreateProject().ToJsonString()==after&&History("_undo")==undo&&History("_redo")==redo,
                        "Picker applied after gesture cleanup changed its document/target: "+mode+" / "+transition);
                }
                finally{window.Canvas.TransformPreviewEnded-=cancel;typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,false);window.Canvas.CancelGesture();}
            }
            await Prepare();choose=(_,_)=>throw new IOException("Injected color picker failure.");Click(Action("team"));
            Assert(calls==1&&History("_undo")==0,"Picker failure changed history.");choose=(_,_)=>"#123456";Click(Action("team"));Flush();
            Assert(calls==2&&window.PaintLayers.First(layer=>layer.Id=="paint-left").Color=="#123456","Picker failure prevented a later retry.");
            typeof(StudioWindow).GetMethod("SetStatus",flags)!.Invoke(window,["Court ready."]);
            foreach(var size in new[]{(1000,680),(1440,900)})
            {
                Layout(window,size.Item1,size.Item2);
                foreach(var row in ((StackPanel)window.FindName("LayersHost")).Children.OfType<Expander>().SelectMany(group=>((StackPanel)group.Content).Children.OfType<Grid>()))
                {
                    var hex=Descendants<TextBox>(row).Single();var colors=(StackPanel)hex.Parent;if(colors.Visibility!=Visibility.Visible)continue;
                    var action=Action("team",(string)row.Tag);var bounds=new Rect(row.RenderSize);
                    foreach(var control in new FrameworkElement[]{hex,action})Assert(bounds.Contains(control.TransformToAncestor(row).TransformBounds(new Rect(control.RenderSize))),"Compact layer row clipped hex/team colors.");
                    Assert(action.ActualHeight>=26&&action.ActualWidth>=70,"Team Colors hit target is too small.");
                }
                Snapshot(window,Path.Combine(output,$"color-pickers-{size.Item1}.png"),size.Item1,size.Item2);
            }
            Assert(!window.IsVisible,"Color picker checks opened the workspace.");
            await Prepare();string? closedState=null;var closedHistory=0;
            choose=(_,_)=>{window.Close();closedState=window.CreateProject().ToJsonString();closedHistory=History("_undo");return "#654321";};
            Click(Action("pinned"));Assert(window.CreateProject().ToJsonString()==closedState&&History("_undo")==closedHistory,"Picker result wrote to a closed workspace.");
            Console.WriteLine("PASS color pickers: all 23 inline palette targets; swatch/palette/pinned acceptance, undo/redo and no-ops; hidden/blocked/retired rows; New/Open/Undo/theme/selection/color/busy/closed ownership; nested dialog rejection; name-only left double-click; live-drag no-op preservation and post-cleanup ownership; failure/retry; 1000/1440px layout. Dialog results and click counts injected; no native windows opened.");
        }
        finally{StudioTheme.Apply(false);window.Close();}
    }
}
