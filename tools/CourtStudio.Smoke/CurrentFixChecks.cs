using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckCurrentFixes(StudioWindow window,string output)
    {
        await window.NewProjectAsync();window.SwitchSection("logos");Layout(window,1440,900);
        foreach(var name in new[]{"TopChrome","DocumentChrome"})Assert(((FrameworkElement)window.FindName(name)).ActualHeight==36,"Chrome rows are not both 36 DIP.");
        Assert(((SolidColorBrush)((Border)window.FindName("TopChrome")).Background).Color==(Color)ColorConverter.ConvertFromString("#F8F9FB") && ((SolidColorBrush)((Border)window.FindName("DocumentChrome")).Background).Color==(Color)ColorConverter.ConvertFromString("#EFF1F4"),"Approved chrome colors differ.");
        var stateWindow=new StudioWindow(true);
        foreach(var state in new[]{WindowState.Maximized,WindowState.Normal,WindowState.Maximized,WindowState.Minimized,WindowState.Normal})
        {
            stateWindow.WindowState=state;
            var button=(Button)stateWindow.FindName("MaximizeButton");
            Assert(Equals(button.ToolTip,state==WindowState.Maximized?"Restore":"Maximize") && System.Windows.Automation.AutomationProperties.GetName(button)==(state==WindowState.Maximized?"Restore":"Maximize"),"Window state did not update its glyph semantics.");
        }
        ((Button)stateWindow.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(stateWindow.WindowState==WindowState.Maximized,"Maximize button did not change WindowState.");
        ((Button)stateWindow.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(stateWindow.WindowState==WindowState.Normal,"Restore button did not change WindowState.");stateWindow.Close();
        var moon=(Viewbox)((Button)window.FindName("ThemeToolbarButton")).Content;
        var path=(System.Windows.Shapes.Path)moon.Child;
        Assert(double.IsNaN(path.Width)&&double.IsNaN(path.Height),"Moon retains a fixed clipping slot.");
        var geometry=path.RenderedGeometry.GetRenderBounds(new Pen(path.Stroke,path.StrokeThickness));
        var clip=VisualTreeHelper.GetClip(path);
        Assert(clip is null || clip.Bounds.Contains(geometry),"Moon stroke is clipped inside its Path.");
        var inMoon=path.TransformToAncestor(moon).TransformBounds(geometry);
        Assert(new Rect(moon.RenderSize).Contains(inMoon),"Moon stroke exceeds its scaling container.");
        CaptureRegion(window,(FrameworkElement)window.FindName("PinnedTools"),Path.Combine(output,"rail-footer-after.png"));
        var zoom=(Button)window.FindName("ZoomMenuButton");Assert(zoom.Padding==new Thickness(8,5,8,5) && zoom.BorderThickness==new Thickness(),"Canvas zoom menu sizing differs.");
        window.Canvas.ActualSize();Assert(Math.Abs(window.Canvas.Scale-1)<.000001 && ((TextBlock)window.FindName("ZoomText")).Text=="100%","100% does not mean actual pixels.");window.Canvas.Fit();
        Assert(((TextBlock)window.FindName("ZoomText")).Text==$"{window.Canvas.Scale*100:0}%","Fit percentage is not actual Canvas scale.");
        var transparent=new byte[64*64*4];for(var y=20;y<44;y++)for(var x=20;x<44;x++){var i=(y*64+x)*4;transparent[i]=170;transparent[i+1]=60;transparent[i+2]=30;transparent[i+3]=255;}
        var fixture=BitmapSource.Create(64,64,96,96,PixelFormats.Bgra32,null,transparent,256);fixture.Freeze();
        var file=Path.Combine(output,"transparent-drag-logo.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(fixture));using(var stream=File.Create(file))encoder.Save(stream);
        await window.AddLogoAsync(file,"Transparent logo");var layer=window.Canvas.SelectedLayer!;layer.X=2400;layer.Y=1100;layer.Width=1200;layer.Height=900;layer.Rotation=31.5;layer.ScaleLocked=true;window.Canvas.SnapEnabled=false;
        var original=layer.Capture();var padding=layer.Center+TransformGeometry.Rotate(new Vector(-layer.Width*.3,0),layer.Rotation);
        Assert(window.Canvas.HitArtwork(padding) is null,"Unselected transparent padding steals artwork hits.");
        window.Canvas.SelectedLayer=null;Assert(!window.Canvas.BeginArtworkGesture(window.Canvas.ToScreen(padding)),"Unselected transparent box intercepted click.");
        window.Canvas.SelectedLayer=layer;Assert(window.Canvas.BeginArtworkGesture(window.Canvas.ToScreen(padding)),"Selected transparent box cannot drag.");
        window.Canvas.ContinueArtworkGesture(window.Canvas.ToScreen(padding)+new Vector(1,1));Assert(layer.Capture()==original,"Move jumps below the drag threshold.");
        var destination=window.Canvas.ToScreen(padding)+new Vector(40,-20);window.Canvas.ContinueArtworkGesture(destination);window.Canvas.CommitArtworkGesture();
        Assert(Math.Abs(layer.X-original.X-40/window.Canvas.Scale)<.001 && Math.Abs(layer.Y-original.Y+20/window.Canvas.Scale)<.001,"Rotated padding move maps the wrong coordinates.");
        await window.UndoAsync();layer=window.Canvas.SelectedLayer!;Assert(layer.Capture()==original,"Move did not produce one coherent undo.");await window.UndoAsync(true);Assert(window.Canvas.SelectedLayer!.Capture()!=original,"Move redo failed.");
        layer=window.Canvas.SelectedLayer!;layer.Restore(original);
        var corner=layer.Center+TransformGeometry.Rotate(new Vector(layer.Width/2,layer.Height/2),layer.Rotation);
        var screen=window.Canvas.ToScreen(corner);Assert(window.Canvas.BeginArtworkGesture(screen),"Resize handle cannot start.");
        var offset=TransformGeometry.Rotate(new Vector(350,70),layer.Rotation)*window.Canvas.Scale;
        window.Canvas.ContinueArtworkGesture(screen+offset);Assert(Math.Abs(layer.Width/layer.Height-original.Width/original.Height)<.000001,"Default handle scaling is not proportional.");
        var lockedState=layer.Capture();window.Canvas.ContinueArtworkGesture(screen+offset,shift:true);Assert(layer.Capture()==lockedState,"Shift changed mode with a jump.");
        window.Canvas.ContinueArtworkGesture(screen+offset+new Vector(70,0),shift:true);Assert(Math.Abs(layer.Width/layer.Height-original.Width/original.Height)>.01,"Shift did not temporarily stretch.");
        var freeState=layer.Capture();window.Canvas.ContinueArtworkGesture(screen+offset+new Vector(70,0),shift:false);Assert(layer.Capture()==freeState,"Releasing Shift jumped.");
        window.Canvas.CancelGesture();Assert(layer.Capture()==original,"Cancel failed to restore complete resize.");
        layer.ScaleLocked=false;var resize=new ArtworkResizeGesture(original,corner,2,true);var inverse=resize.Update(corner+new Vector(80,20),true,original);Assert(Math.Abs(inverse.Width/inverse.Height-original.Width/original.Height)<.000001,"Manual unlock Shift inversion failed.");layer.ScaleLocked=true;
        var anchor=layer.Center+TransformGeometry.Rotate(new Vector(-layer.Width/2,-layer.Height/2),layer.Rotation);
        resize=new ArtworkResizeGesture(original,corner,2,true);var result=resize.Update(corner+TransformGeometry.Rotate(new Vector(200,80),layer.Rotation),true,original);var newCenter=new Point(result.X+result.Width/2,result.Y+result.Height/2);var newAnchor=newCenter+TransformGeometry.Rotate(new Vector(-result.Width/2,-result.Height/2),result.Rotation);Assert((newAnchor-anchor).Length<.000001,"Resize did not retain the opposite rotated corner.");
        layer.X=2500.123456789;layer.Y=1111.987654321;window.SwitchSection("paint");window.SwitchSection("logos");Layout(window,1000,680);
        window.SelectCanvasTool(ArtworkTool.Transform);Layout(window,1000,680);
        var xField=Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).First(box=>Equals(box.Tag,"X"));var exact=layer.X;
        xField.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,xField,window){RoutedEvent=Keyboard.LostKeyboardFocusEvent});Assert(layer.X==exact,"Rounded display degraded stored precision.");
        foreach(var size in new[]{(1440,900),(1000,680)}){window.SwitchSection("logos");Snapshot(window,Path.Combine(output,$"compact-logos-one-{size.Item1}.png"),size.Item1,size.Item2);var list=(ListBox)window.FindName("LogoList");Assert(list.ActualHeight==224,"Fixed logo list changed height.");}
        for(var i=0;i<3;i++)await window.AddLogoAsync(file,$"Logo {i+2}");Layout(window,1000,680);Assert(!((Button)window.FindName("ImportLogoButton")).IsEnabled && !((Button)window.FindName("DuplicateLogoButton")).IsEnabled,"Four-slot capacity has misleading enabled actions.");
        bool denied=false;try{await window.AddLogoAsync(file);}catch(InvalidOperationException){denied=true;}Assert(denied && window.Canvas.Layers.Count==4,"Import overflow changed the four-slot model.");
        Snapshot(window,Path.Combine(output,"compact-logos-four.png"),1000,680);
        await window.NewProjectAsync();Snapshot(window,Path.Combine(output,"compact-logos-empty.png"),1000,680);
        Assert(((Image)window.FindName("FloorThumbnail")).Stretch==Stretch.Uniform,"Hardwood thumbnail still crops.");
        await window.SelectFloorAsync(window.Floors.First(f=>f.Path.Contains("floor-564-")));
        foreach(var paint in window.PaintLayers)window.SetLayerSettings(paint.Id,visible:paint.Id.StartsWith("paint-")||paint.Id.StartsWith("secondary-"),color:"#19583F");
        foreach(var line in window.LineLayers)window.SetLayerSettings(line.Id,visible:false);
        foreach(var scale in new[]{.125,.25,1d})
        {
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.PushTransform(new ScaleTransform(scale,scale));dc.PushTransform(new TranslateTransform(-1200,-1666));dc.DrawDrawing(window.Canvas.BackgroundDrawing!);}
            var pixels=new RenderTargetBitmap((int)(1000*scale),(int)Math.Ceiling(10*scale),96,96,PixelFormats.Pbgra32);pixels.Render(visual);var buffer=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(buffer,pixels.PixelWidth*4,0);
            for(var i=0;i<buffer.Length;i+=4)Assert(buffer[i]==63 && buffer[i+1]==88 && buffer[i+2]==25 && buffer[i+3]==255,$"Off-state native paint seam remains at scale {scale}.");
        }
        window.SetLayerSettings("line_lane_inner_lowShape",visible:true);
        Assert(window.LineLayers.Single(line=>line.Id=="line_lane_inner_lowShape").Visible,"Seam fix disabled intentional key line.");
        await window.NewProjectAsync();
        Console.WriteLine("PASS current fixes: complete moon geometry inside Viewbox; Canvas actual-pixel zoom; selected transparent drag/unselected alpha hits; threshold and one undo/redo; rotated anchored proportional resize; Shift rebase/inversion; cancel; display precision; compact 0/1/4-logo inspector; truthful capacity; thumbnail contain.");
    }
    private static void CaptureRegion(StudioWindow window,FrameworkElement element,string path)
    {
        var bounds=element.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(element.RenderSize));var v=new DrawingVisual();using(var dc=v.RenderOpen()){var brush=new VisualBrush((Visual)window.Content){AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,Viewbox=bounds,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,bounds.Width,bounds.Height),Stretch=Stretch.Fill};dc.DrawRectangle(brush,null,new Rect(0,0,bounds.Width,bounds.Height));}var image=new RenderTargetBitmap((int)Math.Ceiling(bounds.Width),(int)Math.Ceiling(bounds.Height),96,96,PixelFormats.Pbgra32);image.Render(v);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
    }
}
