using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Drawing.Drawing2D;

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
            if (args.Length == 2 && args[0] == "--spinner-preview")
            {
                using var spinner = new CircularSpinner { Size = new Size(32, 32), BackColor = Color.White };
                using var bitmap = new Bitmap(32, 32); spinner.DrawToBitmap(bitmap, new Rectangle(0, 0, 32, 32)); bitmap.Save(args[1]);
                return 0;
            }
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
        using var resource = OpenPayload();
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

    static Stream OpenPayload()
    {
        var file = File.OpenRead(Environment.ProcessPath ?? throw new IOException("The launcher path is unavailable."));
        try
        {
            file.Seek(-64, SeekOrigin.End);
            using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
            var offset = reader.ReadInt64(); var length = reader.ReadInt64();
            var hash = Convert.ToHexString(reader.ReadBytes(32));
            var magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(16));
            if (magic != "CourtBundleZipV1" || offset <= 0 || length <= 0 || offset + length != file.Length - 64
                || !hash.Equals(BundleInfo.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The portable bundle footer is invalid.");
            return new PayloadStream(file, offset, length);
        }
        catch { file.Dispose(); throw; }
    }
}

internal sealed class PayloadStream(FileStream file, long offset, long length) : Stream
{
    long position;
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int start, int count)
    {
        file.Position = offset + position; var read = file.Read(buffer, start, (int)Math.Min(count, length - position)); position += read; return read;
    }
    public override long Seek(long value, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => value, SeekOrigin.Current => position + value, _ => length + value };
        if (target < 0 || target > length) throw new IOException("Payload seek outside its bounds.");
        return position = target;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
}

internal sealed class CircularSpinner : Control
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    public CircularSpinner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        timer.Tick += (_, _) => Invalidate(); timer.Start();
        AccessibleName = "Opening Court Creator";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(3, 3, Width - 7, Height - 7);
        using var track = new Pen(Color.FromArgb(228, 233, 239), 2.5f);
        using var arc = new Pen(Color.FromArgb(48, 108, 220), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawEllipse(track, bounds);
        e.Graphics.DrawArc(arc, bounds, (float)(clock.Elapsed.TotalSeconds * 270 % 360), 95);
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class LauncherWindow : Form
{
    readonly Label status = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 22, 12, 0), Text = "Opening Court Creator…", AutoEllipsis = true };
    bool busy = true;

    public LauncherWindow()
    {
        Text = "NBA 2K Court Creator"; Width = 390; Height = 120; BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true;
        Controls.Add(status);
        var left = new Panel { Dock = DockStyle.Left, Width = 64, BackColor = Color.White };
        left.Controls.Add(new CircularSpinner { Left = 20, Top = 20, Width = 26, Height = 26, BackColor = Color.White }); Controls.Add(left);
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
            Progress("Opening the studio…");
            var readyName = "Local\\CourtCreatorWindowReady-" + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            var app = new ProcessStartInfo(Path.Combine(root, "desktop/NBA2KCourtCreator.exe")) { WorkingDirectory = root, UseShellExecute = false };
            app.Environment["COURT_CREATOR_ROOT"] = root;
            app.Environment["COURT_CREATOR_PREPARE_FLOORS"] = "1";
            app.Environment["COURT_CREATOR_READY_EVENT"] = readyName;
            using var studio = Process.Start(app) ?? throw new IOException("Court Creator could not start.");
            await Task.Run(() => { var watch = Stopwatch.StartNew(); while (!ready.WaitOne(100) && !studio.HasExited && watch.Elapsed < TimeSpan.FromSeconds(20)) { } });
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Court Creator could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; Close(); }
    }
}
