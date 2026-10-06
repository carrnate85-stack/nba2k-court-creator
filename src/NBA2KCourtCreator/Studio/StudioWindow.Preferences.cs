using System.Text.Json.Nodes;
using TwoK.Studio;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    private readonly StudioPreferenceStore? _preferences;
    private JsonObject CapturePreferences() => new StudioPreferences(_favorites.ToArray(), _recent.ToArray(), StudioTheme.IsDark).ToDocument();

    private async Task<string?> LoadPreferencesAsync()
    {
        if (_preferences is null) return null;
        try
        {
            var settings = await _preferences.ReadAsync();
            if (_closed) throw new ObjectDisposedException(nameof(StudioWindow));
            if (settings is null) return null;
            _favorites.Clear(); _favorites.UnionWith(settings.Favorites);
            _recent.Clear(); _recent.AddRange(settings.Recent);
            StudioTheme.Apply(settings.Dark);
            return null;
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        { return "Preferences ignored: " + error.Message; }
    }

    private void SavePreferences()
    {
        if (_preferences is null || _closed || _closePending) return;
        try { _ = ObservePreferencesAsync(_preferences.Queue(CapturePreferences())); }
        catch (Exception error) { SetStatus("Preferences could not be saved: " + error.Message); }
    }

    private async Task ObservePreferencesAsync(Task<StudioWriteResult> work)
    {
        var result = await work;
        if (!_closed && !_closePending && result.Revision == _preferences?.LatestRevision && result.Error is not null)
            SetStatus("Preferences could not be saved: " + result.Error.Message);
    }
}
