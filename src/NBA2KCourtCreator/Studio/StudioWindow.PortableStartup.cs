using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

namespace NBA2KCourtCreator.Studio;

public partial class StudioWindow
{
    IPortableFloorPreparation? _floorPreparation;
    bool _preparingFloors;

    private async Task InitializePortableAsync()
    {
        if (Environment.GetEnvironmentVariable("COURT_CREATOR_PREPARE_FLOORS") != "1"
            || await Task.Run(() => PortableFloorStartup.HasCompleteCatalog(_engine.ProjectRoot)))
        { await InitializeAsync(); return; }
        await PrepareFloorsInBackgroundAsync();
    }

    private async void FloorPreparationRetryClick(object sender, RoutedEventArgs e)
        => await Guard(() => PrepareFloorsInBackgroundAsync());

    internal async Task PrepareFloorsInBackgroundAsync(IPortableFloorPreparation? preparation = null)
    {
        if (_preparingFloors || _closed) return;
        _preparingFloors = true;
        FloorPreparationStatus.Visibility = Visibility.Visible;
        FloorPreparationRetry.Visibility = Visibility.Collapsed;
        FloorPreparationSpinner.Visibility = Visibility.Visible;
        FloorPreparationText.Text = "Preparing court floors…";
        _floorPreparation = preparation ?? new PortableFloorStartup();
        try
        {
            string? game = null;
            while (!_closed)
            {
                var result = await _floorPreparation.RunAsync(_engine.ProjectRoot, game, async line =>
                {
                    if (_closed) return;
                    if (line == "FIRST_FLOOR_READY")
                    {
                        if (!_ready) await InitializeAsync();
                        FloorPreparationText.Text = "Preparing remaining court floors…";
                    }
                    else FloorPreparationText.Text = line;
                });
                if (_closed) return;
                if (result.Code == 0) break;
                if (result.Code != 2) throw new IOException(result.Error);
                var chooser = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your installed NBA 2K27 folder (manifest and mod.exe)" };
                if (chooser.ShowDialog(this) != true)
                    throw new OperationCanceledException("Choose your NBA 2K27 folder to prepare the court floors.");
                game = chooser.FolderName;
            }
            if (_closed) return;
            if (!_ready) await InitializeAsync();
            var response = await _engine.RequestAsync(["load-stock"], TimeSpan.FromMinutes(4));
            if (_closed) return;
            var floors = StockFloor.ReadMany(response["customFloorImages"]!.AsArray().OfType<JsonObject>(), _engine.ProjectRoot);
            // Only refresh the catalog; retain the user's current court, artwork and undo history.
            _floors.Clear(); _floors.AddRange(floors);
            FloorPreparationText.Text = $"{_floors.Count} court floors ready.";
            FloorPreparationStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                FloorPreparationText.Text = "Floor preparation: " + error.Message;
                FloorPreparationRetry.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _floorPreparation.Dispose(); _floorPreparation = null; _preparingFloors = false;
            if (!_closed) FloorPreparationSpinner.Visibility = Visibility.Collapsed;
        }
    }
}
