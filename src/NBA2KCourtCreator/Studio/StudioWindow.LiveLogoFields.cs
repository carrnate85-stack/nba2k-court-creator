using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private Action? _refreshLiveLogoFields;
    private bool _liveLogoFieldsPending;
    private void QueueLiveLogoFields()
    {
        if(_liveLogoFieldsPending || _refreshLiveLogoFields is null)return;
        _liveLogoFieldsPending=true;
        CompositionTarget.Rendering+=LiveLogoFieldsFrame;
    }
    private void LiveLogoFieldsFrame(object? sender,EventArgs e)=>FlushLiveLogoFields();
    private void FlushLiveLogoFields()
    {
        CancelLiveLogoFields();
        _refreshLiveLogoFields?.Invoke();
    }
    private void CancelLiveLogoFields()
    {
        if(!_liveLogoFieldsPending)return;
        CompositionTarget.Rendering-=LiveLogoFieldsFrame;
        _liveLogoFieldsPending=false;
    }
}
