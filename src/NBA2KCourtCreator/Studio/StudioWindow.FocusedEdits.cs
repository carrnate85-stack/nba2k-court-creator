using System.Windows;
using System.Windows.Controls;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private void CommitPendingDocumentInput(bool allowPendingImports=false)
    {
        CommitHardwoodNumberInputs();
        CommitHardwoodGesture();
        if(!CanChangeDocument || !allowPendingImports && PendingLogoImports>0)throw new InvalidOperationException("Wait for the current court operation and logo imports to finish before continuing.");
        var input=_focusedInput();if(input is null)return;
        var committed=true;
        if(ReferenceEquals(input,ProjectNameInput) && _renaming && input.Visibility==Visibility.Visible)
        {FinishRename(true);committed=!_renaming;}
        else if(input.Tag is ArtworkLayer && CurrentLogoNameInput(input))committed=ApplyLogoName(input,true);
        else if(_logoInlineCommits.TryGetValue(input,out var apply) && input.IsDescendantOf(LogoProperties))committed=apply();
        else if(input.Tag is string tag && tag.StartsWith("Color:",StringComparison.Ordinal) && _layerRows.TryGetValue(tag[6..],out var row) &&
            ReferenceEquals(input,row.Hex) && input.IsDescendantOf(LayersHost) && row.Colors.Visibility==Visibility.Visible)
            committed=row.CommitHex();
        if(!committed)throw new InvalidOperationException("Finish or correct the current field before continuing.");
        if(!CanChangeDocument)throw new InvalidOperationException("The court changed while applying the current field. Try again.");
    }
}
