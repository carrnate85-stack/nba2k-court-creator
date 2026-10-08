using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NBA2KCourtCreator.Studio;
using TextureStudio.Models;
using TwoK.Studio;

internal static partial class Program
{
    private static async Task CheckPaintAndText(StudioWindow window, string output, Point twoPoint, Point key, Point center)
    {
        // Register fixture hardwoods just as the real catalog does before taking an undo baseline.
        await window.RestoreProjectAsync(window.CreateProject());
        var before = window.CreateProject();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        int History(string name) => ((System.Collections.ICollection)typeof(StudioWindow).GetField(name, flags)!.GetValue(window)!).Count;
        var undo = History("_undo"); var redo = History("_redo");
        window.StoreSampledColor(Color.FromRgb(171, 205, 239));
        Assert(JsonNode.DeepEquals(before, window.CreateProject()) && undo == History("_undo") && redo == History("_redo"), "Eyedropper changed the court or history.");
        Assert(window.PickerColors.PrimaryColor?.R == 171 && ((SolidColorBrush)((Border)window.FindName("PinnedColorSwatch")).Background).Color == Color.FromRgb(171, 205, 239), "Primary sample is missing from swatch or shared picker.");
        var preferences = StudioPreferences.FromDocument(new JsonObject { ["primaryColor"] = "#ABCDEF" });
        Assert(StudioPreferences.FromDocument(preferences.ToDocument()).PrimaryColor == "#ABCDEF", "Primary color preference did not round-trip.");
        try { StudioPreferences.FromDocument(new JsonObject { ["primaryColor"] = true }); throw new Exception("Invalid primary preference accepted."); }
        catch (InvalidDataException) { }
        window.SwitchSection("export"); window.SelectCanvasTool(ArtworkTool.Bucket);
        Assert(window.FillCourtRegion(key) == "paint-left" && window.Section == "export", "Bucket lost its clicked region or changed the inspector tab.");
        Assert(window.PaintLayers.First(layer => layer.Id == "paint-left").Color == "#ABCDEF" && History("_undo") == undo + 1, "Bucket did not make one primary-color edit.");
        await window.UndoAsync();
        Assert(JsonNode.DeepEquals(before, window.CreateProject()), "Paint undo did not restore the original court.");
        await window.UndoAsync(true); Assert(window.PaintLayers.First(layer => layer.Id == "paint-left").Color == "#ABCDEF", "Paint redo failed.");
        var filled = History("_undo"); window.FillCourtRegion(key); Assert(History("_undo") == filled, "Painting the same color adds a no-op undo.");
        Assert(window.FillCourtRegion(twoPoint) == "two-point-left" && window.FillCourtRegion(center) == "main-court-area", "Bucket cannot fill two-point or three-point hardwood regions.");
        Assert(window.FillCourtRegion(new Point(10, 10)) == "stock-outside" && window.FillCourtRegion(new Point(-10, 0)) is null, "Bucket outside-court hit testing failed.");
        await window.ExportToAsync(Path.Combine(output, "paint-bucket.png"), false);
        CheckExportPixel(Path.Combine(output, "paint-bucket.png"), key, Color.FromRgb(171, 205, 239));
        CheckExportPixel(Path.Combine(output, "paint-bucket.png"), center + new Vector(0, 150), Color.FromRgb(171, 205, 239));
        await window.RestoreProjectAsync(before);
        window.SelectCanvasTool(ArtworkTool.Type);
        var noText = window.CreateProject(); undo = History("_undo");
        typeof(StudioWindow).GetMethod("OpenTextTool", flags)!.Invoke(window, [center, null]);
        Assert(JsonNode.DeepEquals(noText, window.CreateProject()) && undo == History("_undo"), "Canceled text dialog changed the court.");
        var settings = new TextLayerSettings("COURT\nCREATOR", "Arial", 160, "#ABCDEF", "#112233", 3, true, true, TextLayerAlignment.Center, 190, 2);
        var text = window.ApplyTextSettings(settings, center)!;
        Assert(window.Canvas.Layers.Contains(text) && window.ReadTextSettings(text) == settings && undo + 1 == History("_undo"), "Text tool did not preserve editable settings as one undo.");
        Assert(File.Exists(text.Path) && text.Image is not null && text.Width > 100 && text.Height > 100, "Text was not rendered as a portable graphic.");
        var original = window.CreateProject(); var originalCenter = text.Center;
        var edited = settings with { Text = "UPDATED", FontSize = 180, Bold = false, Alignment = TextLayerAlignment.Right };
        window.ApplyTextSettings(edited, new Point(), text);
        Assert(window.ReadTextSettings(text) == edited && text.Center == originalCenter, "Editing text lost its position/settings.");
        await window.UndoAsync(); Assert(JsonNode.DeepEquals(original, window.CreateProject()), "Text edit undo lost text metadata or its PNG.");
        await window.UndoAsync(true); text = window.Canvas.Layers.Single(); Assert(window.ReadTextSettings(text) == edited, "Text edit redo failed.");
        var saved = Path.Combine(output, "editable-text.court.json"); await window.SaveProjectToAsync(saved); await window.OpenProjectFromAsync(saved);
        text = window.Canvas.Layers.Single(); Assert(window.ReadTextSettings(text) == edited && File.Exists(text.Path), "Portable save/reopen lost editable text or raster assets.");
        await window.ExportToAsync(Path.Combine(output, "editable-text.png"), false);
        var duplicate = (Button)window.FindName("DuplicateLogoButton"); duplicate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(window.Canvas.Layers.Count == 2 && window.ReadTextSettings(window.Canvas.SelectedLayer) == edited, "Duplicating text lost its editable settings.");
        await window.UndoAsync(); text = window.Canvas.Layers.Single();
        var noOpUndo = History("_undo");
        window.ApplyTextSettings(edited, center, text); Assert(window.Canvas.Layers.Count == 1, "No-op text edit duplicated a layer.");
        Assert(History("_undo") == noOpUndo, "No-op text edit changed history.");
        for (var index = 0; index < 3; index++) await window.AddLogoAsync(text.Path);
        var full = window.CreateProject();
        try { window.ApplyTextSettings(settings, center); throw new Exception("Text exceeded four court slots."); }
        catch (InvalidOperationException) { }
        Assert(JsonNode.DeepEquals(full, window.CreateProject()), "Text capacity failure mutated the court.");
        var dialog = new TextureStudio.TextLayerDialog(edited) { PaintColorPalette = window.PickerColors };
        try { Assert(((TextBox)dialog.FindName("TextContentInput")).Text == edited.Text && dialog.PaintColorPalette == window.PickerColors, "Shared Canvas text dialog did not receive text or palette."); }
        finally { dialog.Close(); }
        await window.RestoreProjectAsync(before); window.SwitchSection("paint"); window.ShowHardwoodTools();
        Console.WriteLine("PASS paint/text: sample-only primary swatch, fill keys/two-point/main/outside, undo/redo/no-ops, exported color, shared text controls, portable editable text and canceled drafts.");
    }
}
