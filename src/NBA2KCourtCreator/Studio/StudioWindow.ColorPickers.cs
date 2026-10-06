namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private readonly Func<StockLayer, bool, string?> _pickLayerColor;
    private bool _colorPickerOpen;

    private string? ShowLayerColorDialog(StockLayer layer, bool teamColors)
    {
        if (teamColors)
        {
            var picker = new TeamColorWindow(this, _teams);
            return picker.ShowDialog() == true ? picker.SelectedHex : null;
        }
        var color = new StudioColorWindow(this, layer.Color, _teams);
        return color.ShowDialog() == true ? color.SelectedHex : null;
    }

    private void PickLayerColor(StockLayer layer, bool teamColors, Func<bool>? rowCurrent = null)
    {
        if (!CanChangeDocument || _colorPickerOpen || !layer.Visible || !(rowCurrent?.Invoke() ?? true)) return;
        var version = _documentVersion;
        var originalColor = layer.Color;
        _colorLayerId = layer.Id;
        RefreshLayerSelection(); RefreshSelectedColor();
        bool Current() => CanChangeDocument && version == _documentVersion && layer.Visible
            && ReferenceEquals(SelectedColorLayer, layer) && layer.Color == originalColor && (rowCurrent?.Invoke() ?? true);
        _colorPickerOpen = true; RefreshToolState();
        try
        {
            if (!Current()) return;
            var selected = _pickLayerColor(layer, teamColors);
            if (!Current() || selected is null) return;
            if (StudioImages.Hex(selected) is not { } hex)
            { SetStatus("Enter a valid three- or six-digit hex code."); return; }
            // Modal dialogs pump the dispatcher; document/row ownership must survive both
            // the dialog and gesture/opacity cleanup immediately before mutation.
            ApplyLayerSettings(layer, null, hex, Current);
        }
        catch (Exception error) { if (!_closed) SetStatus(error.Message); }
        finally { _colorPickerOpen = false; if (!_closed) RefreshToolState(); }
    }
}
