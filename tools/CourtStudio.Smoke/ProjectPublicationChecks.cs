using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static JsonObject PublicationProject(string name) => new()
    { ["version"] = 2, ["buildMode"] = "game-uv", ["projectName"] = name };

    private static async Task CheckProjectPublication(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "publication-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var waits = new List<int>(); var calls = 0;
        StudioProjectStore.RetryFileAccess(() =>
        {
            calls++;
            if (calls <= 3) throw new IOException("simulated temporary file access", unchecked((int)(0x80070000u + new uint[] { 5, 32, 33 }[calls - 1])));
        }, waits.Add);
        Assert(calls == 4 && waits.SequenceEqual(new[] { 25, 50, 100 }), "Transient retry attempts/backoff are not bounded.");
        foreach (var code in new[] { 3, 87, 112, 1175, 1176, 1177 })
        {
            var error = new IOException("nonretryable publication error", unchecked((int)(0x80070000u + code)));
            calls = 0; waits.Clear();
            try { StudioProjectStore.RetryFileAccess(() => { calls++; throw error; }, waits.Add); throw new Exception("Nonretryable error was suppressed."); }
            catch (IOException actual) { Assert(ReferenceEquals(actual, error), "Publication changed the original error."); }
            Assert(calls == 1 && waits.Count == 0, "Malformed/path/disk/partial replacement error was retried.");
        }
        calls = 0; waits.Clear();
        var exhausted = new IOException("persistent sharing violation", unchecked((int)0x80070020u));
        try { StudioProjectStore.RetryFileAccess(() => { calls++; throw exhausted; }, waits.Add); throw new Exception("Persistent sharing violation was suppressed."); }
        catch (IOException actual) { Assert(ReferenceEquals(actual, exhausted), "Exhausted retry changed the original error."); }
        Assert(calls == 4 && waits.SequenceEqual(new[] { 25, 50, 100 }), "Persistent lock retries exceeded their budget.");
        calls = 0; waits.Clear();
        try { StudioProjectStore.RetryFileAccess(() => { calls++; throw exhausted; }, waits.Add, () => false); throw new Exception("Missing staging guard was ignored."); }
        catch (IOException) { }
        Assert(calls == 1 && waits.Count == 0, "A no-longer-safe operation was retried.");
        Assert(!StudioProjectStore.RetryableFileAccess(new InvalidDataException("invalid snapshot")), "Project validation failures are retryable.");

        var removeReplaced = new IOException("existing backup locked", unchecked((int)0x80070497u));
        calls = 0; waits.Clear();
        StudioProjectStore.RetryFileAccess(() => { calls++; if (calls < 4) throw removeReplaced; }, waits.Add,
            () => true, StudioProjectStore.RetryableProjectPublication);
        Assert(calls == 4 && waits.SequenceEqual(new[] { 25, 50, 100 }), "Remove-replaced publication retries exceeded their budget.");
        calls = 0; waits.Clear();
        try
        {
            StudioProjectStore.RetryFileAccess(() => { calls++; throw removeReplaced; }, waits.Add,
                () => true, StudioProjectStore.RetryableProjectPublication);
            throw new Exception("Persistent remove-replaced failure was suppressed.");
        }
        catch (IOException actual) { Assert(ReferenceEquals(actual, removeReplaced), "Remove-replaced retries changed the original error."); }
        Assert(calls == 4 && waits.SequenceEqual(new[] { 25, 50, 100 }), "Persistent remove-replaced retry budget failed.");
        foreach (var code in new[] { 1176, 1177 })
            Assert(!StudioProjectStore.RetryableProjectPublication(new IOException("partial move", unchecked((int)(0x80070000u + code)))),
                "Partial replacement failure became retryable.");

        foreach (var keepBackup in new[] { false, true })
        {
            var path = Path.Combine(root, keepBackup ? "replace.court.json" : "move.court.json");
            var staged = path + ".prepared.tmp";
            StudioProjectStore.Write(path, PublicationProject("Previous"));
            StudioProjectStore.Write(path + ".bak", PublicationProject("Older backup"));
            var previous = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
            File.WriteAllText(staged, PublicationProject("Next").ToJsonString());
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            waits.Clear();
            await Task.Run(() => StudioProjectStore.PublishProject(staged, path, keepBackup, [], delay =>
            {
                waits.Add(delay);
                Assert(File.ReadAllBytes(path).SequenceEqual(previous) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "Blocked publication altered the primary or backup before retry.");
                Assert(File.Exists(staged), "Blocked publication consumed its prepared file.");
                holder.Dispose();
            }));
            Assert(waits.Count == 1 && waits[0] == 25 && !File.Exists(staged)
                && StudioProjectStore.Read(path)["projectName"]!.GetValue<string>() == "Next", "Real short Windows destination lock did not recover.");
            Assert(File.ReadAllBytes(path + ".bak").SequenceEqual(keepBackup ? previous : backup), "Retry lost or rewrote the wrong backup.");
        }

        var backupLocked = Path.Combine(root, "backup-locked.court.json");
        var backupLockedStage = backupLocked + ".prepared.tmp";
        StudioProjectStore.Write(backupLocked, PublicationProject("Previous backup-lock project"));
        StudioProjectStore.Write(backupLocked + ".bak", PublicationProject("Older locked backup"));
        File.WriteAllText(backupLockedStage, PublicationProject("Backup lock cleared").ToJsonString());
        var backupLockedBytes = File.ReadAllBytes(backupLocked);
        var backupBytes = File.ReadAllBytes(backupLocked + ".bak");
        using (var holder = new FileStream(backupLocked + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            await Task.Run(() => StudioProjectStore.PublishProject(backupLockedStage, backupLocked, true, [], delay =>
            {
                waits.Add(delay);
                Assert(File.ReadAllBytes(backupLocked).SequenceEqual(backupLockedBytes)
                    && File.ReadAllBytes(backupLocked + ".bak").SequenceEqual(backupBytes)
                    && File.Exists(backupLockedStage), "Blocked backup replacement changed project, backup or staging before retry.");
                holder.Dispose();
            }));
            Assert(waits.SequenceEqual(new[] { 25 }) && !File.Exists(backupLockedStage)
                && StudioProjectStore.Read(backupLocked)["projectName"]!.GetValue<string>() == "Backup lock cleared"
                && File.ReadAllBytes(backupLocked + ".bak").SequenceEqual(backupLockedBytes),
                "Short backup lock did not recover or preserved the wrong backup.");
        }

        var lockedPrevious = File.ReadAllBytes(backupLocked);
        var lockedPreviousBackup = File.ReadAllBytes(backupLocked + ".bak");
        File.WriteAllText(backupLockedStage, PublicationProject("Must not bypass backup lock").ToJsonString());
        using (var holder = new FileStream(backupLocked + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            try
            {
                await Task.Run(() => StudioProjectStore.PublishProject(backupLockedStage, backupLocked, true, [], waits.Add));
                throw new Exception("Persistent backup lock was bypassed.");
            }
            catch (IOException error) { Assert((uint)error.HResult == 0x80070497u, "Persistent backup lock changed its original error."); }
            Assert(waits.SequenceEqual(new[] { 25, 50, 100 }) && File.Exists(backupLockedStage)
                && File.ReadAllBytes(backupLocked).SequenceEqual(lockedPrevious)
                && File.ReadAllBytes(backupLocked + ".bak").SequenceEqual(lockedPreviousBackup),
                "Persistent backup lock changed files or escaped the retry budget.");
        }
        using (var holder = new FileStream(backupLocked + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.PublishProject(backupLockedStage, backupLocked, true, [], delay =>
            {
                waits.Add(delay); holder.Dispose(); File.WriteAllText(backupLocked + ".bak", "externally changed backup");
            })));
            Assert(waits.SequenceEqual(new[] { 25 }) && File.Exists(backupLockedStage)
                && File.ReadAllBytes(backupLocked).SequenceEqual(lockedPrevious)
                && File.ReadAllText(backupLocked + ".bak") == "externally changed backup",
                "Remove-replaced retry failed to revalidate and preserve an externally changed backup.");
        }
        File.Delete(backupLockedStage);

        var fresh = Path.Combine(root, "fresh.court.json"); var freshStage = fresh + ".prepared.tmp";
        File.WriteAllText(freshStage, PublicationProject("Fresh").ToJsonString());
        using (var holder = new FileStream(freshStage, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            await Task.Run(() => StudioProjectStore.PublishProject(freshStage, fresh, true, [], delay => { waits.Add(delay); holder.Dispose(); }));
            Assert(waits.Count == 1 && !File.Exists(freshStage) && !File.Exists(fresh + ".bak")
                && StudioProjectStore.Read(fresh)["projectName"]!.GetValue<string>() == "Fresh", "Short staging-file lock did not recover new-project publication.");
        }

        var guarded = Path.Combine(root, "guarded.court.json"); var guardedStage = guarded + ".prepared.tmp";
        StudioProjectStore.Write(guarded, PublicationProject("Protected previous"));
        StudioProjectStore.Write(guarded + ".bak", PublicationProject("Valid backup"));
        File.WriteAllText(guardedStage, PublicationProject("Rejected next").ToJsonString());
        var guardedBytes = File.ReadAllBytes(guarded);
        using (var holder = new FileStream(guarded, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.PublishProject(guardedStage, guarded, true, [], delay =>
            {
                waits.Add(delay); holder.Dispose(); File.WriteAllText(guarded + ".bak", "new unrecognized backup");
            })));
            Assert(waits.Count == 1 && File.ReadAllBytes(guarded).SequenceEqual(guardedBytes)
                && File.ReadAllText(guarded + ".bak") == "new unrecognized backup" && File.Exists(guardedStage), "Retry did not revalidate the backup destination.");
        }
        File.Delete(guardedStage);

        var source = Path.Combine(root, "protected-source.png"); File.WriteAllText(source, "protected artwork bytes");
        var aliased = Path.Combine(root, "alias.court.json"); var aliasStage = aliased + ".prepared.tmp";
        StudioProjectStore.Write(aliased, PublicationProject("Original alias fixture"));
        File.WriteAllText(aliasStage, PublicationProject("Never published").ToJsonString());
        using (var holder = new FileStream(aliased, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            waits.Clear();
            await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.PublishProject(aliasStage, aliased, false, [source], delay =>
            {
                waits.Add(delay); holder.Dispose(); File.Delete(aliased);
                Assert(CreateHardLink(aliased, source, IntPtr.Zero), "Could not create publication alias fixture.");
            })));
            Assert(waits.Count == 1 && File.ReadAllText(source) == "protected artwork bytes" && File.ReadAllText(aliased) == "protected artwork bytes"
                && File.Exists(aliasStage), "Retry replaced an input introduced through a hard link.");
        }
        File.Delete(aliasStage); File.Delete(aliased);

        var locked = Path.Combine(root, "persistent.court.json");
        StudioProjectStore.Write(locked, PublicationProject("Persistent previous"));
        var lockedBytes = File.ReadAllBytes(locked);
        var foreign = locked + ".foreign.tmp"; File.WriteAllText(foreign, "foreign staging bytes");
        using (var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var elapsed = Stopwatch.StartNew();
            try { await Task.Run(() => StudioProjectStore.Write(locked, PublicationProject("Not saved"))).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Persistent lock was bypassed."); }
            catch (Exception error) when (StudioProjectStore.RetryableFileAccess(error)) { }
            Assert(elapsed.ElapsedMilliseconds >= 150 && File.ReadAllBytes(locked).SequenceEqual(lockedBytes)
                && Directory.GetFiles(root, "persistent.court.json.*.tmp").SequenceEqual(new[] { foreign }), "Persistent lock altered the project, leaked owned staging, or deleted foreign staging.");
        }
        var attributes = File.GetAttributes(locked);
        File.SetAttributes(locked, attributes | FileAttributes.ReadOnly);
        try
        {
            await Expect<UnauthorizedAccessException>(() => Task.Run(() => StudioProjectStore.Write(locked, PublicationProject("Permission denied"))));
            Assert(File.ReadAllBytes(locked).SequenceEqual(lockedBytes) && File.GetAttributes(locked).HasFlag(FileAttributes.ReadOnly), "Retry bypassed a real read-only permission or changed the project.");
        }
        finally { File.SetAttributes(locked, attributes); }

        var exclusive = Path.Combine(root, "exclusive.court.json");
        StudioProjectStore.Write(exclusive, PublicationProject("Exclusively locked previous"));
        using (var holder = new FileStream(exclusive, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var write = Task.Run(() => StudioProjectStore.Write(exclusive, PublicationProject("Exclusive lock cleared"), keepBackup: true));
            await Task.Delay(40);
            Assert(!write.IsCompleted && Directory.GetFiles(root, "exclusive.court.json.*.tmp").Length == 0, "Initial destination validation bypassed an exclusive lock or staged before validating.");
            holder.Dispose(); await write.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert(StudioProjectStore.Read(exclusive)["projectName"]!.GetValue<string>() == "Exclusive lock cleared"
            && StudioProjectStore.Read(exclusive + ".bak")["projectName"]!.GetValue<string>() == "Exclusively locked previous", "Retry did not recover initial locked validation and preserve its backup.");

        var recovery = Path.Combine(root, "recovery.json");
        StudioProjectStore.WriteRecoveryTo(recovery, PublicationProject("Good recovery"));
        File.WriteAllText(recovery + ".bak", "preserved invalid backup");
        using (var holder = new FileStream(recovery, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await Task.Run(() => StudioProjectStore.WriteRecoveryTo(recovery, PublicationProject("Blocked recovery"))).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Recovery lock was bypassed."); }
            catch (Exception error) when (StudioProjectStore.RetryableFileAccess(error)) { }
            Assert(StudioProjectStore.Read(recovery)["projectName"]!.GetValue<string>() == "Good recovery"
                && StudioProjectStore.Read(StudioProjectStore.LastGoodRecoveryPath(recovery))["projectName"]!.GetValue<string>() == "Good recovery"
                && File.ReadAllText(recovery + ".bak") == "preserved invalid backup", "Locked recovery damaged its primary/last-good/invalid backup.");
        }
        await Task.Run(() => StudioProjectStore.WriteRecoveryTo(recovery, PublicationProject("Recovered")));
        Assert(StudioProjectStore.Read(recovery)["projectName"]!.GetValue<string>() == "Recovered"
            && File.ReadAllText(recovery + ".bak") == "preserved invalid backup", "Recovery did not resume after its lock cleared.");

        var window = new StudioWindow(true);
        try
        {
            await window.InitializeAsync();
            var projectPath = Path.Combine(root, "native-save.court.json");
            await window.SaveProjectToAsync(projectPath);
            var before = window.CreateProject().ToJsonString(); var savedBytes = File.ReadAllBytes(projectPath);
            using (var holder = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var saving = window.SaveProjectToAsync(projectPath);
                var dispatched = await window.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background);
                Assert(dispatched && !saving.IsCompleted, "Native save retries blocked the dispatcher or bypassed the held project lock.");
                try { await saving; throw new Exception("Native save bypassed a permanent file lock."); }
                catch (Exception error) when (StudioProjectStore.RetryableFileAccess(error)) { }
            }
            Assert(window.CreateProject().ToJsonString() == before && File.ReadAllBytes(projectPath).SequenceEqual(savedBytes)
                && ((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && ((Button)window.FindName("BuildIffButton")).IsEnabled,
                "Failed locked native save changed the document/project or left controls disabled.");
            await window.SaveProjectToAsync(projectPath);
            Assert(StudioProjectStore.Read(projectPath)["buildMode"]!.GetValue<string>() == "game-uv", "Native save did not recover after a file lock cleared.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS project publication: bounded Win32 5/32/33 retries and publication-only 1175 retries; real initial-validation/destination/staging/backup locks; atomic move/replace and correct backups; per-attempt backup/hard-link protection; partial replacement errors never retried; persistent locks/read-only retain project and foreign staging; recovery/last-good resume; native save keeps dispatcher live, restores controls and recovers.");
    }
}
