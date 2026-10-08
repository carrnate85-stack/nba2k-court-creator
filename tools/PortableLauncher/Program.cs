using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--extract-only")
            {
                var destination = Path.GetFullPath(args[1]);
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException("Extraction destination must not already exist.");
                Extract(destination, _ => { });
                return 0;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new LauncherWindow());
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0) File.WriteAllText(Path.Combine(Path.GetTempPath(), "court-portable-error.txt"), error.ToString());
            else MessageBox.Show(error.Message, "Court Creator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static void Extract(string destination, Action<string> progress)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CourtPayload.zip")
            ?? throw new IOException("The bundled application is missing.");
        var digest = Convert.ToHexString(SHA256.HashData(resource));
        if (!digest.Equals(BundleInfo.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The bundled application did not pass its integrity check.");
        resource.Position = 0;
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        if (zip.Entries.Count > 12000 || zip.Entries.Sum(item => item.Length) > 1024L * 1024 * 1024)
            throw new IOException("The bundle exceeds its extraction limits.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName) || entry.Length > 256L * 1024 * 1024)
                throw new IOException("The bundle contains a duplicate or oversized file.");
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Contains(':') || entry.FullName.Contains('\\'))
                throw new IOException("The bundle contains an invalid file path.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            if (target.Length != entry.Length) throw new IOException("A bundled file was truncated.");
        }
        foreach (var required in new[] { "desktop/NBA2KCourtCreator.exe", "runtime/python/python.exe", "court_creator/service.py", "tools/prepare_portable.py" })
            if (!File.Exists(Path.Combine(destination, required))) throw new IOException("A required bundled file is missing: " + required);
        File.WriteAllText(Path.Combine(destination, ".portable-ready"), BundleInfo.Hash);
        progress("Application files are ready.");
    }
}

internal sealed class LauncherWindow : Form
{
    readonly Label status = new() { Dock = DockStyle.Fill, Padding = new Padding(24), Text = "Preparing Court Creator…" };
    bool busy = true;

    public LauncherWindow()
    {
        Text = "NBA 2K Court Creator"; Width = 520; Height = 180;
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true;
        Controls.Add(status);
        Controls.Add(new ProgressBar { Dock = DockStyle.Bottom, Height = 8, Style = ProgressBarStyle.Marquee });
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += async (_, _) => await StartAsync();
    }

    void Progress(string message)
    {
        if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message));
    }

    async Task StartAsync()
    {
        try
        {
            var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2K Court Creator Portable");
            Directory.CreateDirectory(parent);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("The portable application folder must not be a linked folder.");
            var root = Path.Combine(parent, BundleInfo.Hash[..16]);
            using var mutex = new Mutex(false, "Local\\CourtCreatorPortable-" + BundleInfo.Hash[..16]);
            // A named mutex is thread-affine; hold it on the worker that provisions the bundle.
            await Task.Run(() =>
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(15)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Another Court Creator setup is still running. Try again shortly.");
                try
                {
                    var marker = Path.Combine(root, ".portable-ready");
                    if (!File.Exists(marker) || File.ReadAllText(marker) != BundleInfo.Hash)
                    {
                        if (Directory.Exists(root)) throw new IOException("The portable cache is incomplete. Preserve it and use a newly built bundle.");
                        var candidate = Path.Combine(parent, "prepare-" + Guid.NewGuid().ToString("N"));
                        Progress("Unpacking the app and bundled runtimes…");
                        Program.Extract(candidate, Progress);
                        Directory.Move(candidate, root);
                    }
                }
                finally { mutex.ReleaseMutex(); }
            });
            string? game = null;
            while (true)
            {
                var start = new ProcessStartInfo(Path.Combine(root, "runtime/python/python.exe"))
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add("tools/prepare_portable.py");
                if (game is not null) { start.ArgumentList.Add("--game-root"); start.ArgumentList.Add(game); }
                using var process = Process.Start(start) ?? throw new IOException("The bundled Python runtime could not start.");
                var errors = process.StandardError.ReadToEndAsync();
                while (await process.StandardOutput.ReadLineAsync() is { } line) Progress(line);
                await process.WaitForExitAsync();
                var error = await errors;
                if (process.ExitCode == 0) break;
                if (process.ExitCode != 2) throw new IOException(error.Length > 2000 ? error[^2000..] : error);
                using var chooser = new FolderBrowserDialog { Description = "Choose your installed NBA 2K27 folder (containing manifest and mod.exe)", UseDescriptionForTitle = true };
                if (chooser.ShowDialog(this) != DialogResult.OK) { busy = false; Close(); return; }
                game = chooser.SelectedPath;
            }
            var app = new ProcessStartInfo(Path.Combine(root, "desktop/NBA2KCourtCreator.exe")) { WorkingDirectory = root, UseShellExecute = false };
            app.Environment["COURT_CREATOR_ROOT"] = root;
            Process.Start(app);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Court Creator could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; Close(); }
    }
}
