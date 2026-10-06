using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static void CheckPortableAssetPublication(string output)
    {
        var root = Path.GetFullPath(Path.Combine(output, "pa-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.png"); WriteSolidImage(source, Colors.Firebrick);
        var path = Path.Combine(root, "saved.court.json");
        JsonObject Project() => new() { ["version"] = 2, ["buildMode"] = "game-uv",
            ["floor"] = new JsonObject { ["id"] = "fixture", ["name"] = "Fixture", ["path"] = source, ["sourceRevision"] = StudioImages.FileRevision(source) } };
        StudioProjectStore.Write(path, Project()); StudioProjectStore.Write(path + ".bak", Project());
        var previous = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
        var sourceBytes = File.ReadAllBytes(source); var sourceTime = File.GetLastWriteTimeUtc(source);
        string? staged = null; string? destination = null; byte[]? changed = null;
        try
        {
            StudioProjectAssets.Save(path, Project(), root, (stage, target) =>
            {
                staged = stage; destination = target; EditManagedFile(stage); changed = File.ReadAllBytes(stage);
            });
            throw new Exception("Portable save published externally edited bytes under the original content hash and replaced the good project.");
        }
        catch (InvalidDataException) { }
        Assert(File.ReadAllBytes(path).SequenceEqual(previous) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "Rejected portable asset changed the good project/backup.");
        Assert(staged is not null && File.Exists(staged) && File.ReadAllBytes(staged).SequenceEqual(changed!), "Portable cleanup deleted externally changed staging bytes.");
        Assert(destination is not null && !File.Exists(destination), "Changed artwork was published under its original content hash.");
        Assert(File.ReadAllBytes(source).SequenceEqual(sourceBytes) && File.GetLastWriteTimeUtc(source) == sourceTime, "Portable save changed original artwork.");
        CheckPortableCopies(root);
        CheckPortableRetry(root);
        CheckPortableCommitPins(root);
        Console.WriteLine("PASS portable asset publication: reproduced wrong-hash publication; bounded copy/hash/readback, creation identities, exact-limit preflight, partial-failure cleanup; same-size/time edits, replacements, hard links/junctions and concurrent destination changes protected; ordinary dedup/repair/concurrent copies; bounded real Windows lock/retry guards; all bundled assets rechecked before project commit; original project/backup/snapshot/source retained on failure. No native windows opened.");
    }

    private static void CheckPortableCopies(string root)
    {
        var source = Path.Combine(root, "copy-source.bin"); var bytes = new byte[230595]; new Random(76).NextBytes(bytes); File.WriteAllBytes(source, bytes);
        var revision = ManagedHash(source).ToLowerInvariant(); var time = File.GetLastWriteTimeUtc(source);
        foreach (var action in new[] { "ordinary", "repair", "edit", "replace", "hardlink", "junction", "destination-create", "partial-error", "error-after-edit", "directory-lock", "source-lock" })
        {
            var directory = Path.Combine(root, "copy-" + action); Directory.CreateDirectory(directory);
            var sentinel = Path.Combine(directory, "personal.txt"); File.WriteAllText(sentinel, "Personal file stays.");
            var destination = Path.Combine(directory, revision + ".bin");
            if (action == "repair") File.WriteAllText(destination, "corrupted previous bundle");
            string? staged = null; string? displaced = null; string? alias = null; var chunks = 0;
            var primary = new IOException("Controlled copy failure.");
            var rejected = action is not "ordinary" and not "repair" and not "source-lock";
            try
            {
                var receipt = StudioPortableAssets.Bundle(source, directory, revision, [source],
                    prepared: (stage, target) =>
                    {
                        staged = stage;
                        if (action is "edit" or "error-after-edit") EditManagedFile(stage);
                        if (action is "replace" or "junction")
                        {
                            displaced = stage + ".owned"; File.Move(stage, displaced);
                            if (action == "replace") File.Copy(displaced, stage);
                            else StagingJunction(stage, root);
                        }
                        if (action == "hardlink") { alias = stage + ".alias"; Assert(CreateHardLink(alias, stage, IntPtr.Zero), "Could not create portable stage hard link."); }
                        if (action == "destination-create") File.WriteAllText(target, "Unexpected destination bytes.");
                        if (action == "error-after-edit") throw primary;
                        if (action == "directory-lock")
                        {
                            try { Directory.Move(directory, directory + ".moved"); throw new Exception("Portable directory lifetime handle did not deny rename."); }
                            catch (IOException) { }
                            throw primary;
                        }
                        if (action == "source-lock")
                        {
                            try { using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new Exception("Portable source was not read-pinned."); }
                            catch (IOException) { }
                        }
                    }, copiedChunk: (stage, total) =>
                    {
                        staged = stage; chunks++;
                        if (action == "partial-error" && chunks == 2) throw primary;
                    });
                if (rejected) throw new Exception("Changed/failed portable copy was published: " + action);
                receipt.Validate(); Assert(receipt.Hash == revision && File.ReadAllBytes(receipt.Path).SequenceEqual(bytes), "Portable copy/repair pixels changed.");
                var beforeTime = File.GetLastWriteTimeUtc(receipt.Path);
                var repeated = StudioPortableAssets.Bundle(source, directory, revision, [source], prepared: (_, _) => throw new Exception("Reusable portable asset was recopied."));
                Assert(repeated == receipt && File.GetLastWriteTimeUtc(receipt.Path) == beforeTime && chunks == 4, "Portable dedup changed identity/time or copy chunk count.");
            }
            catch (Exception error) when (rejected && error is IOException or InvalidDataException)
            {
                if (action is "partial-error" or "error-after-edit" or "directory-lock") Assert(ReferenceEquals(error, primary), "Portable cleanup masked its original error.");
                var retained = action is "edit" or "replace" or "hardlink" or "junction" or "error-after-edit";
                Assert(staged is not null && (File.Exists(staged) || Directory.Exists(staged)) == retained, "Portable stage cleanup ownership was incorrect: " + action);
                if (action == "destination-create") Assert(File.ReadAllText(destination) == "Unexpected destination bytes.", "New foreign target was overwritten.");
                else Assert(!File.Exists(destination), "Rejected portable stage was published.");
                if (displaced is not null) Assert(File.ReadAllBytes(displaced).SequenceEqual(bytes), "Displaced owned stage changed.");
                if (alias is not null) Assert(File.ReadAllBytes(alias).SequenceEqual(bytes), "Shared portable alias changed.");
                if (action == "junction") Assert(File.ReadAllText(sentinel) == "Personal file stays." && Directory.Exists(root), "Portable stage junction target was deleted.");
            }
            finally
            {
                if (action == "junction" && staged is not null && Directory.Exists(staged)) Directory.Delete(staged, false);
                if (alias is not null && File.Exists(alias)) File.Delete(alias);
            }
            Assert(File.ReadAllText(sentinel) == "Personal file stays.", "Portable cleanup removed an unrelated file.");
            Assert(File.ReadAllBytes(source).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(source) == time, "Portable copy changed its source.");
        }
        var oversizedDirectory = Path.Combine(root, "too-large");
        try { StudioPortableAssets.Bundle(source, oversizedDirectory, revision, [source], maximumBytes: bytes.Length - 1); throw new Exception("Oversized portable source was copied."); }
        catch (InvalidDataException) { }
        Assert(!Directory.Exists(oversizedDirectory), "Portable source limit was enforced only after storage creation.");
        var exact = StudioPortableAssets.Bundle(source, Path.Combine(root, "exact-limit"), revision, [source], maximumBytes: bytes.Length); exact.Validate(bytes.Length);
        var sharedSource = source + ".alias"; Assert(CreateHardLink(sharedSource, source, IntPtr.Zero), "Could not create borrowed source alias.");
        try { StudioPortableAssets.Bundle(sharedSource, Path.Combine(root, "borrowed-shared"), revision, [source, sharedSource]).Validate(); }
        finally { File.Delete(sharedSource); }
        var concurrentDirectory = Path.Combine(root, "concurrent");
        var concurrent = Enumerable.Range(0, 12).Select(_ => Task.Run(() => StudioPortableAssets.Bundle(source, concurrentDirectory, revision, [source]))).ToArray();
        Task.WaitAll(concurrent);
        foreach (var task in concurrent) task.Result.Validate();
        Assert(concurrent.All(task => task.Result == concurrent[0].Result) && Directory.GetFiles(concurrentDirectory).Length == 1, "Concurrent portable copies did not converge on one complete file or left staging behind.");
    }

    private static void CheckPortableRetry(string root)
    {
        var source = Path.Combine(root, "copy-source.bin"); var revision = ManagedHash(source).ToLowerInvariant();
        foreach (var action in new[] { "transient", "persistent", "target-edit", "target-replace", "stage-edit", "stage-replace", "target-hardlink" })
        {
            var directory = Path.Combine(root, "retry-" + action); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, revision + ".bin"); File.WriteAllText(destination, "Previous corrupt portable file.");
            var previous = File.ReadAllBytes(destination); var waits = new List<int>(); string? stage = null; string? retained = null;
            using var holder = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var succeeds = action == "transient";
            try
            {
                var receipt = StudioPortableAssets.Bundle(source, directory, revision, [source], prepared: (prepared, _) => stage = prepared,
                    wait: delay =>
                    {
                        waits.Add(delay); if (action == "persistent") return;
                        holder.Dispose();
                        if (action == "target-edit") EditManagedFile(destination);
                        if (action == "stage-edit") EditManagedFile(stage!);
                        if (action is "target-replace" or "stage-replace")
                        {
                            var path = action == "target-replace" ? destination : stage!; retained = path + ".owned"; File.Move(path, retained); File.Copy(retained, path);
                        }
                        if (action == "target-hardlink") { File.Delete(destination); Assert(CreateHardLink(destination, source, IntPtr.Zero), "Could not create retry target alias."); }
                    });
                Assert(succeeds, "Portable publication bypassed an external retry-time change: " + action); receipt.Validate();
                Assert(waits.SequenceEqual(new[] { 25 }), "Transient portable lock had incorrect retry count.");
            }
            catch (Exception error) when (!succeeds && error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Assert(waits.SequenceEqual(action == "persistent" ? new[] { 25, 50, 100 } : new[] { 25 }), "Portable retries were not bounded or rechecked: " + action);
                if (action == "target-edit") Assert(!File.ReadAllBytes(destination).SequenceEqual(previous), "Changed target was replaced or restored.");
                else if (action != "target-hardlink") Assert(File.ReadAllBytes(destination).SequenceEqual(previous), "Rejected portable publication changed its old destination.");
                Assert(stage is not null && File.Exists(stage) == (action is "stage-edit" or "stage-replace"), "Retry cleanup removed foreign stage or retained owned stage: " + action);
                if (retained is not null) Assert(File.Exists(retained), "Portable retry deleted a displaced file.");
            }
            finally { holder.Dispose(); if (action == "target-hardlink") File.Delete(destination); }
        }
    }

    private static void CheckPortableCommitPins(string root)
    {
        var directory = Path.Combine(root, "commit-pins"); Directory.CreateDirectory(directory);
        var floor = Path.Combine(directory, "floor.png"); var logo = Path.Combine(directory, "logo.png");
        WriteSolidImage(floor, Colors.Firebrick); WriteSolidImage(logo, Colors.SteelBlue);
        var project = new JsonObject { ["version"] = 2, ["buildMode"] = "game-uv",
            ["floor"] = new JsonObject { ["id"] = "floor", ["name"] = "Floor", ["path"] = floor },
            ["logoImages"] = new JsonArray(new JsonObject { ["id"] = "logo", ["name"] = "Logo", ["path"] = logo }) };
        var path = Path.Combine(directory, "pinned.court.json"); StudioProjectStore.Write(path, project); StudioProjectStore.Write(path + ".bak", project);
        var original = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak"); var snapshot = project.ToJsonString();
        var calls = 0; string? first = null;
        try
        {
            StudioProjectAssets.Save(path, project, root, (_, target) =>
            {
                if (++calls == 1) first = target;
                else if (calls == 2) EditManagedFile(first!);
            });
            throw new Exception("Project JSON committed after an earlier bundled asset was externally edited.");
        }
        catch (InvalidDataException) { }
        Assert(calls == 2 && File.ReadAllBytes(path).SequenceEqual(original) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup)
            && project.ToJsonString() == snapshot && first is not null && File.Exists(first), "Portable asset validation did not protect project/backup/snapshot/foreign artwork.");
        File.WriteAllBytes(first!, File.ReadAllBytes(floor));
        var receipt = new StudioPortableAssets.Receipt(first!, StudioImages.FileRevision(floor), new FileInfo(floor).Length, StudioFileSafety.StagingIdentity(first!));
        var stagedProject = path + ".publication.tmp"; File.WriteAllText(stagedProject, project.ToJsonString()); var attempts = 0;
        using (var lockProject = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                StudioProjectStore.PublishProject(stagedProject, path, true, [], wait: _ => { lockProject.Dispose(); EditManagedFile(first!); },
                    validateAssets: () => { attempts++; receipt.Validate(); });
                throw new Exception("Project publication did not revalidate portable assets after a sharing retry.");
            }
            catch (InvalidDataException) { }
        }
        Assert(attempts == 2 && File.ReadAllBytes(path).SequenceEqual(original) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "Project retry committed stale portable assets.");
    }
}
