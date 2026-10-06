using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckLiveResize(StudioWindow window,string output)
    {
        await window.NewProjectAsync();window.SwitchSection("logos");((Expander)window.FindName("LogoDetailsExpander")).IsExpanded=true;Layout(window,1440,900);
        foreach(var (layoutWidth,layoutHeight,sidebar) in new[]{(1440,900,400d),(1000,680,400d),(1440,900,400d)})
        {
            Layout(window,layoutWidth,layoutHeight);var column=(ColumnDefinition)window.FindName("InspectorWidth");column.Width=new GridLength(sidebar);((FrameworkElement)window.Content).UpdateLayout();
            var root=(Visual)window.Content;var group=(FrameworkElement)window.FindName("ZoomControls");var card=(FrameworkElement)window.FindName("SelectedCourtCard");var tools=group.TransformToAncestor(root).TransformBounds(new Rect(group.RenderSize));var panel=card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
            Assert(Math.Abs((tools.Left+tools.Right)/2-(panel.Left+panel.Right)/2)<1,"Navigation group is not centered over the actual inspector.");
            Assert(tools.Left>panel.Left+20 && tools.Right<panel.Right-20,"Navigation group lacks clean sidebar inset.");
            card.Visibility=Visibility.Collapsed;((FrameworkElement)window.FindName("Inspector")).Visibility=Visibility.Collapsed;((FrameworkElement)window.Content).UpdateLayout();var reserved=group.TransformToAncestor(root).TransformBounds(new Rect(group.RenderSize));Assert(reserved==tools,"Hiding panels moved the reserved navigation group.");
            card.Visibility=Visibility.Visible;((FrameworkElement)window.FindName("Inspector")).Visibility=Visibility.Visible;
        }
        Layout(window,1440,900);
        Assert(!window.Canvas.SnapEnabled && !window.Canvas.ShowGuides,"Guides/snapping remain enabled.");
        var source=Path.Combine(output,"live-resize-logo.png");WriteLogoExample(source);var cleaned=new LogoCleanupImage(StudioImages.Load(source));cleaned.RemoveBackground(Colors.White,0);cleaned.WritePng(source);
        for(var i=0;i<4;i++)await window.AddLogoAsync(source,$"Live logo {i+1}");
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var frame=typeof(StudioWindow).GetMethod("LiveLogoFieldsFrame",flags)!;
        var undo=(List<JsonObject>)typeof(StudioWindow).GetField("_undo",flags)!.GetValue(window)!;
        bool Pending()=>(bool)typeof(StudioWindow).GetField("_liveLogoFieldsPending",flags)!.GetValue(window)!;
        void Flush()=>frame.Invoke(window,[null,EventArgs.Empty]);
        TextBox Input(string tag)=>Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Single(box=>Equals(box.Tag,tag));
        void AssertFields(ArtworkLayer layer)
        {foreach(var (key,value) in new[]{("X",layer.X),("Y",layer.Y),("Width",layer.Width),("Height",layer.Height)})Assert(Input(key).Text==value.ToString("0.##",CultureInfo.InvariantCulture),$"Live {key} is stale.");}
        int frames=0,updates=0,textChanges=0;var frameMs=0d;
        foreach(var (rotation,zoom,locked) in new[]{(0d,1d,true),(37d,2.5,true),(81d,.7,false)})
        {
            var layer=window.Canvas.Layers[0];layer.X=2400.123456789;layer.Y=1400.987654321;layer.Width=1800.123456789;layer.Height=1200.876543219;layer.Rotation=rotation;layer.ScaleLocked=locked;
            window.Canvas.SelectedLayer=layer;window.SwitchSection("paint");window.SwitchSection("logos");Layout(window,1440,900);window.Canvas.Fit();window.Canvas.ChangeZoom(zoom);window.Canvas.Focus();
            var focusBefore = Keyboard.FocusedElement;
            if (_allowNativeWindows) Assert(ReferenceEquals(focusBefore, window.Canvas), "Canvas did not acquire native keyboard focus.");
            var oldInputs=new[]{Input("X"),Input("Y"),Input("Width"),Input("Height")};foreach(var input in oldInputs)input.TextChanged+=(_,_)=>textChanges++;
            var before=layer.Capture();var history=undo.Count;var start=window.Canvas.ToScreen(layer.Center+TransformGeometry.Rotate(new Vector(layer.Width/2,layer.Height/2),rotation));
            Assert(window.Canvas.BeginArtworkGesture(start),"Resize did not start.");var watch=Stopwatch.StartNew();
            for(var i=0;i<5000;i++)
            {
                var point=start+new Vector(70+Math.Sin(i*.03)*35,40+Math.Cos(i*.021)*25);window.Canvas.ContinueArtworkGesture(point,shift:i%600>=300);updates++;
                if(i%128==127){var timer=Stopwatch.StartNew();Flush();frameMs+=timer.Elapsed.TotalMilliseconds;frames++;AssertFields(layer);}
            }
            var totalMs=watch.Elapsed.TotalMilliseconds;
            Assert(undo.Count==history,"Preview generated undo entries.");Assert(oldInputs.All(input=>input.IsDescendantOf((DependencyObject)window.FindName("LogoProperties"))),"Drag rebuilt the inspector.");
            Assert(ReferenceEquals(Keyboard.FocusedElement,focusBefore),"Live updates changed keyboard focus.");
            Flush();AssertFields(layer);if(rotation==0)RenderDpi(window,Path.Combine(output,"during-resize.png"),1440,900,1);var final=layer.Capture();window.Canvas.CommitArtworkGesture();Assert(undo.Count==history+1 && !Pending(),"Commit did not yield exactly one action and flush latest values.");
            await window.UndoAsync();Assert(window.Canvas.SelectedLayer!.Capture()==before,"Resize undo lost precise state.");await window.UndoAsync(true);layer=window.Canvas.SelectedLayer!;Assert(layer.Capture()==final,"Resize redo lost exact final state.");
            AssertFields(layer);start=window.Canvas.ToScreen(layer.Center+TransformGeometry.Rotate(new Vector(layer.Width/2,layer.Height/2),rotation));window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(90,30));Flush();window.Canvas.CancelGesture();Assert(layer.Capture()==final && !Pending(),"Cancel left a pending/different transform.");AssertFields(layer);
            Console.WriteLine($"PASS live resize case angle={rotation}, zoom={zoom}, lock={locked}: 5000 updates + synthetic frame flushes {totalMs:0.0}ms.");
        }
        // Move uses the same frame-coalesced X/Y refresh, with precise cancel and no extra transaction.
        var selected=window.Canvas.SelectedLayer!;var moveBefore=selected.Capture();var move=window.Canvas.ToScreen(selected.Center);
        window.Canvas.BeginArtworkGesture(move);window.Canvas.ContinueArtworkGesture(move+new Vector(60,-25));Flush();AssertFields(selected);window.Canvas.CancelGesture();AssertFields(selected);Assert(selected.Capture()==moveBefore,"Move cancel failed.");
        var width=Input("Width");var height=Input("Height");var precise=selected.Width;
        width.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,width,height){RoutedEvent=Keyboard.LostKeyboardFocusEvent});Assert(selected.Width==precise,"Readout rounded stored precision on blur.");
        selected.ScaleLocked=true;var boundedBefore=selected.Capture();var requestedScale=1000.123456789/boundedBefore.Width;
        var boundedScale=Math.Clamp(requestedScale,Math.Max(1/boundedBefore.Width,1/boundedBefore.Height),Math.Min(32768/boundedBefore.Width,32768/boundedBefore.Height));
        width.Text="1000.123456789";width.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,width,height){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        var boundedWidth=boundedScale==requestedScale?1000.123456789:Math.Clamp(boundedBefore.Width*boundedScale,1,32768);
        var boundedHeight=Math.Clamp(boundedBefore.Height*boundedScale,1,32768);
        Assert(selected.Width==boundedWidth && selected.Height==boundedHeight && selected.X==boundedBefore.X && selected.Y==boundedBefore.Y && selected.Rotation==boundedBefore.Rotation && height.Text==selected.Height.ToString("0.##",CultureInfo.InvariantCulture),$"Typed resize after long rebases failed proportional saturation: {boundedBefore.Width:R}x{boundedBefore.Height:R} -> {selected.Width:R}x{selected.Height:R}, expected {boundedWidth:R}x{boundedHeight:R}.");
        selected.Width=1800.123456789;selected.Height=1200.876543219;typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);width=Input("Width");height=Input("Height");
        width.Text="1000.123456789";width.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,width,height){RoutedEvent=Keyboard.LostKeyboardFocusEvent});Assert(selected.Width==1000.123456789 && height.Text==selected.Height.ToString("0.##",CultureInfo.InvariantCulture),"Legal typed dimensions changed behavior/precision.");
        Layout(window,1440,900);var eyes=Descendants<CheckBox>((DependencyObject)window.FindName("LogoList")).Where(box=>box.Tag is ArtworkLayer).ToArray();Assert(eyes.Length==4,"Missing per-layer eye controls.");
        var eye=eyes.First(box=>!ReferenceEquals(box.Tag,selected));var hidden=(ArtworkLayer)eye.Tag;eye.Focus();Assert(ReferenceEquals(window.Canvas.SelectedLayer,selected),"Focusing eye changed selection.");
        var beforeEye=undo.Count;eye.IsChecked=false;eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(!hidden.Visible && ReferenceEquals(window.Canvas.SelectedLayer,selected) && undo.Count==beforeEye+1,"Eye toggle altered selection or undo semantics.");
        var glyph=Descendants<System.Windows.Shapes.Path>(eye).Single();Assert(eye.Width==26 && eye.Height==30 && eye.Margin==new Thickness(0,0,5,0) && glyph.StrokeThickness==1.4 && glyph.Opacity==.2,"Eye dimensions/hidden state differ from Canvas.");
        Assert(!Descendants<CheckBox>((DependencyObject)window.FindName("LogoProperties")).Any(),"Removed Visible/Guides/Snap controls remain.");
        var state=window.CreateProject();Assert(state["logoImages"]!.AsArray().OfType<JsonObject>().Single(item=>item["id"]!.GetValue<string>()==hidden.Id)["visible"]!.GetValue<bool>()==false,"Hidden state is not serialized.");
        var saved=Path.Combine(output,"hidden-layer.court.json");window.SaveProjectTo(saved);await window.OpenProjectFromAsync(saved);Assert(!window.Canvas.Layers.Single(layer=>layer.Id==hidden.Id).Visible && !window.Canvas.ShowGuides && !window.Canvas.SnapEnabled,"Open lost hidden state or enabled removed tools.");
        for(var i=0;i<window.Canvas.Layers.Count;i++){var logo=window.Canvas.Layers[i];logo.X=1900+i*1200;logo.Y=1600;logo.Width=900;logo.Height=585;logo.Rotation=i*12;}window.Canvas.SelectedLayer=null;window.Canvas.SelectedLayer=window.Canvas.Layers[0];window.Canvas.Fit();AssertFields(window.Canvas.SelectedLayer);
        window.SwitchSection("logos");Snapshot(window,Path.Combine(output,"live-logo-fields.png"),1440,900);Snapshot(window,Path.Combine(output,"live-logo-fields-compact.png"),1000,680);
        File.WriteAllText(Path.Combine(output,"live-resize-metrics.json"),new JsonObject{["syntheticPointerUpdates"]=updates,["syntheticFrameFlushes"]=frames,["fourReadoutTextChanges"]=textChanges,["frameFlushTotalMs"]=frameMs,["actualMouseOrFrameLatencyMeasured"]=false,["physicalDpiSwitchTested"]=false}.ToJsonString());
        await window.NewProjectAsync();Console.WriteLine("PASS live fields: coalesced X/Y/W/H; inspector identity/focus; precise typed values; locked/unlocked/rotated/zoomed/Shift long drags; single undo/redo; cancel; exact Canvas eye dimensions/placement/hidden state, selection preserved; hidden save/open; guides/snapping disabled.");
    }
}
