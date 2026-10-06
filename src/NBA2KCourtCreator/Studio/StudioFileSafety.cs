using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NBA2KCourtCreator.Studio;

internal static class StudioFileSafety
{
    internal static void ProtectSources(string destination, IEnumerable<string> sources)
    {
        var candidates = sources.ToArray();
        if (candidates.Length == 0) return;
        var resolved = ResolvePath(destination);
        var identity = File.Exists(destination) ? Identity(destination) : (FileIdentity?)null;
        foreach (var source in candidates)
        {
            if (resolved.Equals(ResolvePath(source), StringComparison.OrdinalIgnoreCase)
                || identity is not null && File.Exists(source) && identity == Identity(source))
                throw new InvalidDataException("Save the court project as a different file; source assets must stay unchanged.");
        }
    }

    internal static string ResolvePath(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo? item = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : File.Exists(resolved) ? new FileInfo(resolved) : null;
            if (item?.LinkTarget is not null)
                resolved = item.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Could not resolve a linked project path: " + resolved);
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static FileIdentity Identity(string path)
    {
        var information = InspectFile(path);
        return new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }
    internal static FileIdentity StagingIdentity(SafeFileHandle handle) => OwnedIdentity(InspectFile(handle));
    internal static FileIdentity StagingIdentity(string path)
    {
        using var handle = OpenMetadata(path, FileShare.ReadWrite | FileShare.Delete);
        return OwnedIdentity(InspectFile(handle));
    }
    internal static SafeFileHandle CreateOwnedDirectory(string path)
    {
        if (!CreateDirectory(NativePath(path), IntPtr.Zero))
            throw new IOException("Could not create a new temporary directory: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        return OpenOwnedDirectory(path);
    }
    internal static SafeFileHandle OpenOwnedDirectory(string path)
    {
        var handle = OpenMetadata(path, FileShare.ReadWrite, access: 1);
        try { DirectoryIdentity(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }
    internal static FileIdentity DirectoryIdentity(string path)
    {
        using var handle = OpenMetadata(path, FileShare.ReadWrite | FileShare.Delete);
        return DirectoryIdentity(handle);
    }
    internal static FileIdentity DirectoryIdentity(SafeFileHandle handle)
    {
        var information = InspectFile(handle);
        if ((information.Attributes & (uint)FileAttributes.Directory) == 0
            || (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The temporary directory became linked or is not a directory.");
        return new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }
    private static string NativePath(string path)
    {
        var nativePath = Path.GetFullPath(path);
        if (nativePath.Length >= 260 && !nativePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            nativePath = nativePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + nativePath[2..] : @"\\?\" + nativePath;
        return nativePath;
    }
    private static SafeFileHandle OpenMetadata(string path, FileShare share, uint access = 0)
    {
        const uint openExisting = 3, openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        // Inspect the link itself, with metadata-only access, rather than opening its target.
        var handle = CreateFile(NativePath(path), access, share, IntPtr.Zero,
            openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            var message = "Could not inspect temporary file " + path + ": " + new Win32Exception(code).Message;
            throw code switch
            {
                2 => new FileNotFoundException(message, path),
                3 => new DirectoryNotFoundException(message),
                5 => new UnauthorizedAccessException(message),
                _ => new IOException(message, unchecked((int)(0x80070000u | (uint)code)))
            };
        }
        return handle;
    }
    private static FileIdentity OwnedIdentity(FileInformation information)
    {
        if ((information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || information.Links != 1)
            throw new InvalidDataException("The temporary file became shared or linked; refusing publication or cleanup.");
        return new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }
    internal static void EnsureOwnedStaging(string path, FileIdentity identity)
    {
        if (StagingIdentity(path) != identity)
            throw new InvalidDataException("The temporary file identity changed; refusing publication or cleanup: " + path);
    }
    internal static void EnsureSingleLink(string path)
    {
        if (InspectFile(path).Links > 1) throw new InvalidDataException("Hard-linked preferences files are not supported.");
    }
    private static FileInformation InspectFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return InspectFile(handle, path);
    }
    private static FileInformation InspectFile(SafeFileHandle handle, string? path = null)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Could not check project file identity" + (path is null ? "." : ": " + path), new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }
    internal readonly record struct FileIdentity(uint Volume, uint High, uint Low);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
}
