using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckManagedLogos(string output)
    {
        var source=Path.GetFullPath(Path.Combine(output,"managed-source.png"));
        var pixels=new byte[256*256*4];new Random(76).NextBytes(pixels);
        for(var index=3;index<pixels.Length;index+=4)pixels[index]=255;
        var image=BitmapSource.Create(256,256,96,96,PixelFormats.Bgra32,null,pixels,256*4);image.Freeze();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using(var stream=new FileStream(source,FileMode.Create,FileAccess.Write,FileShare.None))encoder.Save(stream);
        Assert(new FileInfo(source).Length>128*1024,"Copy fixture must span several chunks.");
        var owned=Path.GetFullPath(Path.Combine(output,"managed-edited-"+Guid.NewGuid().ToString("N")+".png"));File.Copy(source,owned);
        var prepared=new PreparedLogoAsset(StudioImages.Load(owned),owned,true);
        try
        {
            var time=File.GetLastWriteTimeUtc(owned);var bytes=File.ReadAllBytes(owned);bytes[^1]^=1;
            File.WriteAllBytes(owned,bytes);File.SetLastWriteTimeUtc(owned,time);
            var changed=SHA256.HashData(bytes);prepared.Dispose();
            Assert(File.Exists(owned)&&SHA256.HashData(File.ReadAllBytes(owned)).SequenceEqual(changed),
                "Canceled managed-asset cleanup deleted externally changed bytes with the same path/size/timestamp.");
        }
        finally{prepared.Dispose();if(File.Exists(owned))File.Delete(owned);}
        CheckManagedDisposal(output,source);
        CheckManagedCopy(output,source);
        await CheckManagedPending(output,source);
        Console.WriteLine("PASS managed logos: same-size/time edits, replacements, hard links, borrowed/committed files and duplicate disposal protected; transient/permanent locks bounded; create-new copy pins/handle identity, 512MB preflight, missing/locked/malformed inputs, mid-copy cancellation/failure, decode-time edits/replacements/cancellation; real pending New/Undo/Close and failed project-load cleanup preserve foreign files/history/source bytes. No native windows opened.");
    }

    private static string ManagedHash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void EditManagedFile(string path)
    {
        var time=File.GetLastWriteTimeUtc(path);var bytes=File.ReadAllBytes(path);bytes[^1]^=1;File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,time);
    }
    private static void CheckManagedDisposal(string output,string source)
    {
        var root=Path.GetFullPath(Path.Combine(output,"disposal-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var image=StudioImages.Load(source);var sourceHash=ManagedHash(source);var sourceTime=File.GetLastWriteTimeUtc(source);
        foreach(var action in new[]{"ordinary","commit","replace","hardlink","transient-lock","permanent-lock","retry-edit"})
        {
            var path=Path.Combine(root,action+".png");File.Copy(source,path);var asset=new PreparedLogoAsset(image,path,true);
            var displaced=path+".owned";var alias=path+".alias";FileStream? holder=null;Task? unlocked=null;
            try
            {
                if(action=="commit")asset.Commit();
                if(action=="replace"){File.Move(path,displaced);File.Copy(source,path);}
                if(action=="hardlink")Assert(CreateHardLink(alias,path,IntPtr.Zero),"Could not create shared-logo fixture.");
                if(action is "transient-lock" or "permanent-lock" or "retry-edit")
                {
                    holder=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                    if(action!="permanent-lock")unlocked=Task.Run(async()=>
                    {
                        await Task.Delay(40);
                        if(action=="retry-edit")
                        {
                            holder.Position=holder.Length-1;var value=holder.ReadByte();holder.Position=holder.Length-1;holder.WriteByte((byte)(value^1));holder.Flush(true);
                        }
                        holder.Dispose();
                    });
                }
                asset.Dispose();unlocked?.GetAwaiter().GetResult();
                var retained=action is "commit" or "replace" or "hardlink" or "permanent-lock" or "retry-edit";
                Assert(File.Exists(path)==retained,"Managed disposal had incorrect ownership/lock behavior: "+action);
                if(action=="hardlink")Assert(File.Exists(alias)&&ManagedHash(alias)==sourceHash,"Shared file alias was modified.");
                if(action=="replace")Assert(ManagedHash(displaced)==sourceHash&&ManagedHash(path)==sourceHash,"Same-byte replacement/displaced file was modified.");
                if(!retained){File.Copy(source,path);asset.Dispose();Assert(File.Exists(path),"Second Dispose deleted a reused filename.");}
                else if(action!="permanent-lock")asset.Dispose();
            }
            finally{holder?.Dispose();unlocked?.GetAwaiter().GetResult();asset.Dispose();foreach(var file in new[]{path,alias,displaced})if(File.Exists(file))File.Delete(file);}
        }
        using(var borrowed=new PreparedLogoAsset(image,source)){borrowed.Dispose();borrowed.Commit();}
        Assert(ManagedHash(source)==sourceHash&&File.GetLastWriteTimeUtc(source)==sourceTime,"Managed disposal modified borrowed source bytes/time.");
    }

    private static void CheckManagedCopy(string output,string source)
    {
        var root=Path.GetFullPath(Path.Combine(output,"copy-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var sentinel=Path.Combine(root,"personal.txt");File.WriteAllText(sentinel,"Personal file remains untouched.");
        var sourceHash=ManagedHash(source);var sourceTime=File.GetLastWriteTimeUtc(source);
        using(var copied=PreparedLogoAsset.CopyAndLoad(source,root,CancellationToken.None))
        {
            Assert(copied.Path!=source&&ManagedHash(copied.Path)==sourceHash&&StudioImages.SourceRevision(copied.Image)==StudioImages.FileRevision(source),"Owned copy changed source bytes or its pixel revision.");
            var path=copied.Path;copied.Dispose();Assert(!File.Exists(path),"Uncommitted copied logo leaked.");
        }
        using(var committed=PreparedLogoAsset.CopyAndLoad(source,root,CancellationToken.None))
        {
            var path=committed.Path;committed.Commit();committed.Dispose();Assert(File.Exists(path),"Committed copied logo was deleted.");File.Delete(path);
        }
        foreach(var failure in new[]{"cancel-chunk","throw-chunk","cancel-decode","edit-decode","replace-decode"})
        {
            using var cancellation=new CancellationTokenSource();string? staged=null;string? displaced=null;var rejected=false;
            try
            {
                using var unexpected=PreparedLogoAsset.CopyAndLoad(source,root,cancellation.Token,
                    copiedChunk:(path,bytes)=>
                    {
                        staged=path;Assert(bytes>0&&bytes<=StudioImages.MaximumAssetBytes,"Copy escaped its byte budget.");
                        if(failure=="cancel-chunk")cancellation.Cancel();if(failure=="throw-chunk")throw new IOException("Injected copy failure.");
                    },beforeDecode:path=>
                    {
                        staged=path;
                        if(failure=="cancel-decode")cancellation.Cancel();
                        if(failure=="edit-decode")EditManagedFile(path);
                        if(failure=="replace-decode"){displaced=path+".owned";File.Move(path,displaced);File.Copy(source,path);}
                    });
            }
            catch(Exception error) when(error is OperationCanceledException or IOException or InvalidDataException or NotSupportedException){rejected=true;}
            try
            {
                var foreign=failure is "edit-decode" or "replace-decode";
                Assert(rejected&&staged is not null&&File.Exists(staged)==foreign,"Copy cancellation/failure cleanup lost ownership: "+failure);
                if(failure=="replace-decode")Assert(File.Exists(displaced)&&ManagedHash(displaced!)==sourceHash&&ManagedHash(staged!)==sourceHash,"Copy replaced foreign same-byte file.");
                if(failure=="edit-decode")Assert(ManagedHash(staged!)!=sourceHash,"Copy cleanup replaced an in-place external edit.");
            }
            finally{foreach(var file in new[]{staged,displaced})if(file is not null&&File.Exists(file))File.Delete(file);}
        }
        using(var cancellation=new CancellationTokenSource())
        {
            cancellation.Cancel();var absent=Path.Combine(root,"canceled-directory");var rejected=false;
            try{using var asset=PreparedLogoAsset.CopyAndLoad(source,absent,cancellation.Token);}catch(OperationCanceledException){rejected=true;}
            Assert(rejected&&!Directory.Exists(absent),"Pre-canceled copy created a managed directory.");
        }
        var oversized=Path.Combine(root,"oversized.png");var oversizedDirectory=Path.Combine(root,"oversized-destination");
        try
        {
            using(var stream=new FileStream(oversized,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.SetLength(StudioImages.MaximumAssetBytes+1);
            var rejected=false;try{using var asset=PreparedLogoAsset.CopyAndLoad(oversized,oversizedDirectory,CancellationToken.None);}catch(NotSupportedException){rejected=true;}
            Assert(rejected&&!Directory.Exists(oversizedDirectory),"Oversized input was copied before its size preflight.");
        }
        finally{File.Delete(oversized);}
        var invalid=Path.Combine(root,"invalid.png");File.WriteAllText(invalid,"Not an image.");var before=Directory.GetFiles(root).Order().ToArray();
        var invalidRejected=false;try{using var asset=PreparedLogoAsset.CopyAndLoad(invalid,root,CancellationToken.None);}catch(Exception error) when(error is IOException or NotSupportedException){invalidRejected=true;}
        Assert(invalidRejected&&Directory.GetFiles(root).Order().SequenceEqual(before)&&File.ReadAllText(invalid)=="Not an image.","Malformed image left a copied stage or modified its source.");
        var missingDirectory=Path.Combine(root,"missing-destination");var missingRejected=false;
        try{using var asset=PreparedLogoAsset.CopyAndLoad(Path.Combine(root,"missing.png"),missingDirectory,CancellationToken.None);}catch(FileNotFoundException){missingRejected=true;}
        Assert(missingRejected&&!Directory.Exists(missingDirectory),"Missing source created a managed directory.");
        using(var holder=new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var lockedDirectory=Path.Combine(root,"locked-destination");var rejected=false;
            try{using var asset=PreparedLogoAsset.CopyAndLoad(source,lockedDirectory,CancellationToken.None);}catch(IOException){rejected=true;}
            Assert(rejected&&!Directory.Exists(lockedDirectory),"Locked source created a managed directory.");
        }
        Assert(ManagedHash(source)==sourceHash&&File.GetLastWriteTimeUtc(source)==sourceTime&&File.ReadAllText(sentinel)=="Personal file remains untouched.","Managed copies modified the source or unrelated personal file.");
    }

    private static async Task CheckManagedPending(string output,string source)
    {
        var root=Path.GetFullPath(Path.Combine(output,"pending-managed-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        foreach(var transition in new[]{"new","undo","close"})
        foreach(var external in new[]{"ordinary","edited","replaced","shared"})
        {
            var entered=new TaskCompletionSource<PreparedLogoAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var hold=false;
            var window=new StudioWindow(true,new PythonServiceClient(),new PythonServiceClient(),prepareLogo:async(path,cancellation)=>
            {
                if(!hold)return new PreparedLogoAsset(StudioImages.Load(path),path);
                var asset=await Task.Run(()=>PreparedLogoAsset.CopyAndLoad(path,root,cancellation),cancellation);
                entered.TrySetResult(asset);await release.Task;return asset;
            });
            string? path=null;string? displaced=null;string? alias=null;Task? pending=null;
            try
            {
                await window.InitializeAsync();window.SetLayerSettings("paint-left",color:"#123456");hold=true;
                pending=window.AddLogoAsync(source,"Canceled managed logo");var asset=await entered.Task;path=asset.Path;
                if(external=="edited")EditManagedFile(path);
                if(external=="replaced"){displaced=path+".owned";File.Move(path,displaced);File.Copy(source,path);}
                if(external=="shared"){alias=path+".alias";Assert(CreateHardLink(alias,path,IntPtr.Zero),"Could not create pending-logo shared alias.");}
                if(transition=="new")await window.NewProjectAsync();else if(transition=="undo")await window.UndoAsync();else window.Close();
                var before=window.CreateProject().ToJsonString();var history=((List<JsonObject>)typeof(StudioWindow).GetField("_undo",flags)!.GetValue(window)!).Count;
                release.TrySetResult();await pending;
                Assert(window.CreateProject().ToJsonString()==before&&window.Canvas.Layers.Count==0&&window.PendingLogoImports==0&&((List<JsonObject>)typeof(StudioWindow).GetField("_undo",flags)!.GetValue(window)!).Count==history,
                    "Canceled managed import changed its replacement court/history: "+transition+" / "+external);
                Assert(File.Exists(path)==(external!="ordinary"),"Canceled managed import deleted foreign bytes or leaked owned copy: "+transition+" / "+external);
                Assert(!window.IsVisible,"Managed-logo cancellation test opened a native window.");
            }
            finally
            {
                release.TrySetResult();if(pending is not null)await pending;window.Close();
                foreach(var file in new[]{path,alias,displaced})if(file is not null&&File.Exists(file))File.Delete(file);
            }
        }
        foreach(var external in new[]{false,true})
        {
            PreparedLogoAsset? prepared=null;var calls=0;var failFloor=false;
            var window=new StudioWindow(true,new PythonServiceClient(),new PythonServiceClient(),prepareLogo:(path,cancellation)=>
            {
                if(++calls==1){prepared=PreparedLogoAsset.CopyAndLoad(source,root,cancellation);return Task.FromResult(prepared);}
                if(external)EditManagedFile(prepared!.Path);failFloor=true;throw new IOException("Injected second project logo failure.");
            },loadFloorImage:(path,width)=>failFloor?throw new IOException("Injected hardwood preparation failure."):StudioImages.Load(path,width));
            try
            {
                await window.InitializeAsync();var before=window.CreateProject().ToJsonString();var snapshot=window.CreateProject();
                snapshot["logoImages"]=new JsonArray(Logo("first"),Logo("second"));
                JsonObject Logo(string id)=>new(){["id"]=id,["name"]=id,["path"]=source,["x"]=1500,["y"]=1000,["width"]=500,["height"]=300};
                var rejected=false;try{await window.RestoreProjectAsync(snapshot);}catch(IOException){rejected=true;}
                Assert(rejected&&window.CreateProject().ToJsonString()==before&&prepared is not null&&File.Exists(prepared.Path)==external,
                    "Failed project load changed its court or mishandled prepared owned/edited logo cleanup.");
            }
            finally{window.Close();if(prepared is not null&&File.Exists(prepared.Path))File.Delete(prepared.Path);}
        }
    }
}
