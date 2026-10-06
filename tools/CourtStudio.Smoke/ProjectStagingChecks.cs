using System.IO;
using System.Diagnostics;
using System.Text;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckProjectStaging(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "staging-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        foreach (var keepBackup in new[] { false, true })
        {
            var path = Path.Combine(root, keepBackup ? "replace.court.json" : "move.court.json");
            var staged = path + ".prepared.tmp"; var retained = staged + ".retained";
            StudioProjectStore.Write(path, PublicationProject("Last good project"));
            StudioProjectStore.Write(path + ".bak", PublicationProject("Last good backup"));
            File.WriteAllText(staged, PublicationProject("Owned new project").ToJsonString());
            var previous = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
            var prepared = File.ReadAllBytes(staged); var calls = 0;
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.PublishProject(staged, path, keepBackup, [], delay =>
                {
                    calls++; holder.Dispose(); File.Move(staged, retained); File.WriteAllText(staged, "foreign replacement");
                })));
            }
            Assert(calls == 1 && File.ReadAllBytes(path).SequenceEqual(previous) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup)
                && File.ReadAllText(staged) == "foreign replacement" && File.ReadAllBytes(retained).SequenceEqual(prepared),
                "Project publication or retry cleanup changed the last good project, backup or foreign staging path.");
        }
        await CheckInitialStaging(root);
        CheckStagingCleanup(root);
        await CheckPreferenceStaging(root);
        CheckLongStaging(root);
        Console.WriteLine("PASS native staging: handle-captured identities; initial/retry-time replacements, shared hard links and real junctions rejected; last good project/backup/preferences and foreign paths preserved; bounded ownership-aware cleanup, post-commit filename reuse and normal writes. No native windows opened.");
    }

    private static StudioFileSafety.FileIdentity PrepareStaging(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var identity = StudioFileSafety.StagingIdentity(stream.SafeFileHandle);
        var bytes = Encoding.UTF8.GetBytes(PublicationProject("Owned stage").ToJsonString());
        stream.Write(bytes); stream.Flush(true);
        return identity;
    }

    private static void StagingJunction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0, "Could not create staging junction: " + stdout + stderr);
    }

    private static async Task CheckInitialStaging(string root)
    {
        foreach (var kind in new[] { "replacement", "hardlink", "junction", "directory" })
        foreach (var keepBackup in new[] { false, true })
        {
            var directory = Path.Combine(root, kind + "-" + keepBackup); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "project.json"); var staged = path + ".tmp"; var retained = staged + ".retained";
            var personal = Path.Combine(directory, "personal"); Directory.CreateDirectory(personal);
            var personalPath = Path.Combine(personal, "personal.txt"); File.WriteAllText(personalPath, "private artwork sentinel");
            var personalBytes = File.ReadAllBytes(personalPath); var personalTime = File.GetLastWriteTimeUtc(personalPath);
            StudioProjectStore.Write(path, PublicationProject("Previous"));
            StudioProjectStore.Write(path + ".bak", PublicationProject("Previous backup"));
            var previous = File.ReadAllBytes(path); var previousTime = File.GetLastWriteTimeUtc(path);
            var backup = File.ReadAllBytes(path + ".bak"); var backupTime = File.GetLastWriteTimeUtc(path + ".bak");
            var identity = PrepareStaging(staged); var prepared = File.ReadAllBytes(staged);
            if (kind == "hardlink") Assert(CreateHardLink(retained, staged, IntPtr.Zero), "Could not create shared-stage alias.");
            else
            {
                File.Move(staged, retained);
                if (kind == "junction") StagingJunction(staged, personal);
                else if (kind == "directory") Directory.CreateDirectory(staged);
                else File.WriteAllText(staged, "foreign replacement");
            }
            await Expect<InvalidDataException>(() => Task.Run(() => StudioProjectStore.PublishProject(staged, path, keepBackup, [], identity: identity)));
            StudioProjectStore.CleanupStaging(staged, identity);
            Assert(File.ReadAllBytes(path).SequenceEqual(previous) && File.GetLastWriteTimeUtc(path) == previousTime
                && File.ReadAllBytes(path + ".bak").SequenceEqual(backup) && File.GetLastWriteTimeUtc(path + ".bak") == backupTime
                && File.ReadAllBytes(retained).SequenceEqual(prepared)
                && File.ReadAllBytes(personalPath).SequenceEqual(personalBytes) && File.GetLastWriteTimeUtc(personalPath) == personalTime,
                "Rejected staging changed project, backup, retained owned file or junction target.");
            if (kind == "replacement") Assert(File.ReadAllText(staged) == "foreign replacement", "Cleanup removed foreign staging.");
            else if (kind == "hardlink") Assert(File.ReadAllBytes(staged).SequenceEqual(prepared), "Cleanup changed shared staging.");
            else Assert(Directory.Exists(staged), "Cleanup removed the replacement folder or junction.");
            if (kind == "junction") Directory.Delete(staged);
        }
    }

    private static void CheckStagingCleanup(string root)
    {
        var directory = Path.Combine(root, "cleanup"); Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, "stage.tmp"); var retained = staged + ".retained";
        var identity = PrepareStaging(staged); var prepared = File.ReadAllBytes(staged);
        using var log = new StringWriter(); using var listener = new TextWriterTraceListener(log);
        Trace.Listeners.Add(listener);
        try
        {
            using (var holder = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var waits = new List<int>();
                StudioProjectStore.CleanupStaging(staged, identity, delay =>
                {
                    waits.Add(delay); holder.Dispose(); File.Move(staged, retained); File.WriteAllText(staged, "foreign cleanup replacement");
                });
                Assert(waits.SequenceEqual(new[] { 25 }) && File.ReadAllText(staged) == "foreign cleanup replacement"
                    && File.ReadAllBytes(retained).SequenceEqual(prepared), "Cleanup retry did not recheck staging ownership.");
            }
            listener.Flush();
            Assert(log.ToString().Contains(staged) && log.ToString().Contains("identity changed"), "Retained foreign staging was not logged.");

            var foreign = Path.Combine(directory, "never-owned.tmp"); File.WriteAllText(foreign, "never-owned sentinel");
            StudioProjectStore.CleanupStaging(foreign, null);
            Assert(File.ReadAllText(foreign) == "never-owned sentinel", "Cleanup deleted a path whose creation was never recorded.");

            var locked = Path.Combine(directory, "locked.tmp"); var lockedIdentity = PrepareStaging(locked);
            var lockedBytes = File.ReadAllBytes(locked); var waitsForLock = new List<int>();
            using (var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StudioProjectStore.CleanupStaging(locked, lockedIdentity, waitsForLock.Add);
                Assert(waitsForLock.SequenceEqual(new[] { 25, 50, 100 }) && File.ReadAllBytes(locked).SequenceEqual(lockedBytes),
                    "Permanent cleanup lock was bypassed or escaped its retry budget.");
            }
            StudioProjectStore.CleanupStaging(locked, lockedIdentity);
            StudioProjectStore.CleanupStaging(locked, lockedIdentity);
            Assert(!File.Exists(locked), "Owned staging was not cleaned after its lock cleared or missing-stage cleanup failed.");

            var published = Path.Combine(directory, "published.json"); var committedStage = published + ".tmp";
            var committedIdentity = PrepareStaging(committedStage);
            StudioProjectStore.PublishProject(committedStage, published, false, [], identity: committedIdentity);
            var committedBytes = File.ReadAllBytes(published);
            File.WriteAllText(committedStage, "foreign reuse after commit");
            StudioProjectStore.CleanupStaging(committedStage, committedIdentity);
            Assert(File.ReadAllText(committedStage) == "foreign reuse after commit" && File.ReadAllBytes(published).SequenceEqual(committedBytes),
                "Post-commit cleanup deleted a reused filename or changed the published project.");

            var primary = new IOException("original project write failure");
            try
            {
                try { throw primary; }
                finally { StudioProjectStore.CleanupStaging(staged, identity); }
            }
            catch (IOException error) { Assert(ReferenceEquals(error, primary), "Ownership-aware cleanup masked the original write failure."); }
            Assert(File.ReadAllText(staged) == "foreign cleanup replacement", "Failure cleanup deleted a foreign replacement.");
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private static async Task CheckPreferenceStaging(string root)
    {
        foreach (var kind in new[] { "replacement", "hardlink", "junction" })
        {
            var directory = Path.Combine(root, "preferences-" + kind); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "preferences.json");
            var prior = new StudioPreferences(["old favorite"], ["old recent"], false);
            var next = new StudioPreferences(["new favorite"], ["new recent"], true);
            StudioPreferences.Write(path, prior.ToDocument());
            var previous = File.ReadAllBytes(path); var previousTime = File.GetLastWriteTimeUtc(path);
            var unrelated = Path.Combine(directory, "unrelated.tmp"); File.WriteAllText(unrelated, "unrelated sentinel");
            var personal = Path.Combine(directory, "personal"); Directory.CreateDirectory(personal);
            var personalPath = Path.Combine(personal, "personal.txt"); File.WriteAllText(personalPath, "personal sentinel");
            var personalTime = File.GetLastWriteTimeUtc(personalPath);
            string? staged = null; string? retained = null; byte[]? prepared = null; var waits = new List<int>();
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Expect<InvalidDataException>(() => Task.Run(() => StudioPreferences.Write(path, next.ToDocument(), delay =>
                {
                    waits.Add(delay); holder.Dispose();
                    staged = Directory.GetFiles(directory, ".preferences-*.tmp").Single(); retained = staged + ".retained";
                    prepared = File.ReadAllBytes(staged);
                    if (kind == "hardlink") Assert(CreateHardLink(retained, staged, IntPtr.Zero), "Could not create shared preferences staging.");
                    else
                    {
                        File.Move(staged, retained);
                        if (kind == "junction") StagingJunction(staged, personal);
                        else File.WriteAllText(staged, "foreign preference replacement");
                    }
                })));
            }
            Assert(waits.SequenceEqual(new[] { 25 }) && staged is not null && retained is not null && prepared is not null
                && File.ReadAllBytes(path).SequenceEqual(previous) && File.GetLastWriteTimeUtc(path) == previousTime
                && File.ReadAllBytes(retained).SequenceEqual(prepared)
                && File.ReadAllText(unrelated) == "unrelated sentinel" && File.ReadAllText(personalPath) == "personal sentinel"
                && File.GetLastWriteTimeUtc(personalPath) == personalTime, "Preferences staging rejection changed settings or personal files.");
            if (kind == "replacement") Assert(File.ReadAllText(staged!) == "foreign preference replacement", "Foreign preference staging was removed.");
            else if (kind == "hardlink") Assert(File.ReadAllBytes(staged!).SequenceEqual(prepared!), "Shared preference staging was removed.");
            else Assert(new DirectoryInfo(staged!).LinkTarget is not null, "Preference staging junction was removed.");
            StudioPreferences.Write(path, next.ToDocument());
            Assert(StudioPreferences.Read(path).Dark && StudioPreferences.Read(path).Favorites.SequenceEqual(next.Favorites), "Preferences did not recover after staging rejection.");
            if (kind == "junction") Directory.Delete(staged!);
        }
    }

    private static void CheckLongStaging(string root)
    {
        var directory = Path.Combine(root, "long-path", new string('a', 80), new string('b', 80), new string('c', 80));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "project.json");
        Assert(path.Length > 260, "Long staging fixture is not long enough.");
        StudioProjectStore.Write(path, PublicationProject("Long initial"));
        StudioProjectStore.Write(path, PublicationProject("Long replacement"), keepBackup: true);
        Assert(StudioProjectStore.Read(path)["projectName"]!.GetValue<string>() == "Long replacement"
            && StudioProjectStore.Read(path + ".bak")["projectName"]!.GetValue<string>() == "Long initial", "Long project staging failed publication or backup.");
        var preferences = Path.Combine(directory, "preferences.json");
        StudioPreferences.Write(preferences, new StudioPreferences(["long favorite"], [], true).ToDocument());
        Assert(StudioPreferences.Read(preferences).Dark, "Long preference staging failed.");
        var staged = Path.Combine(directory, "cleanup.tmp"); var identity = PrepareStaging(staged);
        StudioProjectStore.CleanupStaging(staged, identity);
        Assert(!File.Exists(staged) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Long staging cleanup failed.");
    }
}
