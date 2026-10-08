using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SixLabors.ImageSharp;
using TextureStudio.Models;
using TextureStudio.Services;
using TwoK.Studio;
using Point = System.Windows.Point;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private readonly Func<TextLayerSettings, TextLayerSettings?> _pickTextSettings;

    private void ConfigurePaintAndTextTools()
    {
        CourtCanvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.IsKeyDown(Key.Space) || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) return;
            var point = CourtCanvas.ToDocument(e.GetPosition(CourtCanvas));
            try
            {
                if (CourtCanvas.Tool == ArtworkTool.Bucket) { e.Handled = true; FillCourtRegion(point); }
                else if (CourtCanvas.Tool == ArtworkTool.Type) { e.Handled = true; OpenTextTool(point); }
            }
            catch (Exception error) { if (!_closed) SetStatus(error.Message); }
        };
    }

    internal string? FillCourtRegion(Point point)
    {
        if (!CanChangeDocument || _section == "import" || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !new Rect(0, 0, 8192, 4096).Contains(point)) return null;
        CommitPendingDocumentInput();
        // Colors fill their own geometric regions, including regions whose paint is currently off.
        var layer = _paints.FirstOrDefault(layer => layer.Geometry.FillContains(point));
        if (layer is null && _courtSurface?.FillContains(point) != true) layer = _outside;
        if (layer is null) return null;
        SetLayerSettings(layer.Id, visible: true, color: _primaryColorHex);
        SetStatus(layer.Name + " filled with primary color " + _primaryColorHex + ".");
        return layer.Id;
    }

    internal TextLayerSettings? ReadTextSettings(ArtworkLayer? layer)
    {
        if (layer is null || !_logoArtwork.TryGetValue(layer.Id, out var artwork) || artwork["textSettings"] is null) return null;
        return artwork["textSettings"]!.Deserialize<TextLayerSettings>() ?? throw new InvalidDataException("Invalid text layer settings.");
    }

    private TextLayerSettings? ShowTextDialog(TextLayerSettings settings)
    {
        var dialog = new TextureStudio.TextLayerDialog(settings) { Owner = this, PaintColorPalette = PickerColors };
        dialog.Title = "Text"; ((Button)dialog.FindName("AcceptButton")).Content = "Apply";
        StudioWindowBounds.Attach(dialog);
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    private void EditTextClick(object sender, RoutedEventArgs e)
    {
        if (ReadTextSettings(CourtCanvas.SelectedLayer) is not null) OpenTextTool(CourtCanvas.SelectedLayer!.Center, CourtCanvas.SelectedLayer);
    }

    private void OpenTextTool(Point point, ArtworkLayer? target = null)
    {
        if (!CanChangeDocument || _section == "import" || _colorPickerOpen || PendingLogoImports > 0
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !new Rect(0, 0, 8192, 4096).Contains(point)) return;
        target ??= CourtCanvas.HitArtwork(point);
        if (ReadTextSettings(target) is not { } settings)
        {
            target = null;
            if (CourtCanvas.Layers.Count >= 4) { SetStatus("Text uses a court artwork slot. Delete a layer before adding text; four slots are supported."); return; }
            settings = new TextLayerSettings("TEXT", "Arial", 180, _primaryColorHex, "#000000", 0, true);
        }
        CommitPendingDocumentInput(); CourtCanvas.CancelGesture();
        var version = _documentVersion;
        _artworkEditorOpen = true; RefreshMutationState(); RefreshToolState();
        TextLayerSettings? selected;
        try { selected = _pickTextSettings(settings); }
        catch (Exception error) { if (!_closed) SetStatus("Text could not be edited: " + error.Message); return; }
        finally { _artworkEditorOpen = false; if (!_closed) { RefreshMutationState(); RefreshToolState(); } }
        if (selected is null || !CanChangeDocument || version != _documentVersion || target is not null && !CourtCanvas.Layers.Contains(target)) return;
        try { ApplyTextSettings(selected, point, target); }
        catch (Exception error) { if (!_closed) SetStatus("Text could not be placed: " + error.Message); }
    }

    internal ArtworkLayer? ApplyTextSettings(TextLayerSettings settings, Point point, ArtworkLayer? target = null)
    {
        if (!CanChangeDocument || PendingLogoImports > 0 || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || target is not null && !CourtCanvas.Layers.Contains(target)) return null;
        if (target is null && CourtCanvas.Layers.Count >= 4) throw new InvalidOperationException("Court export supports four artwork slots.");
        if (target is not null && ReadTextSettings(target) == settings) return target;
        // Keep text metadata alongside its rendered PNG; saved courts render without installing the font.
        using var image = TextLayerFactory.Render(settings);
        var directory = Path.Combine(_engine.ProjectRoot, "logos"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "text-" + Guid.NewGuid().ToString("N") + ".png");
        var committed = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) image.SaveAsPng(stream);
            var bitmap = StudioImages.Load(path);
            var layer = target ?? new ArtworkLayer();
            var center = target?.Center ?? point;
            var factorX = target?.Image is { } previous ? target.Width / previous.Width : 1.0;
            var factorY = target?.Image is { } previousHeight ? target.Height / previousHeight.Height : 1.0;
            // Bitmap Width is in device-independent pixels; PNGs are rendered at 96 DPI.
            var width = Math.Max(1, bitmap.PixelWidth * factorX); var height = Math.Max(1, bitmap.PixelHeight * factorY);
            if (width > 32768 || height > 32768) throw new InvalidOperationException("Text dimensions exceed the supported layer size.");
            if (!Change(() =>
            {
                layer.Path = path; layer.Image = bitmap;
                layer.Width = width; layer.Height = height;
                layer.X = center.X - width / 2; layer.Y = center.Y - height / 2;
                if (target is null)
                {
                    var name = settings.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    layer.Name = name[..Math.Min(80, name.Length)]; CourtCanvas.Layers.Add(layer);
                }
                _logoArtwork[layer.Id] = new JsonObject { ["textSettings"] = JsonSerializer.SerializeToNode(settings) };
            })) return null;
            committed = true; CourtCanvas.SelectedLayer = layer; RefreshLogoInspector(); RefreshToolState();
            SetStatus(target is null ? "Text placed. Use Move or Transform to position it; use Text to edit it." : "Text updated.");
            return layer;
        }
        finally { if (!committed && File.Exists(path)) File.Delete(path); }
    }
}
