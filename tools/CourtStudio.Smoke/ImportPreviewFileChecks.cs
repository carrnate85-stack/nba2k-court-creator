using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static void CheckImportPreviewFiles(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "preview-files-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var log = new StringWriter(); using var listener = new TextWriterTraceListener(log);
        Trace.Listeners.Add(listener);
        try
        {
            foreach (var action in new[] { "empty", "ordinary", "cancel", "decode-failure", "unknown", "nested", "no-receipt", "edit", "replace", "hardlink", "transient-lock", "permanent-lock", "retry-edit", "malformed" })
            {
                var preview = new StudioImportPreviewFiles(root);
                var paths = new List<string>(); var directories = new List<string>();
                FileStream? holder = null; Task? unlocked = null;
                try
                {
                    if (action != "empty")
                    {
                        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = new FileStream(preview.OutputPath, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
                        if (action is "cancel" or "decode-failure")
                        {
                            if (action == "decode-failure") File.WriteAllText(preview.OutputPath, "Owned but invalid PNG bytes.");
                            using var cancellation = new CancellationTokenSource();
                            if (action == "cancel") cancellation.Cancel();
                            try { preview.Decode(PreviewReceipt(preview.OutputPath), cancellation.Token); throw new Exception("Canceled/invalid preview decoded."); }
                            catch (OperationCanceledException) when (action == "cancel") { }
                            catch (Exception error) when (action == "decode-failure" && error is NotSupportedException or System.IO.FileFormatException) { }
                        }
                        else if (action is not "no-receipt" and not "malformed")
                        {
                            preview.AcceptReceipt(PreviewReceipt(preview.OutputPath));
                            preview.ValidatePixels(StudioImages.Load(preview.OutputPath));
                        }
                    }
                    if (action == "malformed")
                    {
                        var receipt = PreviewReceipt(preview.OutputPath); receipt["identity"]!["volume"] = true;
                        try { preview.AcceptReceipt(receipt); throw new Exception("Malformed receipt was accepted."); }
                        catch (InvalidDataException) { }
                    }
                    if (action == "unknown")
                    {
                        var path = Path.Combine(preview.DirectoryPath, "personal.txt"); File.WriteAllText(path, "Keep this file."); paths.Add(path);
                    }
                    if (action == "nested")
                    {
                        var directory = Path.Combine(preview.DirectoryPath, "personal"); Directory.CreateDirectory(directory); directories.Add(directory);
                        var path = Path.Combine(directory, "keep.txt"); File.WriteAllText(path, "Keep this file."); paths.Add(path);
                    }
                    if (action == "edit") EditManagedFile(preview.OutputPath);
                    if (action == "replace")
                    {
                        var displaced = preview.OutputPath + ".owned"; File.Move(preview.OutputPath, displaced); File.Copy(displaced, preview.OutputPath); paths.Add(displaced);
                    }
                    if (action == "hardlink")
                    {
                        var alias = preview.OutputPath + ".alias"; Assert(CreateHardLink(alias, preview.OutputPath, IntPtr.Zero), "Could not create preview hard link."); paths.Add(alias);
                    }
                    if (action is "transient-lock" or "permanent-lock" or "retry-edit")
                    {
                        holder = new FileStream(preview.OutputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        if (action != "permanent-lock") unlocked = Task.Run(async () =>
                        {
                            await Task.Delay(40);
                            if (action == "retry-edit") { holder.Position = holder.Length - 1; var value = holder.ReadByte(); holder.Position--; holder.WriteByte((byte)(value ^ 1)); holder.Flush(true); }
                            holder.Dispose();
                        });
                    }
                    var expected = action is "empty" or "ordinary" or "cancel" or "decode-failure" or "transient-lock";
                    var ownedDeleted = expected || action is "unknown" or "nested";
                    preview.Dispose(); unlocked?.GetAwaiter().GetResult();
                    Assert(Directory.Exists(preview.DirectoryPath) != expected, "Preview folder retention was incorrect: " + action);
                    Assert(!File.Exists(preview.OutputPath) == ownedDeleted, "Preview file ownership cleanup was incorrect: " + action);
                    Assert(paths.All(File.Exists), "Preview cleanup deleted an unexpected file: " + action);
                    if (!expected) { listener.Flush(); Assert(log.ToString().Contains(preview.DirectoryPath), "Cleanup did not report the retained path: " + action); }
                    if (expected)
                    {
                        Directory.CreateDirectory(preview.DirectoryPath); File.WriteAllText(preview.OutputPath, "reused filename");
                        preview.Dispose(); Assert(File.ReadAllText(preview.OutputPath) == "reused filename", "Duplicate disposal deleted a reused path.");
                    }
                }
                finally
                {
                    holder?.Dispose(); unlocked?.GetAwaiter().GetResult(); preview.Dispose();
                    foreach (var path in paths.Append(preview.OutputPath)) if (File.Exists(path)) File.Delete(path);
                    foreach (var directory in directories) Directory.Delete(directory, false);
                    if (Directory.Exists(preview.DirectoryPath)) Directory.Delete(preview.DirectoryPath, false);
                }
            }
            CheckPreviewDirectoryOwnership(root);
        }
        finally { Trace.Listeners.Remove(listener); if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false); }
        Console.WriteLine("PASS preview files: creation identity/content receipts, decoded pixel pins, empty/ordinary/canceled/decode-failure cleanup, unknown/nested/unreceipted/edited/replaced/shared/locked file retention, bounded retries and retry-time edits, malformed receipts, duplicate disposal, create-new and directory replacement/junction guards. No native windows opened.");
    }

    private static JsonObject PreviewReceipt(string path)
    {
        var identity = StudioFileSafety.StagingIdentity(path);
        return new JsonObject { ["version"] = 1, ["sha256"] = StudioImages.FileRevision(path), ["bytes"] = new FileInfo(path).Length,
            ["identity"] = new JsonObject { ["volume"] = identity.Volume, ["high"] = identity.High, ["low"] = identity.Low } };
    }

    private static void CheckPreviewDirectoryOwnership(string root)
    {
        var collision = Path.Combine(root, "existing"); Directory.CreateDirectory(collision);
        try { using var handle = StudioFileSafety.CreateOwnedDirectory(collision); throw new Exception("Existing temporary directory was adopted."); }
        catch (IOException) { }
        Directory.Delete(collision, false);
        foreach (var action in new[] { "directory", "junction" })
        {
            var preview = new StudioImportPreviewFiles(root); var displaced = preview.DirectoryPath + ".owned";
            var personal = Path.Combine(root, "personal-" + action); Directory.CreateDirectory(personal);
            var foreign = Path.Combine(personal, "preview.png"); File.WriteAllText(foreign, "Personal bytes.");
            try
            {
                try { Directory.Move(preview.DirectoryPath, displaced); throw new Exception("Live preview directory handle did not block replacement."); }
                catch (IOException) { }
                // Model the narrow race after the ownership handle is released.
                ((SafeFileHandle)typeof(StudioImportPreviewFiles).GetField("_directoryHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!).Dispose();
                Directory.Move(preview.DirectoryPath, displaced);
                if (action == "junction") StagingJunction(preview.DirectoryPath, personal);
                else { Directory.CreateDirectory(preview.DirectoryPath); File.Copy(foreign, preview.OutputPath); }
                preview.Dispose();
                Assert(File.ReadAllText(foreign) == "Personal bytes." && File.ReadAllText(preview.OutputPath) == "Personal bytes.", "Changed preview directory or junction target was deleted.");
            }
            finally
            {
                preview.Dispose();
                if (action == "directory" && File.Exists(preview.OutputPath)) File.Delete(preview.OutputPath);
                if (Directory.Exists(preview.DirectoryPath)) Directory.Delete(preview.DirectoryPath, false);
                if (Directory.Exists(displaced)) Directory.Delete(displaced, false);
                File.Delete(foreign); Directory.Delete(personal, false);
            }
        }
    }
}
