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
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckFocusedClose(string output)
    {
        var failures=new List<string>();var flags=BindingFlags.NonPublic|BindingFlags.Instance;var root=ProjectRoot();
        var logo=Path.Combine(output,"focused-close-logo.png");var pixels=Enumerable.Repeat((byte)255,32*32*4).ToArray();var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(logo))encoder.Save(stream);
        var valid=new Dictionary<string,string>{["X"]="1800.987654321",["Y"]="1400.123456789",["Width"]="1000.123456789",["Height"]="800.123456789",["Rotation (°)"]="-37.5",["hex"]="abc",["logo name"]="  Recovered logo  ",["project name"]="  Recovered court  "};
        async Task Scenario(string kind,string text,bool invalid=false,string? busyFlag=null,bool failWrite=false,bool pendingImport=false,bool blur=false,bool callbackBusy=false)
        {
            TextBox? focused=null;var path=Path.Combine(output,Guid.NewGuid().ToString("N")+".recovery.json");var fail=false;
            using var releaseFailure=new ManualResetEventSlim();var failureStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer=new StudioSnapshotWriter(project=>{if(Volatile.Read(ref fail)){failureStarted.TrySetResult();Assert(releaseFailure.Wait(TimeSpan.FromSeconds(20)),"Focused recovery failure fixture timed out.");throw new IOException("Focused close publication failure");}StudioProjectStore.WriteRecoveryTo(path,project);});
            var held=new TaskCompletionSource<PreparedLogoAsset>(TaskCreationOptions.RunContinuationsAsynchronously);var preparations=0;CancellationToken importToken=default;
            Task<PreparedLogoAsset> PrepareLogo(string source,CancellationToken cancellation)
            {if(++preparations==1)return Task.FromResult(new PreparedLogoAsset(StudioImages.Load(source),source,false));importToken=cancellation;return held.Task;}
            var window=new StudioWindow(true,new PythonServiceClient(root,null),new PythonServiceClient(root,null),prepareLogo:pendingImport?PrepareLogo:null,recovery:writer,focusedInput:()=>focused);
            var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();Task? importing=null;
            int History(string name)=>((ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
            TextBox Edit()
            {
                TextBox input;
                if(kind=="hex"){window.SwitchSection("paint");Layout(window,1440,900);input=Descendants<TextBox>((DependencyObject)window.FindName("LayersHost")).Single(box=>Equals(box.Tag,"Color:paint-left"));}
                else if(kind=="logo name")input=Descendants<TextBox>((DependencyObject)window.FindName("LogoList")).Single(box=>ReferenceEquals(box.Tag,window.Canvas.SelectedLayer));
                else if(kind=="project name"){typeof(StudioWindow).GetMethod("RenameProjectClick",flags)!.Invoke(window,[window,new RoutedEventArgs()]);input=(TextBox)window.FindName("ProjectNameInput");}
                else input=Descendants<TextBox>((DependencyObject)window.FindName("LogoProperties")).Single(box=>Equals(box.Tag,kind));
                focused=input;input.Text=text;return input;
            }
            void Check(string name,Action assertion){try{assertion();}catch(Exception error){failures.Add(name+": "+error.Message);}}
            try
            {
                await window.InitializeAsync();await window.AddLogoAsync(logo,"First");window.SwitchSection("logos");var layer=window.Canvas.SelectedLayer!;layer.X=1700;layer.Y=1000;layer.Width=900;layer.Height=700;
                typeof(StudioWindow).GetMethod("RefreshLogoInspector",flags)!.Invoke(window,null);window.SelectCanvasTool(TwoK.Studio.ArtworkTool.Transform);Layout(window,1440,900);
                ((DispatcherTimer)typeof(StudioWindow).GetField("_recoveryTimer",flags)!.GetValue(window)!).Stop();
                foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
                await window.QueueRecovery()!;await writer.FlushAsync();var bytes=File.ReadAllBytes(path);var time=File.GetLastWriteTimeUtc(path);var before=window.CreateProject().ToJsonString();
                if(pendingImport)importing=window.AddLogoAsync(logo,"Pending");
                var input=Edit();var draft=input.Text;
                if(blur)input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,input,window){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                if(busyFlag is not null)
                {
                    focused=null;var start=window.Canvas.ToScreen(layer.Center);window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(60,30));var preview=layer.Capture();
                    var field=typeof(StudioWindow).GetField(busyFlag,flags)!;var previous=field.GetValue(window);if(!callbackBusy)field.SetValue(window,true);var ended=0;EventHandler ending=(_,_)=>{++ended;if(callbackBusy)field.SetValue(window,true);};window.Canvas.TransformPreviewEnded+=ending;
                    try{window.Close();Check("blocked close / "+busyFlag+" / callback="+callbackBusy,()=>Assert(!closed.Task.IsCompleted && window.PendingCloseRecovery is null && (callbackBusy?ended==1:layer.Capture()==preview && ended==0) && File.ReadAllBytes(path).SequenceEqual(bytes),"Busy close canceled an unrelated drag, wrote recovery or closed the workspace."));}
                    finally{field.SetValue(window,previous);window.Canvas.TransformPreviewEnded-=ending;}
                    window.Canvas.CancelGesture();return;
                }
                Volatile.Write(ref fail,failWrite);window.Close();var closing=window.PendingCloseRecovery;
                if(invalid)
                {
                    Check("invalid close / "+kind,()=>Assert(!closed.Task.IsCompleted && closing is null && !window.RecoveryClosePending && input.Text==draft && window.CreateProject().ToJsonString()==before && History("_undo")==0 && File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path)==time,"Invalid close discarded its draft or replaced the last recovery."));return;
                }
                if(failWrite)
                {
                    await failureStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));releaseFailure.Set();
                    Assert(closing is not null && !await closing,"Failed close unexpectedly succeeded.");await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
                    Check("failed focused close / "+kind,()=>Assert(!closed.Task.IsCompleted && !window.RecoveryClosePending && History("_undo")==1 && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path)==time,"Failed focused close lost the applied edit, disabled editing or replaced the recovery."));
                    var committed=window.CreateProject().ToJsonString();focused=null;await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Failed focused close edit cannot be undone.");await window.UndoAsync(true);Assert(window.CreateProject().ToJsonString()==committed,"Failed focused close edit cannot be redone.");
                    Volatile.Write(ref fail,false);window.Close();
                }
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));var saved=StudioProjectStore.Read(path);var savedLogo=saved["logoImages"]![0]!;
                Check("focused recovery / "+kind+" / pending="+pendingImport+" / retry="+failWrite,()=>
                {
                    if(kind=="hex")Assert(saved["paintSettings"]!["paint-left"]!["color"]!.GetValue<string>()=="#AABBCC","Final recovery omitted the focused hex.");
                    else if(kind=="logo name")Assert(savedLogo["name"]!.GetValue<string>()=="Recovered logo","Final recovery omitted the focused logo name.");
                    else if(kind=="project name")Assert(saved["projectName"]!.GetValue<string>()=="Recovered court","Final recovery omitted the focused project name.");
                    else{var key=kind.StartsWith("Rotation",StringComparison.Ordinal)?"rotation":kind.ToLowerInvariant();Assert(savedLogo[key]!.GetValue<double>()==double.Parse(text,CultureInfo.InvariantCulture),"Final recovery omitted or rounded the focused number.");}
                    Assert(History("_undo")==1 && !window.IsVisible,"Closing added duplicate history or showed a window.");
                });
                if(pendingImport)
                {
                    Assert(importToken.IsCancellationRequested && saved["logoImages"]!.AsArray().Count==1,"Focused close did not cancel pending artwork independently.");
                    held.TrySetResult(new PreparedLogoAsset(StudioImages.Load(logo),logo,false));await importing!;
                    Assert(window.Canvas.Layers.Count==1 && File.Exists(logo),"Late pending artwork changed the closed court or deleted a borrowed source.");
                }
            }
            finally
            {
                focused=null;Volatile.Write(ref fail,false);releaseFailure.Set();held.TrySetResult(new PreparedLogoAsset(StudioImages.Load(logo),logo,false));
                if(importing is not null)await importing;
                typeof(StudioWindow).GetMethod("FinishRename",flags)!.Invoke(window,[false]);
                if(!closed.Task.IsCompleted){if(window.PendingCloseRecovery is {} pending)await pending;window.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));}
                await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        foreach(var(kind,text) in valid)await Scenario(kind,text);
        foreach(var(kind,text) in new[]{("X","NaN"),("Y","Infinity"),("X","1000001"),("Width","invalid"),("hex","zzzzzz"),("logo name"," "),("logo name",new string('a',121)),("project name"," ")})await Scenario(kind,text,invalid:true);
        foreach(var flag in new[]{"_saving","_exporting","_syncing","_catalogBusy"})await Scenario("X",valid["X"],busyFlag:flag);
        await Scenario("X",valid["X"],busyFlag:"_catalogBusy",callbackBusy:true);
        foreach(var kind in new[]{"X","hex","logo name","project name"})await Scenario(kind,valid[kind],failWrite:true);
        await Scenario("X",valid["X"],pendingImport:true);
        await Scenario("project name",valid["project name"],blur:true);
        await Scenario("project name"," ",invalid:true,blur:true);
        var renameWindow=new StudioWindow(true);
        try
        {
            await renameWindow.InitializeAsync();var original=renameWindow.CreateProject();
            typeof(StudioWindow).GetMethod("RenameProjectClick",flags)!.Invoke(renameWindow,[renameWindow,new RoutedEventArgs()]);var input=(TextBox)renameWindow.FindName("ProjectNameInput");input.Text="Old document draft";
            var invalid=(JsonObject)original.DeepClone();invalid["version"]="invalid";await Expect<InvalidDataException>(()=>renameWindow.RestoreProjectAsync(invalid));
            Assert(input.Text=="Old document draft" && input.Visibility==Visibility.Visible && (bool)typeof(StudioWindow).GetField("_renaming",flags)!.GetValue(renameWindow)!,"Failed document replacement retired the current name draft.");
            await renameWindow.NewProjectAsync();var fresh=renameWindow.CreateProject().ToJsonString();
            input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,input,renameWindow){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
            Assert(!(bool)typeof(StudioWindow).GetField("_renaming",flags)!.GetValue(renameWindow)! && input.Visibility==Visibility.Collapsed && renameWindow.CreateProject().ToJsonString()==fresh,"Successful New retained or applied an old project-name draft.");
        }
        finally{typeof(StudioWindow).GetMethod("FinishRename",flags)!.Invoke(renameWindow,[false]);renameWindow.Close();}
        Assert(failures.Count==0,"Focused close failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS focused close: final recovery captures exact current numeric, hex and name drafts; invalid drafts keep the workspace/text/model/last-good bytes/time; busy save/export/sync/catalog closes retain live gestures and cancellation-time readiness is rechecked; publication failure retains an undoable edit and retries; pending artwork is canceled without dropping a draft or waiting for late preparation; project-name blur commits valid names, preserves invalid drafts, and successful New retires an old rename while failed replacement retains it. Injected focus/routed blur only; no native windows or dialogs opened.");
    }
}
