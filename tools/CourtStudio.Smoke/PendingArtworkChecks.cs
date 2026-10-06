using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NBA2KCourtCreator.Studio;

internal static partial class Program
{
    private static async Task CheckPendingArtwork(string output)
    {
        var source = Path.GetFullPath(Path.Combine(output, "pending-logo.png")); WriteLogoExample(source);
        var image = StudioImages.Load(source);
        var held = new List<(TaskCompletionSource<PreparedLogoAsset> Completion, CancellationToken Token)>();
        var hold = false;
        Task<PreparedLogoAsset> Prepare(string path, CancellationToken token)
        {
            if (!hold) return Task.FromResult(new PreparedLogoAsset(image, path));
            var completion = new TaskCompletionSource<PreparedLogoAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            held.Add((completion, token)); return completion.Task;
        }
        string Complete(int index)
        {
            var owned = Path.GetFullPath(Path.Combine(output, "pending-owned-" + Guid.NewGuid().ToString("N") + ".png"));
            File.Copy(source, owned); held[index].Completion.SetResult(new PreparedLogoAsset(image, owned, true)); return owned;
        }
        var root = ProjectRoot();
        var window = new StudioWindow(true, new PythonServiceClient(root, null), new PythonServiceClient(root, null), Prepare);
        List<JsonObject> History(string field) => (List<JsonObject>)typeof(StudioWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        int UndoCount() => History("_undo").Count;
        void AssertControls(bool pending)
        {
            Assert(((Button)window.FindName("SaveToolbarButton")).IsEnabled == !pending && ((MenuItem)window.FindName("SaveMenuItem")).IsEnabled == !pending && ((Button)window.FindName("ExportTopButton")).IsEnabled == !pending, "Pending imports did not protect Save/Export consistently.");
            Assert(((FrameworkElement)window.FindName("ExportPanel")).IsEnabled == !pending && ((Button)window.FindName("BuildIffButton")).IsEnabled == !pending, "Secondary export actions were still available during a reserved logo import.");
            Assert(((FrameworkElement)window.FindName("WorkspaceRoot")).IsEnabled && ((Menu)window.FindName("ApplicationMenu")).IsEnabled, "Pending logo loading unnecessarily disabled document replacement or editing.");
        }
        try
        {
            await window.InitializeAsync();
            for (var index = 0; index < 3; index++) await window.AddLogoAsync(source, "Existing " + index);
            hold = true; var historyBefore = UndoCount(); var fourth = window.AddLogoAsync(source, "Fourth");
            Assert(window.PendingLogoImports == 1 && window.Canvas.Layers.Count == 3, "Loading artwork was not reserved separately from committed layers.");
            AssertControls(true);
            Assert(!((Button)window.FindName("ImportLogoButton")).IsEnabled && !((Button)window.FindName("DuplicateLogoButton")).IsEnabled && !((Button)window.FindName("SelectedCourtCard")).IsEnabled, "Reserved fourth slot or hardwood mutation was still available.");
            await Expect<InvalidOperationException>(() => window.AddLogoAsync(source, "Fifth"));
            await Expect<InvalidOperationException>(() => window.SaveProjectToAsync(Path.Combine(output, "pending-should-not-save.json")));
            await Expect<InvalidOperationException>(() => window.ExportToAsync(Path.Combine(output, "pending-should-not-export.png"), false));
            await Expect<InvalidOperationException>(() => window.SelectFloorAsync(window.Floors[1]));
            typeof(StudioWindow).GetMethod("DuplicateLogo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
            Assert(window.Canvas.Layers.Count == 3 && window.PendingLogoImports == 1, "A racing duplicate consumed a reserved logo slot.");
            var committed = Complete(0); await fourth;
            Assert(window.PendingLogoImports == 0 && window.Canvas.Layers.Count == 4 && UndoCount() == historyBefore + 1 && window.Canvas.Layers.Contains(window.Canvas.SelectedLayer!) && File.Exists(committed), "Completed import did not produce one valid layer/history action.");
            AssertControls(false);
            hold = false;
            await window.UndoAsync(); Assert(window.Canvas.Layers.Count == 3, "Import undo did not remove exactly one logo.");
            await window.UndoAsync(true); Assert(window.Canvas.Layers.Count == 4, "Import redo lost its managed source.");
            hold = true;

            await window.NewProjectAsync(); var pendingNew = window.AddLogoAsync(source, "Obsolete New");
            await window.NewProjectAsync(); var afterNew = window.CreateProject().ToJsonString(); var newHistory = UndoCount();
            Assert(held[1].Token.IsCancellationRequested && window.PendingLogoImports == 0, "New did not invalidate the pending document operation.");
            AssertControls(false); var obsoleteNew = Complete(1); await pendingNew;
            Assert(!File.Exists(obsoleteNew) && window.CreateProject().ToJsonString() == afterNew && UndoCount() == newHistory && window.Canvas.SelectedLayer is null, "Late artwork changed New's court, selection, history, or leaked its staged asset.");

            var openedPath = Path.GetFullPath(Path.Combine(output, "pending-open.court.json"));
            var opened = window.CreateProject(); opened["projectName"] = "Opened court"; StudioProjectStore.Write(openedPath, opened);
            var pendingOpen = window.AddLogoAsync(source, "Obsolete Open"); await window.OpenProjectFromAsync(openedPath);
            var afterOpen = window.CreateProject().ToJsonString(); var obsoleteOpen = Complete(2); await pendingOpen;
            Assert(!File.Exists(obsoleteOpen) && window.CreateProject().ToJsonString() == afterOpen && UndoCount() == 0 && window.Canvas.SelectedLayer is null, "Late artwork changed the opened project or its cleared history.");

            window.SetLayerSettings("stock-outside", color: "#334455"); var pendingUndo = window.AddLogoAsync(source, "Obsolete Undo");
            await window.UndoAsync(); var afterUndo = window.CreateProject().ToJsonString(); var undoneHistory = UndoCount();
            held[3].Completion.SetException(new IOException("old document load failure")); await pendingUndo;
            Assert(window.CreateProject().ToJsonString() == afterUndo && UndoCount() == undoneHistory && window.PendingLogoImports == 0, "A failure from an old document altered the restored court.");

            var beforeFailure = window.CreateProject().ToJsonString(); var failure = window.AddLogoAsync(source, "Failed import");
            held[4].Completion.SetException(new IOException("current document load failure")); await Expect<IOException>(() => failure);
            Assert(window.CreateProject().ToJsonString() == beforeFailure && window.PendingLogoImports == 0 && window.Canvas.SelectedLayer is null, "Failed current import changed the document or left a reservation/orphan selection.");
            AssertControls(false);

            var originalColor = window.CreateProject()["outsideColor"]!.GetValue<string>();
            var independent = window.AddLogoAsync(source, "Independent edit"); window.SetLayerSettings("stock-outside", color: "#BBAACC");
            Complete(5); await independent;
            await window.UndoAsync(); Assert(window.Canvas.Layers.Count == 0 && window.CreateProject()["outsideColor"]!.GetValue<string>() == "#BBAACC", "Import undo swallowed an independent edit made while loading.");
            await window.UndoAsync(); Assert(window.CreateProject()["outsideColor"]!.GetValue<string>() == originalColor, "Independent edit history was not preserved.");

            var closing = window.AddLogoAsync(source, "Obsolete Close"); window.Close();
            Assert(held[6].Token.IsCancellationRequested, "Closing did not signal pending artwork cancellation.");
            var obsoleteClose = Complete(6); await closing;
            Assert(!File.Exists(obsoleteClose) && window.Canvas.Layers.Count == 0 && window.Canvas.SelectedLayer is null, "Closed workspace accepted or leaked late artwork.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS pending artwork: reserved four-slot capacity, duplicate/save/export/floor guards, one undo action, New/Open/Undo/Close invalidation, stale failure suppression, staged-file cleanup, independent edit history.");
    }
}
