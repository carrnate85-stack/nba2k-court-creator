using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckPolish(StudioWindow window, string output)
    {
        CheckNativeWorkArea();
        Assert(window.Section=="paint", "Colors & Lines is not the launch inspector.");
        var renamePath = Path.Combine(output, "named-project.court.json");
        Assert(window.RenameProject("  球场 – Montréal 🏀  ") && window.ProjectName == "球场 – Montréal 🏀", "Unicode/trim rename failed.");
        Assert(!window.RenameProject(" ") && !window.RenameProject(new string('x',121)), "Invalid names were accepted.");
        window.SaveProjectTo(renamePath); Assert(window.RenameProject(new string('x',120)), "Long valid name failed.");
        Assert(window.CreateProject()["_projectPath"]!.GetValue<string>() == Path.GetFullPath(renamePath) && File.Exists(renamePath), "Rename moved an existing file.");
        await window.OpenProjectFromAsync(renamePath); Assert(window.ProjectName == "球场 – Montréal 🏀", "Display name did not survive save/open.");
        ((Button)window.FindName("ProjectTitleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)window.FindName("ProjectNameInput")).Text = "Cancel this";
        typeof(StudioWindow).GetMethod("FinishRename",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,[false]);
        Assert(window.ProjectName == "球场 – Montréal 🏀", "Cancel changed project name.");
        await window.NewProjectAsync(); Assert(window.ProjectName == "Untitled court", "New did not reset project name.");
        Assert(window.Section=="paint", "New did not restore Colors & Lines.");

        window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-5000;window.Top=-5000;window.Show();
        foreach(var key in new[]{Key.Enter,Key.Escape})
        {
            var oldName=window.ProjectName;((Button)window.FindName("ProjectTitleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var input=(TextBox)window.FindName("ProjectNameInput");input.Text=key==Key.Enter?"Court 名 – revised":"Discard this";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,key){RoutedEvent=Keyboard.KeyDownEvent});
            Assert(window.ProjectName==(key==Key.Enter?"Court 名 – revised":oldName) && input.Visibility==Visibility.Collapsed,"Inline rename Enter/Escape behavior failed.");
        }
        Layout(window,1000,680);
        var card=(Button)window.FindName("SelectedCourtCard");
        Assert(!Descendants<Button>(card).Any(),"Selected hardwood contains a nested button.");
        foreach(var point in new[]{new Point(8,8),new Point(25,35),new Point(120,30),new Point(card.ActualWidth-10,30)})
        { var hit=card.InputHitTest(point) as DependencyObject; Assert(hit is not null && (ReferenceEquals(hit,card)||card.IsAncestorOf(hit)),"A card region is not part of the catalog hit target."); }
        var openCount=0; bool active=false, choose=false;
        EventManager.RegisterClassHandler(typeof(FloorCatalogWindow),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
        {
            if(!active)return; var dialog=(FloorCatalogWindow)sender;openCount++;
            var category=Descendants<ComboBox>((DependencyObject)dialog.Content).First();
            var search=Descendants<TextBox>((DependencyObject)dialog.Content).First();
            Assert((string)category.SelectedItem=="All" && search.Text=="", "Catalog did not reopen unfiltered.");
            var all=Descendants<ListBox>((DependencyObject)dialog.Content).First();
            Assert(all.Items.OfType<FloorCatalogRow>().Count()==window.Floors.Count,"All catalog omitted courts.");
            Assert((all.SelectedItem as FloorCatalogRow)?.Floor.Id==window.CreateProject()["floor"]?["id"]?.GetValue<string>(),"Current hardwood is not identified in All.");
            if(!choose)search.Text="deliberately unmatched search";
            if(!choose){dialog.Close();return;}
            var list=Descendants<ListBox>((DependencyObject)dialog.Content).First();
            list.SelectedItem=list.Items.OfType<FloorCatalogRow>().First();
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        }));
        bool CatalogBusy() => (bool)typeof(StudioWindow).GetField("_catalogBusy",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        for(var i=0;i<4;i++)
        {
            var section=i<2?"paint":"logos";window.SwitchSection(section);var before=window.CreateProject()["floor"]?.ToJsonString();
            active=true;choose=i%2==1;card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));for(var wait=0;CatalogBusy() && wait<200;wait++)await Task.Delay(10);active=false;
            Assert(openCount==i+1 && !CatalogBusy() && window.Section==section,"Catalog changed the active inspector or failed to reopen.");
            if(!choose)Assert(window.CreateProject()["floor"]?.ToJsonString()==before,"Cancel changed hardwood.");
        }
        await window.NewProjectAsync();

        // Pointer selection and palette acceptance must still target a layer without the removed summary box.
        Layout(window,1000,680);
        var target=window.PaintLayers.First(layer=>layer.Id=="paint-right");var other=window.PaintLayers.First(layer=>layer.Id=="paint-left");var otherBefore=other.Color;
        Descendants<TextBox>((DependencyObject)window.Content).First(input=>Equals(input.Tag,"Color:paint-left")).Focus();
        var row=Descendants<Grid>((DependencyObject)window.Content).First(element=>Equals(element.Tag,target.Id));
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});
        ((Button)window.FindName("PinnedColorButton")).Focus();
        string? acceptedHex=null;bool paletteActive=false;
        EventManager.RegisterClassHandler(typeof(TeamColorWindow),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
        {
            if(!paletteActive)return;var dialog=(TeamColorWindow)sender;dialog.SetSearch("WNBA");dialog.UpdateLayout();
            var swatch=Descendants<Button>((DependencyObject)dialog.Content).First(button=>button.Tag is string hex&&hex.StartsWith('#'));
            acceptedHex=(string)swatch.Tag;swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        EventManager.RegisterClassHandler(typeof(StudioColorWindow),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
        {
            if(!paletteActive)return;var picker=(StudioColorWindow)sender;
            Descendants<Button>((DependencyObject)picker.Content).Single(button=>Equals(button.Content,"Team Colors")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Descendants<Button>((DependencyObject)picker.Content).Single(button=>Equals(button.Content,"Apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        paletteActive=true;((Button)window.FindName("PinnedColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));paletteActive=false;
        Assert(acceptedHex is not null && target.Color==acceptedHex && other.Color==otherBefore,"In-context palette affected the wrong selected layer.");
        Assert(window.FindName("LayerSearch") is null && window.FindName("WorkflowTeamColorsButton") is null && window.FindName("InspectorTeamColorsButton") is null,"Removed search/duplicate palettes remain.");
        ((Button)window.FindName("LogosButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(window.Section=="logos","Logos tab failed.");
        ((Button)window.FindName("PaintButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(window.Section=="paint","Colors tab failed.");
        Assert(((StackPanel)window.FindName("LayersHost")).Children.OfType<Expander>().Sum(group=>((StackPanel)group.Content).Children.Count)==23,"Removing layer search omitted controls.");
        await window.NewProjectAsync();

        var source=Path.Combine(output,"polish-logo.png");WriteLogoExample(source);
        var importer=new LogoImportWindow(testing:true);await importer.LoadImageAsync(source);
        importer.WindowStartupLocation=WindowStartupLocation.Manual;importer.Left=-5000;importer.Top=-5000;importer.Show();
        foreach(var scale in new[]{1d,1.25,1.5,2})
        {
            // Native WPF rendering at target DPI; sizes also model a 1920x1040-pixel work area.
            var size=StudioWindowBounds.FitSize(new Size(1000,680),new Size(1920/scale,1040/scale));
            var width=(int)Math.Floor(size.Width);var height=(int)Math.Floor(size.Height);
            window.MinWidth=Math.Min(1000,width);window.MinHeight=Math.Min(680,height);
            Layout(window,width,height);RenderDpi(window,Path.Combine(output,$"workspace-{scale*100:0}pct.png"),width,height,scale);
            var content=(FrameworkElement)window.Content;
            foreach(var name in new[]{"PinnedColorButton","ThemeToolbarButton"})
            {var element=(FrameworkElement)window.FindName(name);var bounds=element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));Assert(bounds.Bottom<height-26 && bounds.Top>=0,"Pinned controls overlap the status/taskbar area.");}
            var nav=(Button)window.FindName("LogosButton");Assert(nav.ActualWidth>50,"Workflow tabs collapsed.");
            var tabs=(FrameworkElement)window.FindName("InspectorTabs");
            foreach(var name in new[]{"PaintButton","LogosButton"})
            {
                var button=(Button)window.FindName(name);var label=Descendants<TextBlock>(button).Single();
                var inner=label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                Assert(new Rect(button.RenderSize).Contains(inner)&&label.ActualWidth>=label.DesiredSize.Width-.5,"Inspector tab label clipped.");
                Assert(tabs.TransformToAncestor(content).TransformBounds(new Rect(tabs.RenderSize)).Top>=((FrameworkElement)window.FindName("SelectedCourtCard")).TransformToAncestor(content).TransformBounds(new Rect(((FrameworkElement)window.FindName("SelectedCourtCard")).RenderSize)).Bottom,"Tabs are not below the hardwood card.");
            }
            var importSize=StudioWindowBounds.FitSize(new Size(780,520),new Size(1920/scale,1040/scale));
            importer.MinHeight=Math.Min(520,importSize.Height);importer.MinWidth=Math.Min(780,importSize.Width);
            importer.Width=importSize.Width;importer.Height=importSize.Height;
            await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            RenderDpi(importer,Path.Combine(output,$"importer-{scale*100:0}pct.png"),importSize.Width,importSize.Height,scale,nativeClient:true);
            var scroll=(ScrollViewer)importer.FindName("CleanupScroll");
            Assert(scroll.ScrollableHeight<1,$"Cleanup requires scrolling at {scale*100}% minimum/work-area size: {scroll.ScrollableHeight}.");
            var undo=(Button)importer.FindName("CleanupUndoButton");Assert(undo.ActualHeight==30,"Cleanup action heights vary.");
        }
        importer.Close();window.MinWidth=1000;window.MinHeight=680;

        var palettePath=Environment.GetEnvironmentVariable("COURT_CREATOR_PALETTE_CHECK");
        if(palettePath is not null)
        {
            var palettes=JsonNode.Parse(File.ReadAllText(palettePath))!["palettes"]!.AsArray();
            Assert(palettes.OfType<JsonObject>().Count(t=>t["league"]?.GetValue<string>()=="WNBA")==15 && palettes.OfType<JsonObject>().Count(t=>t["league"]?.GetValue<string>()=="EuroLeague")==20,"Verified league membership is incomplete.");
            var leagues=new TeamColorWindow(null,palettes,testing:true);
            RenderDpi(leagues,Path.Combine(output,"team-colors-categories.png"),620,650,1);
            Assert(leagues.Rows.Count==4,"Existing categories were lost or extra categories invented.");
            Assert(leagues.Rows.OfType<PaletteHeading>().Select(row=>row.Label).SequenceEqual(new[]{"NBA","WNBA","EuroLeague","D1"}),"Category order differs from the requested order.");
            leagues.SetSearch("EuroLeague");
            Assert(leagues.Rows.OfType<PaletteSwatches>().Count()==20,"EuroLeague search did not expose all teams.");
            leagues.ToggleGroup("league:EuroLeague");
            Assert(((TextBlock)leagues.FindName("ResultHint")).Text.StartsWith("20 matching teams."),"Collapsing search results changed their match count.");
            leagues.ToggleGroup("league:EuroLeague");
            RenderDpi(leagues,Path.Combine(output,"team-colors-euroleague.png"),620,650,1);
            leagues.SetSearch("WNBA");
            foreach(var scale in new[]{1d,1.25,1.5,2}) RenderDpi(leagues,Path.Combine(output,$"team-colors-{scale*100:0}pct.png"),460,400,scale);
            leagues.SetSearch("");Assert(leagues.Rows.Count==4,"Clear league search did not restore collapsed categories.");leagues.Close();
        }

        var synthetic=new JsonArray();for(var i=0;i<1500;i++)synthetic.Add(new JsonObject{["league"]="Large Test League",["team"]=$"Team {i:0000}",["colors"]=new JsonArray(new JsonObject{["name"]="Red",["hex"]="#AA1122"},new JsonObject{["name"]="Duplicate",["hex"]="#AA1122"},new JsonObject{["name"]="Blue",["hex"]="#1122AA"})});
        var watch=Stopwatch.StartNew();var colors=new TeamColorWindow(null,synthetic,testing:true);var createMs=watch.Elapsed.TotalMilliseconds;
        Assert(colors.Rows.Count==1,"Large category must start collapsed.");colors.ToggleGroup("league:Large Test League");
        RenderDpi(colors,Path.Combine(output,"team-colors-expanded.png"),620,650,1);
        Assert(colors.Rows.Count==1501 && Descendants<ListBoxItem>((DependencyObject)colors.Content).Count()<60,"Offscreen palette rows are not virtualized.");
        colors.ToggleGroup("team:0");Assert(colors.Rows.OfType<PaletteSwatches>().Single().Colors.Count==2,"Repeated hex swatches were not deduplicated.");
        colors.SetSearch("Team 1499");Assert(colors.Rows.OfType<PaletteSwatches>().Count()==1,"Search missed a collapsed team.");colors.SetSearch("");Assert(colors.Rows.Count==1502,"Clear search did not restore expansion state.");
        RenderDpi(colors,Path.Combine(output,"team-colors-open.png"),620,650,1);
        var scrollHost=colors.ScrollHost!;watch.Restart();for(var i=0;i<200;i++)colors.ScrollByWheel(-120);await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        colors.UpdateLayout();var down=scrollHost.VerticalOffset;Assert(down>0,"Repeated wheel input did not accumulate.");
        for(var i=0;i<100;i++)colors.ScrollByWheel(120);await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);colors.UpdateLayout();Assert(scrollHost.VerticalOffset<down,"Direction reversal queued stale scrolling.");
        for(var i=0;i<2000;i++)colors.ScrollByWheel(-120);await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);colors.UpdateLayout();Assert(Math.Abs(scrollHost.VerticalOffset-scrollHost.ScrollableHeight)<1,"Scroll bottom bound is wrong.");
        for(var i=0;i<2000;i++)colors.ScrollByWheel(120);await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);colors.UpdateLayout();Assert(scrollHost.VerticalOffset<1,"Scroll top bound is wrong.");
        var wheelMs=watch.Elapsed.TotalMilliseconds;
        var realized=Descendants<ListBoxItem>((DependencyObject)colors.Content).Count();colors.Close();
        File.WriteAllText(Path.Combine(output,"polish-metrics.json"),new JsonObject{["syntheticTeams"]=1500,["constructorMs"]=createMs,["syntheticWheelCalls"]=4300,["wheelAndLayoutMs"]=wheelMs,["realizedRows"]=realized,["actualOsDpiTested"]=false,["renderScales"]="100,125,150,200%",["manualTrackpadTested"]=false}.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS polish: rename/roundtrip/cancel, whole-card hit targets and catalog reopen/select/cancel, work-area and 100/125/150/200% native renders, no cleanup scroll, lazy categories/search/virtualization; 1500 teams created {createMs:0.0}ms, 4300 synthetic wheel calls/layout {wheelMs:0.0}ms, {realized} realized rows.");
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeMonitor { public int Size;public NativeRect Monitor,Work;public uint Flags; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeMinMax { public NativePoint Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor,ref NativeMonitor info);
    private static void CheckNativeWorkArea()
    {
        var hidden=new Window { WindowStyle=WindowStyle.None,Width=400,Height=300,MinWidth=100,MinHeight=100 };
        StudioWindowBounds.Attach(hidden);var handle=new System.Windows.Interop.WindowInteropHelper(hidden).EnsureHandle();
        var memory=System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<NativeMinMax>());
        try { System.Runtime.InteropServices.Marshal.StructureToPtr(new NativeMinMax(),memory,false);SendMessage(handle,0x0024,IntPtr.Zero,memory);var bounds=System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMinMax>(memory);var monitor=new NativeMonitor { Size=System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitor>() };Assert(GetMonitorInfo(MonitorFromWindow(handle,2),ref monitor),"Native monitor lookup failed.");Assert(bounds.MaxSize.X==monitor.Work.Right-monitor.Work.Left&&bounds.MaxSize.Y==monitor.Work.Bottom-monitor.Work.Top,"Frameless maximize does not respect the native taskbar work area."); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);hidden.Close(); }
    }
    private static void RenderDpi(Window window,string path,double width,double height,double scale,bool nativeClient=false)
    {
        var content=(FrameworkElement)window.Content;
        if(nativeClient) { content.UpdateLayout();var slot=System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(content);width=slot.Width;height=slot.Height; }
        else { window.Width=width;window.Height=height;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout(); }
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(width*scale),(int)Math.Ceiling(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(window.Background,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
