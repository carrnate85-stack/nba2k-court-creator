using System.Configuration;
using System.Data;
using System.Windows;

namespace NBA2KCourtCreator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\TwoK.CourtCreator.Studio." + Environment.UserName, out _ownsInstance);
        if (!_ownsInstance)
        {
            var existing = System.Diagnostics.Process.GetProcessesByName("NBA2KCourtCreator").FirstOrDefault(process => process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero);
            if (existing is not null) SetForegroundWindow(existing.MainWindowHandle);
            SignalWindowVisible();
            Shutdown(); return;
        }
        TwoK.Studio.StudioTheme.Apply(false);
        MainWindow = new Studio.StudioWindow();
        MainWindow.ContentRendered += (_, _) => SignalWindowVisible();
        MainWindow.Show();
    }
    private static void SignalWindowVisible()
    {
        var name = Environment.GetEnvironmentVariable("COURT_CREATOR_READY_EVENT");
        if (name is null || !name.StartsWith("Local\\CourtCreatorWindowReady-", StringComparison.Ordinal)) return;
        try { using var ready = EventWaitHandle.OpenExisting(name); ready.Set(); }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
    }
    protected override void OnExit(ExitEventArgs e) { if (_ownsInstance) _instance?.ReleaseMutex(); _instance?.Dispose(); base.OnExit(e); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

