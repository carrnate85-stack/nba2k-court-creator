using System.ComponentModel;
using System.Windows;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private readonly StudioSnapshotWriter? _recovery;
    private bool _closePending, _allowClose;
    internal bool RecoveryClosePending => _closePending;
    internal Task<bool>? PendingCloseRecovery { get; private set; }

    internal Task<StudioWriteResult>? QueueRecovery()
        => _recovery is null || !_ready || _restoring || _closed || _closePending ? null : _recovery.Queue(CreateProject());

    private void WriteRecovery()
    {
        if (_closePending) return;
        try { if (QueueRecovery() is { } work) _ = ObserveRecoveryAsync(work); }
        catch (Exception error) { SetStatus("Recovery could not be saved: " + error.Message); }
    }

    private async Task ObserveRecoveryAsync(Task<StudioWriteResult> work)
    {
        var result = await work;
        if (!_closed && !_closePending && result.Revision == _recovery?.LatestRevision && result.Error is not null)
            SetStatus("Recovery could not be saved: " + result.Error.Message);
    }

    private async void StudioClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_closePending) { e.Cancel = true; return; }
        if (_saving || _exporting || _syncing || _catalogBusy)
        { e.Cancel = true; SetStatus(_saving?"Finishing the project save...":_exporting?"Finishing the court export...":"Finish the current court operation before closing."); return; }
        try
        {
            var editable=CanChangeDocument;
            if(editable)CommitPendingDocumentInput(allowPendingImports:true);
            CourtCanvas.CancelGesture();
            if(editable && !CanChangeDocument)throw new InvalidOperationException("The court changed while preparing to close. Try again.");
            if (!_testing && !ConfirmReplace()) { e.Cancel = true; return; }
            CancelProjectRestore();
            InvalidateFloorRequests();
            if (!_ready || _recovery is null && _preferences is null) return;
            e.Cancel = true;
            PendingCloseRecovery = FlushRecoveryForCloseAsync();
            if (await PendingCloseRecovery && !_closed)
            {
                _allowClose = true;
                // Closing can finish synchronously; re-enter only after the original event unwinds.
                await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (Exception error)
        {
            e.Cancel=true;
            if (!_closed)
            { _allowClose = _closePending = false; RefreshMutationState(); RefreshToolState(); SetStatus("Close could not finish: " + error.Message); }
        }
        finally { PendingCloseRecovery = null; }
    }

    private async Task<bool> FlushRecoveryForCloseAsync()
    {
        FinishLogoOpacity();
        _closePending = true; _recoveryTimer.Stop(); InvalidateFloorRequests(); InvalidateDocumentOperations();
        RefreshMutationState(); RefreshToolState(); SetStatus("Saving recovery and settings...");
        var complete = false;
        try
        {
            var writes = new List<(string Name, Task<StudioWriteResult> Work)>();
            if (_recovery is not null) writes.Add(("Recovery", _recovery.Queue(CreateProject())));
            if (_preferences is not null) writes.Add(("Preferences", _preferences.Queue(CapturePreferences())));
            var results = await Task.WhenAll(writes.Select(item => item.Work));
            var errors = string.Join("\n", results.Select((result, index) => result.Error is null ? null : writes[index].Name + ": " + result.Error.Message).Where(message => message is not null));
            if (errors.Length == 0) { complete = true; return true; }
            SetStatus("Recovery or settings could not be saved. The app remains open: " + errors);
            if (!_testing && MessageBox.Show(this,
                    "The latest recovery or settings could not be saved. Close without them? Previous files remain available.\n\n" + errors,
                    "Court Creator", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            { complete = true; return true; }
            return false;
        }
        catch (Exception error)
        { if (!_closed) SetStatus("Recovery could not be saved. The app remains open: " + error.Message); return false; }
        finally
        {
            if (!complete && !_closed)
            { _closePending = false; RefreshMutationState(); RefreshToolState(); }
        }
    }
}
