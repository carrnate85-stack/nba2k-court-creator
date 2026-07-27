# NBA 2K Court Creator

Standalone court-template tool for layered NBA 2K court PSD files.

## Run

Double-click:

```powershell
Launch NBA 2K Court Creator.bat
```

The launcher uses the bundled Codex Python runtime when it is available, then falls back to installed Python.
On launch, it quietly checks GitHub for a newer app version before opening the court editor.

## Current Features

- Loads the bundled RedLite2K/Jayderoza layered court PSD from the project `templates` folder.
- Reads the PSD layer panel directly, including groups, visibility, opacity, bounds, and layer names.
- Shows selectable Photoshop-style layer groups and layers.
- Highlights the selected layer's PSD bounds over a visible-layer court preview.
- Toggles, solos, and shows all layer states.
- Keeps only one court floor visible at a time inside the Court Floors group.
- Adds and removes custom court floor images in the project floor picker.
- Resets the court back to the bundled template default.
- Renames layer labels by right-clicking a layer name.
- Recolors the outside layer and individual paint/line layers with the selected-row color picker or hex field.
- Shows each colorable layer's current template color as the default swatch.
- Includes a searchable NBA and NCAA D1 team-color palette with hex codes.
- Provides editable court presets above the preview: left-click loads, right-click renames or saves.
- Saves and loads court state JSON files.
- Opens the source PSD in Photoshop when installed.
- Exports a flattened PNG from the PSD composite.

## Template Path

Default project PSD path:

```text
C:\Users\carrn\OneDrive\Documents\NBA 2K Court Creator\templates\NBA 2K25 Court Template By RedLite2K.psd
```

If that file is missing, the app falls back to the original Downloads path. Use **Load PSD** if you move the template.

## Updates

The updater reads:

```text
data\update_config.json
```

By default it downloads the latest `main` branch from:

```text
carrnate85-stack/nba2k-court-creator
```

Updates preserve local-only folders and files such as `templates`, `custom_floors`, `outputs`, and `data\court_presets.json`.
