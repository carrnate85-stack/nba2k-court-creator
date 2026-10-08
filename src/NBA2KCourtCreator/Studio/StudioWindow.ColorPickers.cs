namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private string _primaryColorHex = "#19583F";
    internal TextureStudio.Services.PaintColorPalette PickerColors { get; } = new();
    private readonly Func<string, string?> _pickPrimaryColor;
    private readonly Func<StockLayer, bool, string?> _pickLayerColor;
    private bool _colorPickerOpen;

    private string? ShowPrimaryColorDialog(string hex)
    {
        var picker = StudioColorWindow.Create(this, hex, _teams, "Primary color");
        return picker.ShowDialog() == true ? TextureStudio.Services.RasterPaintService.ToHex(picker.SelectedColor) : null;
    }
    internal void StoreSampledColor(System.Windows.Media.Color color)
    {
        if (!CanChangeDocument) return;
        SetPrimaryColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
        SetStatus("Primary color sampled: " + _primaryColorHex + ". Choose Primary in a color picker to use it.");
    }
    private void SetPrimaryColor(string color)
    {
        _primaryColorHex = StudioImages.Hex(color) ?? throw new ArgumentException("Enter a valid primary color.");
        RefreshSelectedColor(); SavePreferences();
    }
    private void PickPrimaryColor()
    {
        if (!CanChangeDocument || _colorPickerOpen) return;
        var version = _documentVersion; _colorPickerOpen = true; RefreshToolState();
        try
        {
            var selected = _pickPrimaryColor(_primaryColorHex);
            if (CanChangeDocument && version == _documentVersion && StudioImages.Hex(selected) is { } hex) SetPrimaryColor(hex);
        }
        finally { _colorPickerOpen = false; if (!_closed) RefreshToolState(); }
    }

    private string? ShowLayerColorDialog(StockLayer layer, bool teamColors)
    {
        if (teamColors)
        {
            var picker = new TeamColorWindow(this, _teams);
            return picker.ShowDialog() == true ? picker.SelectedHex : null;
        }
        var color = StudioColorWindow.Create(this, layer.Color, _teams, layer.Name);
        return color.ShowDialog() == true ? TextureStudio.Services.RasterPaintService.ToHex(color.SelectedColor) : null;
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
