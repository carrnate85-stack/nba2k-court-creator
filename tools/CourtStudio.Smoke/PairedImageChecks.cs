using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckPairedImages(string output)
    {
        var path=Path.GetFullPath(Path.Combine(output,"paired-floor.bmp"));
        void Write(byte red,byte green,byte blue)
        {
            const int width=2048,height=1024;var pixels=new byte[width*height*4];
            for(var index=0;index<pixels.Length;index+=4){pixels[index]=blue;pixels[index+1]=green;pixels[index+2]=red;pixels[index+3]=255;}
            var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);image.Freeze();var encoder=new BmpBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
        }
        void CheckRead((long SourceReads,long HashedBytes) before,int reads,long bytes,string context)
        {var after=StudioImages.ReadStatistics;Assert(after.SourceReads-before.SourceReads==reads && after.HashedBytes-before.HashedBytes==bytes,context+" reread its source texture or skipped full revision hashing.");}
        static byte[] Pixel(BitmapSource image)
        {var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var pixel=new byte[4];converted.CopyPixels(new Int32Rect(20,20,1,1),pixel,4,0);return pixel;}
        Write(210,30,70);var length=new FileInfo(path).Length;
        var pairMethod=typeof(StudioImages).GetMethod("LoadPair",BindingFlags.NonPublic|BindingFlags.Static);
        var failures=new List<string>();
        if(pairMethod is null)failures.Add("Paired decoder is unavailable.");
        (BitmapSource Image,BitmapSource Thumbnail) Pair(int width=2048,int thumbnail=144,CancellationToken cancellation=default)
        {
            try{return ((BitmapSource,BitmapSource))pairMethod!.Invoke(null,[path,width,thumbnail,cancellation])!;}
            catch(TargetInvocationException error) when(error.InnerException is not null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
        }
        var window=new StudioWindow(true);
        try
        {
            await window.InitializeAsync();var floor=StockFloor.Read(new JsonObject{["id"]="paired-floor",["name"]="Paired floor",["path"]=path,["previewPath"]=path},ProjectRoot());
            var before=StudioImages.ReadStatistics;await window.SelectFloorAsync(floor);
            try{CheckRead(before,1,length,"Native same-source preview/thumbnail");}catch(Exception error){failures.Add(error.Message);}
            if(pairMethod is not null)
            {
                before=StudioImages.ReadStatistics;var first=Pair();CheckRead(before,1,length,"Paired load");
                Assert(first.Image.IsFrozen && first.Thumbnail.IsFrozen && first.Image.PixelWidth==2048 && first.Image.PixelHeight==1024 && first.Thumbnail.PixelWidth==144 && first.Thumbnail.PixelHeight==72,"Paired image dimensions/frozen state differ.");
                var revision=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                Assert(StudioImages.SourceRevision(first.Image)==revision && StudioImages.SourceRevision(first.Thumbnail)==revision,"Paired images do not share the pinned source revision.");
                before=StudioImages.ReadStatistics;var second=Pair();CheckRead(before,1,length,"Cached paired load");
                Assert(ReferenceEquals(first.Image,second.Image) && ReferenceEquals(first.Thumbnail,second.Thumbnail),"Paired load bypassed the shared decoded-image cache.");
                Assert(ReferenceEquals(first.Image,StudioImages.Load(path,2048)) && ReferenceEquals(first.Thumbnail,StudioImages.Load(path,144)),"Single/paired decoding use incompatible cache entries.");
                before=StudioImages.ReadStatistics;var identical=Pair(144,144);CheckRead(before,1,length,"Identical-width pair");Assert(ReferenceEquals(identical.Image,identical.Thumbnail),"Identical widths decoded twice.");
                foreach(var(main,thumbnail) in new[]{(-1,144),(2048,-1),(16385,144),(2048,16385)})
                {before=StudioImages.ReadStatistics;try{Pair(main,thumbnail);throw new Exception("Invalid pair width was accepted.");}catch(ArgumentOutOfRangeException){}CheckRead(before,0,0,"Rejected widths");}
                using(var cancellation=new CancellationTokenSource())
                {cancellation.Cancel();before=StudioImages.ReadStatistics;try{Pair(cancellation:cancellation.Token);throw new Exception("Canceled pair was read.");}catch(OperationCanceledException){}CheckRead(before,0,0,"Canceled pair");}
                var originalTime=File.GetLastWriteTimeUtc(path);Write(20,180,240);Assert(new FileInfo(path).Length==length,"Revision fixture changed length.");File.SetLastWriteTimeUtc(path,originalTime);
                before=StudioImages.ReadStatistics;var changed=Pair();CheckRead(before,1,length,"Same-size/timestamp replacement");
                Assert(!ReferenceEquals(changed.Image,first.Image) && !ReferenceEquals(changed.Thumbnail,first.Thumbnail) && Pixel(changed.Image).SequenceEqual(new byte[]{240,180,20,255}) && Pixel(changed.Thumbnail).SequenceEqual(new byte[]{240,180,20,255}) && StudioImages.SourceRevision(changed.Image)==StudioImages.SourceRevision(changed.Thumbnail) && StudioImages.SourceRevision(changed.Image)!=revision,"Pair reused old artwork or mixed revisions.");
                before=StudioImages.ReadStatistics;await window.SelectFloorAsync(floor);CheckRead(before,1,length,"Changed native same-source floor");
                Assert(window.CreateProject()["floor"]!["sourceRevision"]!.GetValue<string>()==StudioImages.SourceRevision(changed.Image) && ReferenceEquals(((Image)window.FindName("FloorThumbnail")).Source,changed.Thumbnail),"Native floor and selected thumbnail use different source revisions.");
                var alias=floor with{PreviewPath=Path.Combine(Path.GetDirectoryName(path)!,".",Path.GetFileName(path)).ToUpperInvariant()};before=StudioImages.ReadStatistics;await window.SelectFloorAsync(alias);CheckRead(before,1,length,"Case/relative-segment source alias");
                Write(120,90,200);before=StudioImages.ReadStatistics;var tasks=Enumerable.Range(0,12).Select(_=>Task.Run(()=>Pair())).ToArray();var concurrent=await Task.WhenAll(tasks);CheckRead(before,12,length*12,"Concurrent pairs");
                Assert(concurrent.All(item=>ReferenceEquals(item.Image,concurrent[0].Image) && ReferenceEquals(item.Thumbnail,concurrent[0].Thumbnail)),"Concurrent pairs did not share decoded results.");
                using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert(exclusive.Length==length,"Pair kept its source handle open.");
                File.WriteAllBytes(path,[1,2,3,4]);var rejected=false;try{Pair();}catch(Exception error) when(error is IOException or NotSupportedException or FileFormatException){rejected=true;}
                Assert(rejected,"Malformed pair was accepted.");Write(70,100,130);before=StudioImages.ReadStatistics;var recovered=Pair();CheckRead(before,1,length,"Malformed-source retry");Assert(Pixel(recovered.Thumbnail).SequenceEqual(new byte[]{130,100,70,255}),"Paired decoder did not recover after failure.");
                var cache=StudioImages.CacheStatistics;Assert(cache.Bytes<=cache.Budget && cache.Count<=80,"Pairs exceeded the existing image-cache bounds.");
            }
        }
        finally{window.Close();}
        Assert(failures.Count==0,"Paired image failures:\n"+string.Join("\n",failures));
        Console.WriteLine("PASS paired floor images: native same-source court/thumbnail use one file open and one full SHA pass, including cached loads; same-size/time changes produce matching new revisions; widths/cancellation reject before reads; shared single/pair cache, concurrent decoding, finite cache budget, handle release and malformed-source retry remain intact. Off-screen I/O counts, not startup or pointer-latency measurements.");
    }
}
