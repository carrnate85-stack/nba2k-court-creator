using System.IO;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckLogoImporters(string output)
    {
        string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void WriteFixture(string path,byte red)
        {
            var pixels=Enumerable.Repeat((byte)255,16*16*4).ToArray();
            for(var y=4;y<12;y++)for(var x=4;x<12;x++){var i=(y*16+x)*4;pixels[i]=30;pixels[i+1]=50;pixels[i+2]=red;}
            var image=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,pixels,64);image.Freeze();
            var encoder=new BmpBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
        }
        var source=Path.Combine(output,"import-source.bmp");WriteFixture(source,80);
        var importer=new LogoImportWindow(testing:true);
        try
        {
            await importer.LoadImageAsync(source);var metadata=new FileInfo(source);var length=metadata.Length;var time=metadata.LastWriteTimeUtc;var original=Hash(source);
            WriteFixture(source,180);File.SetLastWriteTimeUtc(source,time);
            Assert(new FileInfo(source).Length==length&&Hash(source)!=original,"Source-change fixture was not same-size/time.");
            var accepted=false;
            try{accepted=await importer.PreparePlacementAsync();}catch(InvalidDataException){}
            Assert(!accepted&&importer.PreparedPath is null,"Importer accepted changed source bytes after showing an earlier untouched preview.");
            await importer.LoadImageAsync(source);Assert(await importer.PreparePlacementAsync()&&importer.PreparedPath==Path.GetFullPath(source),"Unchanged untouched source must retain its original bytes.");
            importer.ReleaseTemporaryOutput();Assert(File.Exists(source),"Releasing an untouched logo deleted its source.");
        }
        finally{importer.ReleaseTemporaryOutput();importer.Close();}
        await CheckImporterTemporaryFiles(output,source,WriteFixture,Hash);
        await CheckImporterLoading(output,source);
        await CheckImporterSessions(output,source,WriteFixture,Hash);
        Console.WriteLine("PASS logo importers: same-size/time source changes rejected; borrowed originals retained; prepared PNG pixels/pins, owned cleanup and changed/replaced files; partial-write failure/retry and close/reload during held writes; 101-choice burst limited to one decoder/two reads; stale failures and close guarded; document-bound sessions, New/Open/Undo/constructor/cancellation ownership, capacity/nesting/busy guards, handoff pins, exact undo/redo and failure/retry. Dialog outcomes injected; no native windows opened.");
    }

    private static void PumpImporterTask(Task task)
    {
        if(!task.IsCompleted)
        {
            var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();
            _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task CheckImporterTemporaryFiles(string output,string source,Action<string,byte> write,Func<string,string> hash)
    {
        var importer=new LogoImportWindow(testing:true);
        try
        {
            await importer.LoadImageAsync(source);var sourceHash=hash(source);importer.Cleanup!.RemoveBackground(Colors.White,0);
            Assert(await importer.PreparePlacementAsync(),"Cleaned logo did not prepare.");
            var path=importer.PreparedPath!;var image=new LogoCleanupImage(StudioImages.Load(path));
            Assert(image.Pixel(0,0).A==0&&image.Pixel(8,8).A==255&&StringComparer.OrdinalIgnoreCase.Equals(importer.PreparedSourceRevision,hash(path)),"Prepared PNG lost pixels or its exact revision.");
            importer.ReleaseTemporaryOutput();Assert(!File.Exists(path)&&hash(source)==sourceHash,"Owned cleanup left a PNG or modified the original.");
            foreach(var external in new[]{"edited","replaced"})
            {
                Assert(await importer.PreparePlacementAsync(),"Re-preparation failed.");path=importer.PreparedPath!;
                var originalId=StudioFileSafety.StagingIdentity(path);var displaced=path+".owned";
                if(external=="replaced")File.Move(path,displaced);
                write(path,34);var changed=hash(path);
                if(external=="edited")Assert(StudioFileSafety.StagingIdentity(path)==originalId,"In-place replacement fixture changed file identity.");
                importer.ReleaseTemporaryOutput();
                Assert(File.Exists(path)&&hash(path)==changed&&importer.PreparedPath is null,"Cleanup deleted a changed/replaced external file.");
                File.Delete(path);if(File.Exists(displaced))File.Delete(displaced);
            }
            Assert(await importer.PreparePlacementAsync(),"Transient-lock preparation failed.");path=importer.PreparedPath!;
            var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            var unlocked=Task.Run(async()=>{await Task.Delay(40);held.Dispose();});
            importer.ReleaseTemporaryOutput();await unlocked;
            Assert(!File.Exists(path),"Bounded cleanup failed to recover from a transient lock.");
            Assert(await importer.PreparePlacementAsync(),"Cancel-close preparation failed.");path=importer.PreparedPath!;
            importer.Close();Assert(!File.Exists(path)&&importer.PreparedPath is null&&hash(source)==sourceHash,"Cancel/close leaked owned PNG or altered source bytes.");
        }
        finally{importer.ReleaseTemporaryOutput();importer.Close();}
        foreach(var action in new[]{"fail","close","reload"})
        {
            using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var block=true;
            var current=new LogoImportWindow(null,true,null,(image,stream)=>
            {
                if(block)
                {
                    entered.TrySetResult(((FileStream)stream).Name);
                    if(!release.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException("Held PNG writer timed out.");
                    if(action=="fail")throw new IOException("Injected PNG write failure.");
                }
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(stream);
            });
            try
            {
                await current.LoadImageAsync(source);current.Cleanup!.RemoveBackground(Colors.White,0);var before=hash(source);
                var pending=current.PreparePlacementAsync();var path=await entered.Task;
                Assert(!await current.PreparePlacementAsync(),"Concurrent preparation wrote a second PNG.");
                if(action=="close")current.Close();
                else if(action=="reload")await current.LoadImageAsync(source);
                release.Set();
                if(action=="fail")
                {
                    var rejected=false;try{await pending;}catch(IOException){rejected=true;}
                    Assert(rejected,"Injected partial PNG failure was accepted.");
                }
                else Assert(!await pending,"Closed/reloaded importer accepted an old prepared PNG.");
                Assert(!File.Exists(path)&&current.PreparedPath is null&&hash(source)==before,"Failed/stale PNG preparation leaked a stage or changed source bytes.");
                if(action!="close")
                {
                    block=false;Assert(await current.PreparePlacementAsync(),"Failed/stale preparation did not recover.");current.ReleaseTemporaryOutput();
                }
                Assert(!current.IsVisible,"Temporary-file test opened a native window.");
            }
            finally{release.Set();current.ReleaseTemporaryOutput();current.Close();}
        }
    }

    private static async Task CheckImporterLoading(string output,string source)
    {
        var second=Path.Combine(output,"second-import-source.bmp");File.Copy(source,second,true);
        var calls=0;var active=0;var maximum=0;var hold=false;var fail=false;
        TaskCompletionSource entered=new(TaskCreationOptions.RunContinuationsAsynchronously),release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer=new LogoImportWindow(null,true,async(path,cancellation)=>
        {
            calls++;maximum=Math.Max(maximum,++active);
            try
            {
                if(hold&&calls==1){entered.TrySetResult();await release.Task;if(fail)throw new IOException("Old load failed.");}
                return StudioImages.Load(path);
            }
            finally{active--;}
        },null);
        try
        {
            await importer.LoadImageAsync(source);var original=importer.Cleanup;var originalPath=importer.SourcePath;
            calls=0;maximum=0;hold=true;var pending=new List<Task>{importer.LoadImageAsync(source)};await entered.Task;
            for(var index=0;index<100;index++)pending.Add(importer.LoadImageAsync(second));
            Assert(calls==1&&ReferenceEquals(importer.Cleanup,original)&&importer.SourcePath==originalPath&&!((Button)importer.FindName("PlaceButton")).IsEnabled,
                "Queued logo loads decoded concurrently, replaced pixels early, or allowed placement.");
            Assert(!await importer.PreparePlacementAsync(),"Importer placed old pixels during a new load.");release.TrySetResult();await Task.WhenAll(pending);
            Assert(calls==2&&maximum==1&&importer.SourcePath==Path.GetFullPath(second)&&((Button)importer.FindName("PlaceButton")).IsEnabled,
                "101-choice burst lost its latest source or exceeded one decoder/two reads.");
            calls=0;entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);fail=true;
            var old=importer.LoadImageAsync(source);await entered.Task;var newer=importer.LoadImageAsync(second);release.TrySetResult();await Task.WhenAll(old,newer);
            Assert(importer.SourcePath==Path.GetFullPath(second)&&calls==2,"Old failure interrupted the latest successful logo load.");
            hold=false;var retained=importer.Cleanup;var retainedPath=importer.SourcePath;var rejected=false;
            try{await importer.LoadImageAsync(Path.Combine(output,"missing-logo.bmp"));}catch(FileNotFoundException){rejected=true;}
            Assert(rejected&&ReferenceEquals(importer.Cleanup,retained)&&importer.SourcePath==retainedPath&&((Button)importer.FindName("PlaceButton")).IsEnabled,"Current load failure lost the preceding image or disabled retry.");
            await importer.LoadImageAsync(source);hold=true;
            calls=0;fail=false;entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            original=importer.Cleanup;originalPath=importer.SourcePath;var text=((TextBlock)importer.FindName("SourceText")).Text;
            var closing=importer.LoadImageAsync(source);await entered.Task;importer.Close();release.TrySetResult();await closing;
            Assert(ReferenceEquals(importer.Cleanup,original)&&importer.SourcePath==originalPath&&((TextBlock)importer.FindName("SourceText")).Text==text,"Late decode changed a closed importer's pixels/UI.");
            var disposed=false;try{await importer.LoadImageAsync(source);}catch(ObjectDisposedException){disposed=true;}
            Assert(disposed&&!await importer.PreparePlacementAsync()&&!importer.IsVisible,"Closed importer accepted another load/placement or opened a window.");
        }
        finally{release.TrySetResult();importer.ReleaseTemporaryOutput();importer.Close();}
    }

    private static async Task CheckImporterSessions(string output,string source,Action<string,byte> write,Func<string,string> hash)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;var created=0;var shown=0;var failConstructor=false;
        Action? onCreate=null;Func<LogoImportWindow,bool?> show=_=>false;var dialogs=new List<LogoImportWindow>();
        var window=new StudioWindow(true,new PythonServiceClient(),new PythonServiceClient(),
            prepareLogo:(path,cancellation)=>Task.Run(()=>
            {
                cancellation.ThrowIfCancellationRequested();
                if(Path.GetFullPath(path).StartsWith(Path.GetFullPath(output)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    return new PreparedLogoAsset(StudioImages.Load(path),Path.GetFullPath(path));
                var owned=Path.GetFullPath(Path.Combine(output,"placed-"+Guid.NewGuid().ToString("N")+Path.GetExtension(path)));File.Copy(path,owned);
                return new PreparedLogoAsset(StudioImages.Load(owned),owned,true);
            },cancellation),
            createLogoImporter:()=>{created++;if(failConstructor)throw new IOException("Injected importer constructor failure.");onCreate?.Invoke();var dialog=new LogoImportWindow(testing:true);dialogs.Add(dialog);return dialog;},
            showLogoImporter:dialog=>{shown++;return show(dialog);});
        Task Open()=>(Task)typeof(StudioWindow).GetMethod("OpenLogoImporterAsync",flags)!.Invoke(window,null)!;
        int History(string name)=>((ICollection)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Count;
        bool IsOpen()=>(bool)typeof(StudioWindow).GetField("_logoImporterOpen",flags)!.GetValue(window)!;
        async Task Prepare()
        {
            foreach(var dialog in dialogs){dialog.ReleaseTemporaryOutput();dialog.Close();}dialogs.Clear();
            write(source,180);await window.NewProjectAsync();window.SwitchSection("logos");Layout(window,1000,680);
            foreach(var name in new[]{"_undo","_redo"})((IList)typeof(StudioWindow).GetField(name,flags)!.GetValue(window)!).Clear();
            created=shown=0;failConstructor=false;onCreate=null;
        }
        void PrepareDialog(LogoImportWindow dialog,bool cleaned)
        {
            PumpImporterTask(dialog.LoadImageAsync(source));if(cleaned)dialog.Cleanup!.RemoveBackground(Colors.White,0);
            var task=dialog.PreparePlacementAsync();PumpImporterTask(task);Assert(task.Result,"Session dialog did not prepare its image.");
        }
        try
        {
            await window.InitializeAsync();
            foreach(var cleaned in new[]{false,true})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();var original=hash(source);string? temporary=null;
                show=dialog=>{PrepareDialog(dialog,cleaned);temporary=dialog.PreparedPath;return true;};await Open();
                var logo=window.Canvas.Layers.Single();Assert(created==1&&shown==1&&!IsOpen()&&History("_undo")==1&&logo.Name==Path.GetFileNameWithoutExtension(source)&&File.Exists(logo.Path)&&hash(source)==original,
                    "Accepted importer lost placement/name/asset, added extra history, changed source, or stayed busy.");
                if(cleaned)Assert(!File.Exists(temporary)&&new LogoCleanupImage((BitmapSource)logo.Image!).Pixel(0,0).A==0,"Cleaned PNG was not adopted independently before cleanup.");
                await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==before,"Importer placement undo changed the preceding court.");
                await window.UndoAsync(true);Assert(window.Canvas.Layers.Count==1&&File.Exists(window.Canvas.Layers[0].Path),"Importer redo lost its adopted asset.");
            }
            foreach(var failure in new[]{"constructor","show","cancel"})
            {
                await Prepare();var before=window.CreateProject().ToJsonString();string? temporary=null;failConstructor=failure=="constructor";
                show=dialog=>{PrepareDialog(dialog,true);temporary=dialog.PreparedPath;if(failure=="show")throw new IOException("Injected dialog failure.");return false;};await Open();
                Assert(!IsOpen()&&window.CreateProject().ToJsonString()==before&&History("_undo")==0&&(temporary is null||!File.Exists(temporary)),"Failed/canceled importer changed court, leaked a PNG, or stayed busy.");
                failConstructor=false;show=dialog=>{PrepareDialog(dialog,false);return true;};await Open();Assert(window.Canvas.Layers.Count==1&&!IsOpen(),"Importer failure prevented retry.");
            }
            var blocked=new Dictionary<string,bool>{["_initialized"]=false,["_ready"]=false,["_syncing"]=true,["_restoring"]=true,["_saving"]=true,["_catalogBusy"]=true,["_closed"]=true,["_closePending"]=true};
            foreach(var (name,value) in blocked)
            {
                await Prepare();await window.AddLogoAsync(source);window.Canvas.SelectedLayer=window.Canvas.Layers.Single();Layout(window,1000,680);
                var point=window.Canvas.ToScreen(window.Canvas.SelectedLayer.Center);Assert(window.Canvas.BeginArtworkGesture(point),"Blocked importer fixture could not start a drag.");
                window.Canvas.ContinueArtworkGesture(point+new Vector(40,20));var before=window.CreateProject().ToJsonString();var undo=History("_undo");
                var field=typeof(StudioWindow).GetField(name,flags)!;var prior=field.GetValue(window);field.SetValue(window,value);
                try{await Open();Assert(created==0&&shown==0&&!IsOpen()&&window.CreateProject().ToJsonString()==before&&History("_undo")==undo,"Blocked importer interrupted a drag or bypassed "+name);}
                finally{field.SetValue(window,prior);}
                var commits=0;EventHandler<ArtworkGestureEventArgs> committed=(_,_)=>commits++;window.Canvas.TransformCommitted+=committed;
                try{window.Canvas.CommitArtworkGesture();Assert(commits==1,"Blocked importer consumed the pending drag.");}finally{window.Canvas.TransformCommitted-=committed;}
            }
            foreach(var transition in new[]{"new","open","undo","busy","source-edited","prepared-edited","prepared-replaced","nested","full"})
            {
                await Prepare();string? after=null;string? temporary=null;string? displaced=null;var undo=0;var redo=0;string? externalHash=null;
                show=dialog=>
                {
                    PrepareDialog(dialog,transition!="source-edited");temporary=dialog.PreparedPath;
                    switch(transition)
                    {
                        case "new":PumpImporterTask(window.NewProjectAsync());break;
                        case "open":var file=Path.Combine(output,"importer-open.court.json");window.SaveProjectTo(file);PumpImporterTask(window.OpenProjectFromAsync(file));break;
                        case "undo":window.SetLayerSettings("paint-left",color:"#123456");PumpImporterTask(window.UndoAsync());break;
                        case "busy":typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,true);break;
                        case "source-edited":write(source,20);break;
                        case "prepared-replaced":displaced=temporary+".owned";File.Move(temporary!,displaced);write(temporary!,21);externalHash=hash(temporary!);break;
                        case "prepared-edited":write(temporary!,22);externalHash=hash(temporary!);break;
                        case "nested":PumpImporterTask(Open());break;
                        case "full":for(var index=0;index<4;index++)PumpImporterTask(window.AddLogoAsync(source));break;
                    }
                    after=window.CreateProject().ToJsonString();undo=History("_undo");redo=History("_redo");return true;
                };
                try
                {
                    await Open();Assert(created==1&&shown==1&&!IsOpen(),"Session nested or remained busy.");
                    if(transition=="nested")Assert(window.Canvas.Layers.Count==1&&History("_undo")==1,"Nested importer duplicated or lost placement.");
                    else Assert(window.CreateProject().ToJsonString()==after&&History("_undo")==undo&&History("_redo")==redo,"Stale/full importer placed into a different court: "+transition);
                    if(externalHash is not null)Assert(File.Exists(temporary)&&hash(temporary!)==externalHash,"Session cleanup removed foreign prepared bytes.");
                    else if(transition!="source-edited")Assert(!File.Exists(temporary),"Stale/full session leaked an owned PNG.");
                    else Assert(File.Exists(source),"Changed borrowed source was deleted.");
                }
                finally
                {
                    typeof(StudioWindow).GetField("_catalogBusy",flags)!.SetValue(window,false);
                    if(externalHash is not null&&File.Exists(temporary))File.Delete(temporary!);if(displaced is not null&&File.Exists(displaced))File.Delete(displaced);
                }
            }
            await Prepare();onCreate=()=>PumpImporterTask(window.NewProjectAsync());await Open();
            Assert(created==1&&shown==0&&!IsOpen()&&window.Canvas.Layers.Count==0&&window.Section=="paint","Constructor-time replacement still opened a dialog or placed a logo.");
            await Prepare();for(var index=0;index<3;index++)await window.AddLogoAsync(source);
            var three=window.CreateProject().ToJsonString();var priorUndo=History("_undo");show=dialog=>{PrepareDialog(dialog,false);return true;};await Open();
            Assert(window.Canvas.Layers.Count==4&&History("_undo")==priorUndo+1&&ReferenceEquals(window.Canvas.SelectedLayer,window.Canvas.Layers[^1])&&window.Canvas.Tool==ArtworkTool.Move&&window.Section=="logos"&&!((Button)window.FindName("ImportLogoButton")).IsEnabled,
                "Fourth importer slot failed selection/tool/capacity/history.");
            await window.UndoAsync();Assert(window.CreateProject().ToJsonString()==three,"Fourth placement undo lost the first three logos.");await window.UndoAsync(true);Assert(window.Canvas.Layers.Count==4,"Fourth placement redo failed.");
            await Prepare();await window.AddLogoAsync(source);window.Canvas.SelectedLayer=window.Canvas.Layers.Single();Layout(window,1000,680);
            var start=window.Canvas.ToScreen(window.Canvas.SelectedLayer.Center);window.Canvas.BeginArtworkGesture(start);window.Canvas.ContinueArtworkGesture(start+new Vector(40,20));
            EventHandler reset=(_,_)=>PumpImporterTask(window.NewProjectAsync());window.Canvas.TransformPreviewEnded+=reset;
            try{await Open();Assert(created==0&&shown==0&&!IsOpen()&&window.Canvas.Layers.Count==0,"Cancellation-time New opened an importer for the replacement court.");}
            finally{window.Canvas.TransformPreviewEnded-=reset;}
            await Prepare();await window.AddLogoAsync(source);window.Canvas.SelectedLayer=window.Canvas.Layers.Single();Layout(window,1000,680);
            string? handoffState=null;var handoffHistory=0;var handoffEnds=0;string? handoffTemp=null;
            EventHandler replaceDuringPlacement=(_,_)=>
            {
                if(handoffEnds++!=0)return;PumpImporterTask(window.NewProjectAsync());handoffState=window.CreateProject().ToJsonString();handoffHistory=History("_undo");
            };
            show=dialog=>
            {
                PrepareDialog(dialog,true);handoffTemp=dialog.PreparedPath;window.Canvas.SelectedLayer=window.Canvas.Layers.Single();
                var point=window.Canvas.ToScreen(window.Canvas.SelectedLayer.Center);Assert(window.Canvas.BeginArtworkGesture(point),"Handoff fixture could not start a drag.");
                window.Canvas.ContinueArtworkGesture(point+new Vector(40,20));window.Canvas.TransformPreviewEnded+=replaceDuringPlacement;return true;
            };
            try
            {
                await Open();Assert(handoffEnds==1&&!IsOpen()&&window.CreateProject().ToJsonString()==handoffState&&History("_undo")==handoffHistory&&window.Canvas.Layers.Count==0&&window.Section=="paint"&&!File.Exists(handoffTemp),
                    "Placement cleanup callback added a stale logo to the replacement court or leaked the prepared PNG.");
            }
            finally{window.Canvas.TransformPreviewEnded-=replaceDuringPlacement;window.Canvas.CancelGesture();}
            await Prepare();show=dialog=>{PrepareDialog(dialog,true);window.Close();return true;};await Open();
            Assert(!IsOpen()&&window.Canvas.Layers.Count==0&&!window.IsVisible,"Importer placed into a closed workspace.");
        }
        finally{foreach(var dialog in dialogs){dialog.ReleaseTemporaryOutput();dialog.Close();}window.Close();}
    }
}
