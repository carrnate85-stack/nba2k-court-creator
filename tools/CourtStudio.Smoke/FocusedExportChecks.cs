using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckFocusedExport(string output)
    {
        var failures=new List<string>();var flags=BindingFlags.NonPublic|BindingFlags.Instance;TextBox? focused=null;
        var root=ProjectRoot();
        ProcessStartInfo Worker()
        {
            var start=new ProcessStartInfo(Path.Combine(root,"runtime","python","python.exe")){WorkingDirectory=root};
            foreach(var arg in new[]{"-B","-u",Path.Combine(root,"tests","worker_snapshot_fixture.py")})start.ArgumentList.Add(arg);
            return start;
        }
        using var exports=new PythonServiceClient(root,Worker);
        var window=new StudioWindow(true,new PythonServiceClient(root,null),exports,focusedInput:()=>focused);
        var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        var logo=Path.Combine(output,"focused-export-logo.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(logo))encoder.Save(stream);
        void Verify(string name,Action check){try{check();}catch(Exception error){failures.Add(name+": "+error.Message);}}
        int History(string name)=>((ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
        TextBox Field(string name)=>Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Single(input=>Equals(input.Tag,name));
        async Task Prepare()
        {
            focused=null;typeof(StudioWindow).GetMethod("FinishRename",flags)!.Invoke(window,[false]);
            await window.NewProjectAsync();await window.AddLogoAsync(logo,"First");window.SwitchSection("logos");
            var layer=window.Canvas.SelectedLayer!;layer.X=1700;layer.Y=1000;layer.Width=900;layer.Height=700;
            typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);((Expander)window.FindName("LogoDetailsExpander")).IsExpanded=true;Layout(window,1440,900);
            foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
        }
        TextBox Edit(string kind,string text)
        {
            TextBox input;
            if(kind=="hex")
            {window.SwitchSection("paint");input=Descendants<TextBox>((DependencyObject)window.FindName("LayersHost")).Single(box=>Equals(box.Tag,"Color:paint-left"));}
            else if(kind=="logo name")input=Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(box=>ReferenceEquals(box.Tag,window.Canvas.SelectedLayer));
            else if(kind=="project name")
            {typeof(StudioWindow).GetMethod("RenameProjectClick",flags)!.Invoke(window,[window,new RoutedEventArgs()]);input=(TextBox)window.FindName("ProjectNameInput");}
            else input=Field(kind);
            focused=input;input.Text=text;return input;
        }
        async Task<JsonObject> Export(string mode,string path)
        {
            if(mode=="Canvas")
            {
                var exchange=await window.PrepareCanvasExchangeAsync(path);
                var texture=JsonNode.Parse(File.ReadAllText(exchange.TexturePath))!.AsObject();var project=StudioProjectStore.Read(exchange.ProjectPath);
                texture.Remove("outputPath");texture.Remove("exportFullResolution");texture.Remove("geometryRevision");
                Assert(JsonNode.DeepEquals(texture,project),"Canvas texture and project used different snapshots.");return project;
            }
            await window.ExportToAsync(path,mode=="IFF");return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        }
        var valid=new Dictionary<string,string>{["X"]="1800.987654321",["Y"]="1400.123456789",["Width"]="1000.123456789",["Height"]="800.123456789",["Rotation (°)"]="-37.5",["hex"]="abc",["logo name"]="  Revised logo  ",["project name"]="  Exported court  "};
        try
        {
            await window.InitializeAsync();
            foreach(var mode in new[]{"PNG","IFF","Canvas"})
            foreach(var(kind,text) in valid)
            {
                await Prepare();var before=window.CreateProject().ToJsonString();Edit(kind,text);
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json"));var saved=await Export(mode,path);var savedLogo=saved["logoImages"]![0]!;
                Verify("focused output / "+mode+" / "+kind,()=>
                {
                    if(kind=="hex")Assert(saved["paintSettings"]!["paint-left"]!["color"]!.GetValue<string>()=="#AABBCC","Focused hex was omitted.");
                    else if(kind=="logo name")Assert(savedLogo["name"]!.GetValue<string>()=="Revised logo","Focused logo name was omitted.");
                    else if(kind=="project name")Assert(saved["projectName"]!.GetValue<string>()=="Exported court","Focused project name was omitted.");
                    else{var key=kind.StartsWith("Rotation",StringComparison.Ordinal)?"rotation":kind.ToLowerInvariant();Assert(savedLogo[key]!.GetValue<double>()==double.Parse(text,CultureInfo.InvariantCulture),"Focused number was omitted or rounded.");}
                    Assert(History("_undo")==1,"Focused output edit was not one undoable action.");
                });
                if(History("_undo")==1){var after=window.CreateProject().ToJsonString();focused=null;await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Output edit undo differs.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Output edit redo differs.");}
            }
            foreach(var mode in new[]{"PNG","IFF","Canvas"})
            foreach(var(kind,text) in new[]{("X","NaN"),("Y","Infinity"),("X","1000001"),("Width","invalid"),("hex","zzzzzz"),("logo name"," "),("logo name",new string('a',121)),("project name"," ")})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();var input=Edit(kind,text);var draft=input.Text;
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json"));var bytes=new byte[]{1,2,3,4};DateTime time=default;
                if(mode!="Canvas"){File.WriteAllBytes(path,bytes);time=File.GetLastWriteTimeUtc(path);}
                var rejected=false;try{await Export(mode,path);}catch(InvalidOperationException){rejected=true;}
                Verify("invalid output / "+mode+" / "+kind,()=>Assert(rejected && input.Text==draft && window.CreateProject().ToJsonString()==before && History("_undo")==0 &&
                    (mode=="Canvas"?!Directory.Exists(path):File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path)==time),"Invalid output consumed its draft or touched the destination/document."));
            }
            foreach(var mode in new[]{"PNG","IFF","Canvas"})
            foreach(var(name,value) in new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true,["_exporting"]=true})
            {
                await Prepare();var layer=window.Canvas.SelectedLayer!;var start=window.Canvas.ToScreen(layer.Center);window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));
                var preview=layer.Capture();var before=window.CreateProject().ToJsonString();var ended=0;
                EventHandler ending=(_,_)=>++ended;window.Canvas.TransformPreviewEnded+=ending;
                var field=typeof(StudioWindow).GetField(name,flags)!;var previous=field.GetValue(window);field.SetValue(window,value);
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json"));var rejected=false;
                try{await Export(mode,path);}catch(InvalidOperationException){rejected=true;}
                finally{field.SetValue(window,previous);window.Canvas.TransformPreviewEnded-=ending;}
                Verify("blocked output / "+mode+" / "+name,()=>Assert(rejected && !File.Exists(path) && !Directory.Exists(path) && layer.Capture()==preview && ended==0 && window.CreateProject().ToJsonString()==before && History("_undo")==0,"Blocked output canceled a valid drag or bypassed readiness."));
                window.Canvas.CancelGesture();
            }
            foreach(var mode in new[]{"PNG","IFF","Canvas"})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();var layer=window.Canvas.SelectedLayer!;var start=window.Canvas.ToScreen(layer.Center);
                window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));
                var busy=typeof(StudioWindow).GetField("_catalogBusy",flags)!;EventHandler block=(_,_)=>busy.SetValue(window,true);window.Canvas.TransformPreviewEnded+=block;
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json"));var rejected=false;
                try{await Export(mode,path);}catch(InvalidOperationException){rejected=true;}
                finally{window.Canvas.TransformPreviewEnded-=block;busy.SetValue(window,false);}
                Assert(rejected && !File.Exists(path) && !Directory.Exists(path) && window.CreateProject().ToJsonString()==before && History("_undo")==0,"Output ignored a cancellation-time readiness change.");
                Edit("X",valid["X"]);await Export(mode,path);Assert(History("_undo")==1,"Output did not recover after a callback veto.");
                await Prepare();window.RenameProject("Prior edit");await window.UndoAsync();var redoCount=History("_redo");focused=Field("X");
                await Export(mode,Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json")));
                Assert(History("_undo")==0 && History("_redo")==redoCount,"No-op output created history or cleared redo.");
                foreach(var kind in new[]{"X","hex","logo name","project name"})
                {
                    await Prepare();before=window.CreateProject().ToJsonString();Edit(kind,valid[kind]);
                    typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);((Slider)window.FindName("LogoOpacitySlider")).Value=62.5;
                    var opacityOnly=window.CreateProject().ToJsonString();await Export(mode,Path.Combine(output,Guid.NewGuid().ToString("N")+(mode=="Canvas"?"":".capture.json")));var after=window.CreateProject().ToJsonString();
                    Assert(History("_undo")==2,"Output mixed opacity and the pending field into one history entry.");
                    focused=null;await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==opacityOnly,"Output field undo rewound opacity.");
                    await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Output opacity undo differs.");
                    await window.UndoAsync(true);await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Output opacity/field redo differs.");
                }
            }
        }
        finally{focused=null;window.Close();}
        Assert(failures.Count==0,"Focused output failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS focused outputs: PNG/IFF/Canvas capture exact numeric, hex and name drafts with one undo action; invalid drafts preserve text/model/destinations; nine blocked states cannot interrupt a live drag or create an exchange directory; cancellation-time readiness changes reject and retry; opacity/field history remains ordered and no-op outputs retain redo; Canvas texture/project share one snapshot. Snapshot-only worker writes JSON markers, not real artwork; no native windows or external app launched.");
        await CheckFocusedOutputArtwork(output);
    }
    private static async Task CheckFocusedOutputArtwork(string output)
    {
        TextBox? focused=null;var root=ProjectRoot();var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var window=new StudioWindow(true,new PythonServiceClient(root,null),new PythonServiceClient(root,null),focusedInput:()=>focused);
        static byte[] Pixel(BitmapSource image,int x,int y)
        {
            var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var pixel=new byte[4];converted.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);return pixel;
        }
        TextBox Outside()
        {
            var host=(StackPanel)window.FindName("LayersHost");host.Children.OfType<Expander>().Single(item=>item.Header is TextBlock {Text:"Outside"}).IsExpanded=true;
            Layout(window,1440,900);return Descendants<TextBox>(host).Single(box=>Equals(box.Tag,"Color:stock-outside"));
        }
        try
        {
            await window.InitializeAsync();await window.AddLogoAsync(Path.Combine(output,"focused-export-logo.png"),"Focused artwork");window.SwitchSection("logos");
            var layer=window.Canvas.SelectedLayer!;layer.X=1700;layer.Y=1000;layer.Width=300;layer.Height=200;
            typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);((Expander)window.FindName("LogoDetailsExpander")).IsExpanded=true;Layout(window,1440,900);
            focused=Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Single(box=>Equals(box.Tag,"X"));focused.Text="2200.875";
            var positionPath=Path.GetFullPath(Path.Combine(output,"focused-position.png"));await window.ExportToAsync(positionPath,false);var positionImage=StudioImages.Load(positionPath);
            Assert(positionImage.PixelWidth==8192 && positionImage.PixelHeight==4096 && layer.X==2200.875,"Real focused position export used the wrong dimensions/coordinate.");
            var current=Pixel(positionImage,2350,1100);var retired=Pixel(positionImage,1850,1100);
            Assert(current[0]>250 && current[1]>250 && current[2]>250 && current[3]==255 && !(retired[0]>250 && retired[1]>250 && retired[2]>250),"Real PNG retained the logo's old position or omitted its focused position.");
            window.SwitchSection("paint");focused=Outside();focused.Text="123456";
            var colorPath=Path.GetFullPath(Path.Combine(output,"focused-color.png"));await window.ExportToAsync(colorPath,false);var color=Pixel(StudioImages.Load(colorPath),80,80);
            Assert(color.SequenceEqual(new byte[]{0x56,0x34,0x12,0xff}),"Real PNG did not apply the focused outside hex.");
            focused=Outside();focused.Text="a1b2c3";var iff=Path.GetFullPath(Path.Combine(output,"focused-color.iff"));await window.ExportToAsync(iff,true);
            focused=null;window.SwitchSection("import");await window.LoadImportSourceAsync(iff);
            var drawing=(ImageDrawing)typeof(StudioWindow).GetField("_importDrawing",flags)!.GetValue(window)!;var preview=(BitmapSource)drawing.ImageSource;
            var compressed=Pixel(preview,(int)(preview.PixelWidth*.04),(int)(preview.PixelHeight*.05));
            Assert(Math.Abs(compressed[2]-0xa1)<=4 && Math.Abs(compressed[1]-0xb2)<=4 && Math.Abs(compressed[0]-0xc3)<=4 && compressed[3]==255,"Real IFF reimport did not retain the focused outside hex.");
            Console.WriteLine("PASS real focused output artwork: 8192x4096 PNG moves an opaque logo to the exact typed X and removes its prior placement; focused apron hex matches exact PNG pixels; real BC7 IFF export/reimport retains a second focused hex within compression tolerance. No native windows opened; in-game loading remains unverified.");
        }
        finally{focused=null;window.Close();}
    }
}
