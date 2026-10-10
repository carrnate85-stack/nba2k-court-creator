using System.Text.Json.Nodes;
using System.Windows.Media;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    internal long FastHistoryRestores { get; private set; }

    private static bool EqualExcept(JsonObject a, JsonObject b, params string[] excluded)
    {
        bool Included(string key) => !excluded.Contains(key, StringComparer.Ordinal);
        return a.Where(pair => Included(pair.Key)).All(pair => b.ContainsKey(pair.Key) && JsonNode.DeepEquals(pair.Value, b[pair.Key]))
            && b.All(pair => !Included(pair.Key) || a.ContainsKey(pair.Key));
    }

    // Only internally captured history may enter this path. Asset replacements, additions,
    // deletions, text edits and project loads retain the transactional full restore.
    private async Task<bool> TryRestoreHistoryEditsAsync(JsonObject target, JsonObject current)
    {
        if (!EqualExcept(target, current, "floor", "twoPointFloor", "twoPointHardwoodEnabled", "paintSettings", "lineSettings",
                "outsideColor", "outsideVisible", "visibility", "colorOverrides", "logoImages")
            || target["floor"] is not JsonObject floor || current["floor"] is not JsonObject oldFloor
            || _preparedMainFloor is null || !EqualExcept(floor, oldFloor, "textureSettings")) return false;
        var second = target["twoPointFloor"] as JsonObject;
        var oldSecond = current["twoPointFloor"] as JsonObject;
        if (second is null != (oldSecond is null) || second is not null &&
            (_preparedTwoPointFloor is null || !EqualExcept(second, oldSecond!, "textureSettings"))) return false;
        var layers = _paints.Concat(_lines).Append(_outside).ToArray();
        if (target["visibility"] is not JsonObject visibility || current["visibility"] is not JsonObject oldVisibility
            || !EqualExcept(visibility, oldVisibility, layers.Select(layer => layer.Id).ToArray())) return false;
        if (target["logoImages"] is not JsonArray logos || current["logoImages"] is not JsonArray oldLogos
            || logos.Count != oldLogos.Count || logos.Count != CourtCanvas.Layers.Count) return false;
        for (var i = 0; i < logos.Count; ++i)
            if (logos[i] is not JsonObject logo || oldLogos[i] is not JsonObject oldLogo ||
                !EqualExcept(logo, oldLogo, "x", "y", "width", "height", "rotation", "opacity", "visible", "scaleLocked", "flipX", "flipY")) return false;

        var settings = HardwoodTextureSettings.Read(floor);
        var secondSettings = second is null ? new HardwoodTextureSettings() : HardwoodTextureSettings.Read(second);
        var changeMain = settings != _mainRenderedSettings;
        var changeSecond = second is not null && secondSettings != _twoPointRenderedSettings;
        var enabled = target["twoPointHardwoodEnabled"]!.GetValue<bool>();
        var hardwoodValuesChanged = settings != _mainHardwoodSettings || secondSettings != _twoPointHardwoodSettings
            || enabled != _twoPointHardwoodEnabled;
        var paintChanges = layers.Select(layer =>
        {
            var saved = (target["paintSettings"] as JsonObject)?[layer.Id] ?? (target["lineSettings"] as JsonObject)?[layer.Id];
            var color = ReferenceEquals(layer, _outside) ? target["outsideColor"]!.GetValue<string>() : saved!["color"]!.GetValue<string>();
            var visible = ReferenceEquals(layer, _outside) ? target["outsideVisible"]!.GetValue<bool>() : saved!["visible"]!.GetValue<bool>();
            return (Layer: layer, Color: color, Visible: visible);
        }).Where(change => change.Layer.Color != change.Color || change.Layer.Visible != change.Visible).ToArray();
        var logoChanges = !JsonNode.DeepEquals(logos, oldLogos);
        var backgroundChanged = paintChanges.Length != 0 || changeMain || changeSecond || enabled != _twoPointHardwoodEnabled;
        var toolsChanged = enabled != _twoPointHardwoodEnabled || paintChanges.Any(change => change.Layer.Visible != change.Visible);
        var main = _preparedMainFloor; var secondary = _preparedTwoPointFloor;
        var rectangle = HardwoodRectangle(); var region = _courtSurface!;
        using var cancellation = new CancellationTokenSource();
        _projectRestoreCancellation = cancellation;
        ++_hardwoodPreviewRevision; _hardwoodPreviewTimer.Stop();
        _restoring = _historyRestoring = true;
        InvalidateFloorRequests(); InvalidateDocumentOperations(); _recoveryTimer.Stop();
        RefreshMutationState();
        try
        {
            // Let WPF drain its prior frame before replacing drawings again during rapid undo.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            // Prepare before mutating: failures and close cancellation keep the document/history intact.
            (Drawing? Main, Drawing? Second) drawings = (null, null);
            if (changeMain || changeSecond)
                drawings = await Task.Run(() => (
                    changeMain ? HardwoodTextureSettings.CreateDrawing(main.Image, rectangle, region, settings, cancellation.Token) : null,
                    changeSecond ? HardwoodTextureSettings.CreateDrawing(secondary!.Image, rectangle, region, secondSettings, cancellation.Token) : null), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _syncing = true;
            CancelLayerHex();
            foreach (var change in paintChanges)
            {
                change.Layer.Color = change.Color; change.Layer.Visible = change.Visible;
                RefreshLayerRow(change.Layer);
            }
            _mainHardwoodSettings = _mainRenderedSettings = settings;
            _twoPointHardwoodSettings = _twoPointRenderedSettings = secondSettings;
            _twoPointHardwoodEnabled = enabled;
            if (changeMain) _hardwoodDrawing = drawings.Main;
            if (changeSecond) _twoPointDrawing = drawings.Second;
            if (logoChanges)
                for (var i = 0; i < logos.Count; ++i)
                {
                    var saved = logos[i]!; var logo = CourtCanvas.Layers[i];
                    logo.X = Number(saved["x"]); logo.Y = Number(saved["y"]);
                    logo.Width = Number(saved["width"]); logo.Height = Number(saved["height"]);
                    logo.Rotation = Number(saved["rotation"]); logo.Opacity = Number(saved["opacity"]);
                    logo.Visible = saved["visible"]!.GetValue<bool>(); logo.ScaleLocked = saved["scaleLocked"]!.GetValue<bool>();
                    logo.FlipX = saved["flipX"]!.GetValue<bool>(); logo.FlipY = saved["flipY"]!.GetValue<bool>();
                }
            if (backgroundChanged) RebuildBackground();
            FlushLayerHex(); RefreshSelectedColor();
            ++FastHistoryRestores;
        }
        finally
        {
            _projectRestoreCancellation = null;
            _syncing = _restoring = _historyRestoring = false;
            if (!_closed)
            {
                // This path shields input without disabling controls, so there is no enabled-state tree to rebuild.
                HistoryInputShield.Visibility = System.Windows.Visibility.Collapsed;
                if (hardwoodValuesChanged || toolsChanged) RefreshHardwoodValues();
                if (_dirty && _recovery is not null && !_closePending) _recoveryTimer.Start();
            }
        }
        if (logoChanges) RefreshLogoInspector(); else if (toolsChanged) RefreshToolState();
        return true;
    }
}
