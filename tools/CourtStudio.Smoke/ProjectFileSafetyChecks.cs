using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckProjectFileSafety(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "project-file-safety"));
        Directory.CreateDirectory(root);
        var floor = Path.Combine(root, "floor.png"); WriteSolidImage(floor, Colors.Firebrick);
        var logo = Path.Combine(root, "hidden-logo.png"); WriteLogoExample(logo);
        JsonObject Project() => new()
        {
            ["version"] = 2, ["buildMode"] = "game-uv", ["floor"] = new JsonObject { ["id"] = "fixture-floor", ["name"] = "Fixture floor", ["path"] = floor },
            ["logoImages"] = new JsonArray(new JsonObject { ["id"] = "fixture-logo", ["name"] = "Hidden fixture", ["path"] = logo, ["visible"] = false })
        };
        foreach (var source in new[] { floor, logo })
        {
            var bytes = File.ReadAllBytes(source);
            await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectAssets.Save(source, Project(), root)));
            Assert(File.ReadAllBytes(source).SequenceEqual(bytes), "Saving a project overwrote its floor or hidden logo.");
        }
        var alias = Path.Combine(root, "linked.court.json");
        if (!CreateHardLink(alias, floor, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectAssets.Save(alias, Project(), root)));
        Assert(File.ReadAllBytes(alias).SequenceEqual(File.ReadAllBytes(floor)), "Project save replaced a hard-linked input asset.");
        File.Delete(alias);

        var path = Path.Combine(root, "guarded.court.json");
        StudioProjectStore.Write(path, Project());
        var original = File.ReadAllBytes(path);
        var backup = path + ".bak";
        File.Copy(logo, backup, true); var backupBytes = File.ReadAllBytes(backup);
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectAssets.Save(path, Project(), root)));
        Assert(File.ReadAllBytes(path).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(backupBytes), "Save damaged the current project or an unrelated backup-path file.");
        Assert(!Directory.Exists(Path.Combine(root, "guarded.court.assets")), "Invalid backup destination was detected only after bundling assets.");
        var backupSource = Project(); backupSource["logoImages"]![0]!["path"] = backup;
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectAssets.Save(path, backupSource, root)));
        Assert(File.ReadAllBytes(backup).SequenceEqual(backupBytes), "Backup generation replaced a referenced source asset.");
        File.Delete(backup);

        var invalid = Project(); invalid["version"] = "two";
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Write(path, invalid, keepBackup: true)));
        Assert(File.ReadAllBytes(path).SequenceEqual(original) && !File.Exists(backup), "Invalid snapshot was published or altered a backup.");
        invalid = Project(); var nested = new JsonObject(); invalid["extra"] = nested;
        for (var index = 0; index < 40; index++) { var next = new JsonObject(); nested["next"] = next; nested = next; }
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Write(path, invalid, keepBackup: true)));
        Assert(File.ReadAllBytes(path).SequenceEqual(original) && !File.Exists(backup), "Too-deep snapshot produced an unreadable project.");
        Assert(!Directory.GetFiles(root, "*.tmp").Any(), "Failed project save left staging files behind.");

        var encoded = Path.Combine(root, "encoding.court.json");
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
        {
            File.WriteAllText(encoded, Project().ToJsonString(), encoding);
            Assert(StudioProjectStore.Read(encoded)["version"]!.GetValue<int>() == 2, "Existing BOM-encoded project compatibility was lost.");
        }
        File.WriteAllBytes(encoded, Encoding.UTF8.GetBytes("{\"version\":2,\"buildMode\":\"game-uv\",\"projectName\":\"").Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray());
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Read(encoded)));
        File.Delete(encoded);
        var oversized = Path.Combine(root, "oversized.court.json");
        using (var stream = File.Create(oversized)) stream.SetLength(StudioProjectStore.MaximumJsonBytes + 1L);
        await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.Read(oversized)));
        File.Delete(oversized);

        var portablePath = Path.Combine(root, "source-project", "portable.court.json");
        var portable = StudioProjectAssets.Save(portablePath, Project(), root);
        var before = portable.ToJsonString();
        var resavedPath = Path.Combine(root, "new-project", "resaved.court.json");
        var resaved = StudioProjectAssets.Save(resavedPath, portable, ProjectRoot());
        foreach (var (sourcePath, stored) in new[] { (floor, resaved["floor"]!["path"]!.GetValue<string>()), (logo, resaved["logoImages"]![0]!["path"]!.GetValue<string>()) })
        {
            var bundled = Path.GetFullPath(stored, Path.GetDirectoryName(resavedPath)!);
            Assert(File.Exists(bundled) && SHA256.HashData(File.ReadAllBytes(bundled)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))), "Direct portable Save As resolved an asset against the app rather than the source project.");
        }
        Assert(portable.ToJsonString() == before, "Portable Save As mutated the supplied project snapshot.");

        var recoveryPath = Path.Combine(root, "recovery.json");
        var recovered = Project(); recovered["projectName"] = "Previous good recovery";
        StudioProjectStore.WriteRecoveryTo(recoveryPath, recovered);
        File.WriteAllText(recoveryPath + ".bak", "unrecognized backup data");
        recovered["projectName"] = "Latest recovery";
        StudioProjectStore.WriteRecoveryTo(recoveryPath, recovered);
        var lastGood = StudioProjectStore.LastGoodRecoveryPath(recoveryPath);
        Assert(StudioProjectStore.Read(recoveryPath)["projectName"]!.GetValue<string>() == "Latest recovery"
            && StudioProjectStore.Read(lastGood)["projectName"]!.GetValue<string>() == "Previous good recovery"
            && File.ReadAllText(recoveryPath + ".bak") == "unrecognized backup data", "Invalid backup blocked autosave or destroyed the prior valid recovery.");
        File.WriteAllText(recoveryPath, "{broken");
        Assert(StudioProjectStore.Recovery([recoveryPath, recoveryPath + ".bak", lastGood])?["projectName"]?.GetValue<string>() == "Previous good recovery", "Last-good recovery was not available after primary corruption.");
        recovered["projectName"] = "Recovery after corruption";
        StudioProjectStore.WriteRecoveryTo(recoveryPath, recovered);
        Assert(StudioProjectStore.Read(recoveryPath)["projectName"]!.GetValue<string>() == "Recovery after corruption"
            && StudioProjectStore.Read(lastGood)["projectName"]!.GetValue<string>() == "Previous good recovery"
            && File.ReadAllText(recoveryPath + ".bak") == "unrecognized backup data", "Autosave did not resume safely after corruption.");

        var external = Path.Combine(root, "external-assets"); Directory.CreateDirectory(external);
        var linkedAssets = Path.Combine(root, "redirected.court.assets");
        try
        {
            Directory.CreateSymbolicLink(linkedAssets, external);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            Assert(Path.GetFullPath(linkedAssets).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(external).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Junction fixture escaped its test directory.");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["COURT_FIXTURE_LINK"] = linkedAssets; start.Environment["COURT_FIXTURE_TARGET"] = external;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:COURT_FIXTURE_LINK -Value $env:COURT_FIXTURE_TARGET | Out-Null" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var standardOutput = process.StandardOutput.ReadToEndAsync(); var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await standardOutput; var failure = await standardError;
            if (process.ExitCode != 0) throw new IOException("Could not create a linked asset-directory fixture: " + failure);
        }
        if (new DirectoryInfo(linkedAssets).LinkTarget is not null)
        {
            try
            {
                var externalLogo = Path.Combine(external, "logo.png"); WriteLogoExample(externalLogo);
                var aliasedLogo = Path.Combine(linkedAssets, "logo.png");
                Assert(StudioFileSafety.ResolvePath(aliasedLogo).Equals(externalLogo, StringComparison.OrdinalIgnoreCase), "Parent-directory junction was not resolved.");
                var aliasedProject = Project(); aliasedProject["logoImages"]![0]!["path"] = externalLogo;
                await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectAssets.Save(aliasedLogo, aliasedProject, root)));
                var externalBytes = File.ReadAllBytes(externalLogo);
                await Expect<IOException>(() => Task.Run(() => StudioProjectAssets.Save(Path.Combine(root, "redirected.court.json"), Project(), root)));
                Assert(Directory.EnumerateFileSystemEntries(external).Count() == 1 && File.ReadAllBytes(externalLogo).SequenceEqual(externalBytes)
                    && !File.Exists(Path.Combine(root, "redirected.court.json")), "Save wrote through a linked portable asset directory.");
            }
            finally
            {
                Assert(new DirectoryInfo(linkedAssets).ResolveLinkTarget(true)?.FullName.Equals(external, StringComparison.OrdinalIgnoreCase) == true, "Fixture link target changed before cleanup.");
                Directory.Delete(linkedAssets);
            }
        }
        Console.WriteLine("PASS project file safety: floor/hidden-logo/hard-link/backup/junction collision protection; linked asset folder rejected; unrelated backup retained before bundling; invalid/deep/oversized/invalid-UTF8 snapshots rejected; BOM compatibility; relative portable Save As retains exact asset hashes; invalid recovery backup preserved while autosave/last-good fallback continue.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr securityAttributes);
}
