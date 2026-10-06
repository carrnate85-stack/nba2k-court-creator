using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;
using NBA2KCourtCreator.Services;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckFocusedSave(string output)
    {
        var failures=new List<string>();var flags=BindingFlags.NonPublic|BindingFlags.Instance;TextBox? focused=null;
        var root=ProjectRoot();var window=new StudioWindow(true,new PythonServiceClient(root,null),new PythonServiceClient(root,null),focusedInput:()=>focused);
        var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        var logo=Path.Combine(output,"focused-save-logo.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(logo))encoder.Save(stream);
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
            {
                window.SwitchSection("paint");input=Descendants<TextBox>((DependencyObject)window.FindName("LayersHost")).Single(box=>Equals(box.Tag,"Color:paint-left"));
            }
            else if(kind=="logo name")input=Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(box=>ReferenceEquals(box.Tag,window.Canvas.SelectedLayer));
            else if(kind=="project name")
            {
                typeof(StudioWindow).GetMethod("RenameProjectClick",flags)!.Invoke(window,[window,new RoutedEventArgs()]);input=(TextBox)window.FindName("ProjectNameInput");
            }
            else input=Field(kind);
            focused=input;input.Text=text;return input;
        }
        async Task Save(string path,bool synchronous){if(synchronous)window.SaveProjectTo(path);else await window.SaveProjectToAsync(path);}
        var valid=new Dictionary<string,string>{["X"]="1800.987654321",["Y"]="1400.123456789",["Width"]="1000.123456789",["Height"]="800.123456789",["Rotation (°)"]="-37.5",["hex"]="abc",["logo name"]="  Revised logo  ",["project name"]="  Saved court  "};
        try
        {
            await window.InitializeAsync();
            foreach(var synchronous in new[]{true,false})
            foreach(var(kind,text) in valid)
            {
                await Prepare();var before=window.CreateProject().ToJsonString();Edit(kind,text);
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(path,synchronous);var saved=StudioProjectStore.Read(path);var savedLogo=saved["logoImages"]![0]!;
                Verify("focused save / "+kind+" / sync="+synchronous,()=>
                {
                    if(kind=="hex")Assert(saved["paintSettings"]!["paint-left"]!["color"]!.GetValue<string>()=="#AABBCC","Focused hex was not saved.");
                    else if(kind=="logo name")Assert(savedLogo["name"]!.GetValue<string>()=="Revised logo","Focused logo name was not saved.");
                    else if(kind=="project name")Assert(saved["projectName"]!.GetValue<string>()=="Saved court","Focused project name was not saved.");
                    else{var key=kind.StartsWith("Rotation",StringComparison.Ordinal)?"rotation":kind.ToLowerInvariant();Assert(savedLogo[key]!.GetValue<double>()==double.Parse(text,CultureInfo.InvariantCulture),"Focused number was not saved exactly.");}
                    Assert(History("_undo")==1,"Focused edit was not one undoable action.");
                });
                if(History("_undo")==1)
                {var after=window.CreateProject().ToJsonString();focused=null;await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Focused save edit undo differs.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Focused save edit redo differs.");}
            }
            foreach(var synchronous in new[]{true,false})
            foreach(var(kind,text) in new[]{("X","NaN"),("Y","Infinity"),("X","1000001"),("Width","invalid"),("hex","zzzzzz"),("logo name"," "),("logo name",new string('a',121)),("project name"," "),("project name","Bad\0name")})
            {
                await Prepare();var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(path,synchronous);var bytes=File.ReadAllBytes(path);var time=File.GetLastWriteTimeUtc(path);
                var before=window.CreateProject().ToJsonString();var input=Edit(kind,text);var draft=input.Text;var rejected=false;
                try{await Save(path,synchronous);}catch(InvalidOperationException){rejected=true;}
                Verify("invalid focused save / "+kind+" / sync="+synchronous,()=>Assert(rejected && input.Text==draft && window.CreateProject().ToJsonString()==before && History("_undo")==0 && File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path)==time,"Invalid draft was discarded/applied or replaced the last saved file."));
            }
            foreach(var(name,value) in new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();var input=Edit("X","1800.987654321");var field=typeof(StudioWindow).GetField(name,flags)!;var previous=field.GetValue(window);field.SetValue(window,value);
                var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");var rejected=false;
                try{await Save(path,false);}catch(InvalidOperationException){rejected=true;}
                finally{field.SetValue(window,previous);}
                Verify("blocked save / "+name,()=>Assert(rejected && !File.Exists(path) && window.CreateProject().ToJsonString()==before && input.Text=="1800.987654321" && History("_undo")==0,"Save bypassed readiness or consumed a draft before rejection."));
            }
            await Prepare();var stale=Field("X");typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);stale.Text="8888";focused=stale;
            var stalePath=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(stalePath,false);
            Assert(StudioProjectStore.Read(stalePath)["logoImages"]![0]!["x"]!.GetValue<double>()==1700 && History("_undo")==0,"Save consumed a retired numeric input.");
            var policy=typeof(StudioWindow).GetMethod("HistoryShortcut",BindingFlags.NonPublic|BindingFlags.Static);
            Verify("history shortcut policy exists",()=>Assert(policy is not null,"Ctrl+Shift+Z has no distinct redo policy."));
            if(policy is not null)
            {
                foreach(var key in new[]{Key.Z,Key.Y,Key.S,Key.N})
                foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control,ModifierKeys.Control|ModifierKeys.Shift,ModifierKeys.Control|ModifierKeys.Alt,ModifierKeys.Shift,ModifierKeys.Windows|ModifierKeys.Control})
                {
                    bool? expected=modifiers==ModifierKeys.Control && key==Key.Z?false:modifiers==ModifierKeys.Control && key==Key.Y || modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && key==Key.Z?true:null;
                    Assert(Equals(policy.Invoke(null,[key,modifiers]),expected),"History shortcut modifiers were mapped incorrectly.");
                }
                await Prepare();var before=window.CreateProject().ToJsonString();window.RenameProject("Shortcut history");var after=window.CreateProject().ToJsonString();
                await window.UndoAsync((bool)policy.Invoke(null,[Key.Z,ModifierKeys.Control])!);Assert(window.CreateProject().ToJsonString()==before,"Ctrl+Z did not undo.");
                await window.UndoAsync((bool)policy.Invoke(null,[Key.Z,ModifierKeys.Control|ModifierKeys.Shift])!);Assert(window.CreateProject().ToJsonString()==after,"Ctrl+Shift+Z did not redo.");
            }
            var savePolicy=typeof(StudioWindow).GetMethod("SaveShortcut",BindingFlags.NonPublic|BindingFlags.Static);
            Assert(savePolicy is not null && window.FindName("SaveAsMenuItem") is MenuItem,"Save As policy/menu is missing.");
            foreach(var key in new[]{Key.S,Key.Z})
            foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control,ModifierKeys.Control|ModifierKeys.Shift,ModifierKeys.Control|ModifierKeys.Alt,ModifierKeys.Shift,ModifierKeys.Windows|ModifierKeys.Control})
            {
                bool? expected=key==Key.S && modifiers==ModifierKeys.Control?false:key==Key.S && modifiers==(ModifierKeys.Control|ModifierKeys.Shift)?true:null;
                Assert(Equals(savePolicy!.Invoke(null,[key,modifiers]),expected),"Save shortcut accepted an unrelated modifier combination.");
            }
            foreach(var kind in new[]{"X","hex","logo name","project name"})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();Edit(kind,valid[kind]);
                typeof(StudioWindow).GetMethod("BeginLogoOpacity",flags)!.Invoke(window,null);((Slider)window.FindName("LogoOpacitySlider")).Value=62.5;
                var opacityOnly=window.CreateProject().ToJsonString();var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(path,false);var after=window.CreateProject().ToJsonString();
                Assert(History("_undo")==2,"Save mixed opacity and the pending field into one history entry.");
                focused=null;await window.UndoAsync();var withoutField=window.CreateProject();withoutField["_projectPath"]=null;
                var expectedOpacity=JsonNode.Parse(opacityOnly)!.AsObject();expectedOpacity["_projectPath"]=null;
                Assert(withoutField.ToJsonString()==expectedOpacity.ToJsonString(),"Focused save field undo rewound opacity.");
                await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Focused save opacity undo differs.");
                await window.UndoAsync(true);await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==after,"Focused save opacity/field redo differs.");
            }
            await Prepare();window.RenameProject("First edit");await window.UndoAsync();var redoCount=History("_redo");focused=Field("X");
            var noOpPath=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(noOpPath,false);Assert(History("_undo")==0 && History("_redo")==redoCount,"No-op focused save generated history or cleared redo.");
            foreach(var synchronous in new[]{true,false})
            foreach(var kind in new[]{"X","hex","logo name","project name"})
            {
                await Prepare();var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json");await Save(path,synchronous);
                var before=window.CreateProject().ToJsonString();var bytes=File.ReadAllBytes(path);var time=File.GetLastWriteTimeUtc(path);Edit(kind,valid[kind]);var rejected=false;
                using(var holder=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    try{await Save(path,synchronous);}catch(Exception error) when(StudioProjectStore.RetryableFileAccess(error)){rejected=true;}
                }
                var committed=window.CreateProject().ToJsonString();
                Assert(rejected && committed!=before && History("_undo")==1 && File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path)==time,"Locked focused save discarded its edit or changed the last good file.");
                Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && ((Button)window.FindName("SaveToolbarButton")).IsEnabled && ((MenuItem)window.FindName("SaveAsMenuItem")).IsEnabled,"Failed focused save left editing or Save As disabled.");
                focused=null;await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Failed focused save edit cannot be undone.");
                await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==committed,"Failed focused save edit cannot be redone.");
                await Save(path,synchronous);Assert(window.CreateProject().ToJsonString()==committed && History("_undo")==1,"Retrying focused save generated another edit.");
                Assert(StudioProjectStore.Read(path)["projectName"]!.GetValue<string>()==window.CreateProject()["projectName"]!.GetValue<string>() && File.ReadAllBytes(path+".bak").SequenceEqual(bytes),"Focused save retry lost its last-good backup.");
            }
            await Prepare();var invalid=Edit("X","NaN");var layer=window.Canvas.SelectedLayer!;var start=window.Canvas.ToScreen(layer.Center);window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));
            var preview=layer.Capture();var ended=0;window.Canvas.TransformPreviewEnded+=(_,_)=>++ended;var rejectedDraft=false;
            try{await Save(Path.Combine(output,Guid.NewGuid().ToString("N")+".court.json"),false);}catch(InvalidOperationException){rejectedDraft=true;}
            Assert(rejectedDraft && invalid.Text=="NaN" && layer.Capture()==preview && ended==0,"Invalid focused draft canceled a still-valid drag before rejection.");window.Canvas.CancelGesture();
        }
        finally{focused=null;window.Close();}
        Assert(failures.Count==0,"Focused save failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS focused save: sync/async saves commit exact current numeric, hex, logo-name and project-name drafts; invalid drafts retain text/model/file bytes/time and active drags; eight blocked states and retired inputs are guarded; opacity/field history remains ordered and no-op save retains redo; real locked-file failures preserve committed drafts, history, controls and last-good bytes/time, then retry with correct backups; Ctrl+Z/Ctrl+Y/Ctrl+Shift+Z and Save/Save As policies reject unrelated modifiers. Focus input is injected; no native windows or dialogs opened.");
    }
}
