# NBA 2K Court Creator

For an optional single portable Windows EXE with bundled Python/.NET and a
first-run floor cache generated from the installed game, see [Portable EXE](PORTABLE-EXE.md).

## Native Studio Migration

The desktop launcher now opens the C#/WPF studio, backed by the existing persistent
Python game-file engine. The court is a native editable canvas: import logos, move,
resize with scale lock, rotate, reorder, rename, duplicate across court axes,
and undo/redo directly in the main preview. Compact Flip and Mirror dropdowns
offer X (left/right) and Y (top/bottom): Flip changes the artwork in place, while
Mirror creates an opposite-side copy retaining rotation and flip state. No separate
browser editor is required. Paint & Lines retains its row toggles, editable hex
fields, color swatches, and searchable NBA/college Team Colors.
Swatch and pinned color actions open Canvas's shared `ColorPickerDialog`, with
spectrum/hue controls, RGB/HSB inputs, hex entry and standard swatches. Court
Creator adds Team Colors to that same dialog; Cancel leaves the court untouched.

`src/TwoK.Studio` provides the court placement canvas and transform model. Its
theme and toolbar-icon adapters now use the actual central Canvas WPF controls
and palette. Shared preview conversion preserves transparency and game-data RGB.
Logo imports use shared raster/DDS decoding and Windows color-profile conversion;
profiled, oriented and alternate-format logos are normalized to owned PNGs so the
preview and exported court use the same pixels. Shared memory preflight rejects
large image operations before allocation without discarding the preceding draft.
Export uses full-resolution source or normalized working assets at 8192 x 4096,
rather than resampling the preview.
Floor selection uses a searchable popup catalog with categories, favorites, and recents.
Tool adjustments occupy a 40-DIP overlay inside the court frame at its upper-left
edge, beside the left tool rail and spanning the frame's width. The tool rail
and inspector extend directly to the document bar. The court frame meets the
document bar and left tool rail directly, with no top or left gutter. Options
change with the active tool without moving or resizing the court. Narrow windows put hardwood
labels above their sliders while keeping the edit selector and reset visible.

Native Save/Save As now creates a portable `.court.json` and a neighboring
`<project-name>.assets` folder for its hardwood and logos. Move both together when
sharing a project; duplicate images reuse content-hashed files. Saves run off the
UI thread and preserve the prior project on an asset failure. A startup failure
offers Retry in the status bar without requiring an application restart.

Run `Build Court Creator.bat` after code changes. In a source checkout, the desktop
launcher also adopts newer matching published central Canvas packages automatically
before opening the app: it tests artwork, shared-color and shared-control integration in a
candidate build and retains the last working build on failure. Unchanged launches
do not rebuild. The launcher uses the
published `desktop` build. `Launch Electron Fallback.bat` preserves access to the
previous interface; the Electron files have not been removed.

**Send Texture to 2K Canvas** exports a full-resolution PNG and opens it in the
sibling Canvas app. The accompanying court JSON stays editable in Court Creator.
This is a one-way flattened texture handoff, not live two-way layer synchronization.
**Edit Artwork...** opens a compact native editor for the selected hardwood or
logo/graphic using matching `TwoK.Canvas.Core` and `TwoK.Canvas.Wpf` packages,
with minimum v0.6.0. Supported programmatic edits use `CanvasEditor.Commands` for
atomic history and automatic preview updates. Shared selections,
brush/eraser, eyedropper, text, layers/masks, transforms and navigation operate on
a private draft. Apply updates the court with one undo step; Cancel, Escape and
close keep the original unchanged. Save includes editable `.2kstudio` archives
and any derived DDS alongside the portable artwork, so later editing retains
layers, text and masks. See [Shared Artwork](SHARED-ARTWORK.md) for build setup,
ownership, verification and limitations.

See [Native Studio](NATIVE-STUDIO.md) for architecture and verification, and
[Move to Another PC](MOVE-TO-ANOTHER-PC.md) for current setup instructions.

## Previous Electron Releases

The following release history describes the retained Electron implementation.

The main workspace now uses stock **Game UV** geometry throughout Court Floors,
Paint & Lines, Logos, project recovery, PNG exports, and IFF exports. It does not
load a PSD on startup. The legacy PSD/template system remains in the repository
but its controls are hidden. File actions live in the titlebar; repeated branding,
section banners, and the separate Colors tab have been removed. Paint & Lines
retains its per-row toggle, hex/swatch, and Team Colors controls.

Projects use version 2 with `buildMode: game-uv`. Previous projects can still be
opened; matching hardwood, logo assets, and familiar paint/line settings are
restored into the stock workspace. Existing experimental settings are adopted
once during the upgrade. New resets to stock NBA markings, green paint/outside,
the default hardwood, and no logos. Live previews retain decoded images and draw
locally; Python exports from original assets at full resolution.

Version 1.5.2 stores the extracted court library inside the local project
folder and updates saved paths after relocation. Version 1.5.1 keeps the court-category filters inside the selector at compact
window sizes. Version 1.5 introduces a brighter companion-app workspace, a
thumbnail court browser with categories, favorites, recent courts, sorting and grid/list views,
larger responsive previews, and a streamlined Paint & Lines inspector with a
searchable team-color library. Version 1.4 adds cached floor and logo composition,
atomic preview/export writes, engine timeout recovery, safer project restoration,
resilient data loading, and a more compact responsive workspace with clearer render
and selection states.
Version 1.3 expands the NBA 2K27 manifest scan to include verified alternate naming
patterns for WNBA, historic, international, event, mode, and expansion floors.
Version 1.2.2 keeps the active game-floor catalog separate from saved recovery data
and activates court floors with one click. Version 1.2 uses the extracted NBA 2K27
court library automatically, while retaining
the NBA 2K26 library as a fallback. Version 1.1 added editable project Save/Open,
automatic recovery, preserved logo proportions with staggered imports, panel-width
responsive layouts, serialized previews, native-resolution PNG exports, and staged
release updates with rollback.
Run `Setup Court Creator.bat` once on a new PC, then use the launcher. Development
checkouts update through Git; standalone installs check GitHub releases after opening.

The notes below describe the earlier desktop implementation and may list features
that have not yet been ported to Electron, such as editable preset slots.

Standalone court-template tool for layered NBA 2K court PSD files.

## Run

Double-click:

```powershell
Launch NBA 2K Court Creator.bat
```

The native launcher uses the app-owned Python runtime. Standalone copies apply a
compatible staged native release before launch and check for updates in the
background. Git checkouts are never automatically overwritten.

## Legacy Template Features

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

Default project PSD path (inside the new local project folder):

```text
C:\Users\carrn\Projects\NBA 2k Court Creator\templates\NBA 2K25 Court Template By RedLite2K.psd
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

The Import tab reads older ZIP-style court IFFs containing a DDS floor texture and aligns the playable-court edges to an 8192 x 4096 court canvas. Both imported courts and normal projects export as one baked full-court texture inside a stock NBA 2K27 MyTEAM floor archive. The app prepares that reusable base locally from the installed game, preserves any existing loose mod while doing so, and never stores the game archive in Git.

The export base removes the stock marking meshes once so court markings come from the baked texture. Preparation records interrupted extractions and restores an existing loose floor mod on retry. Import alignment preserves black aprons and real transparency. Exports run in a separate Python worker from live previews. Game discovery reads Steam's additional libraries and remembers a selected installation in the local-only `data/game_installation.json` file.

## Studio Appearance

The native app uses the 2K Canvas / Texture Studio semantic palette in
`src/TwoK.Studio/StudioTheme.cs`. The retained Electron version uses its matching
`electron/studio-theme.js` palette. The workspace defaults to light neutral gray
with blue selection and charcoal primary actions. The title-bar theme button
remembers light/dark mode. UI colors never alter court artwork or exports.

The hardwood browser opens as a modal catalog from the selected-court arrow.
Search, category filters, favorites, recents, and sorting are available. The native
catalog currently uses a compact thumbnail list; grid/list modes remain in the
Electron fallback. Its rows are virtualized, with thumbnails loaded in the
background only for nearby courts and at most two concurrent decoders. Search is
debounced and includes court IDs. A damaged preview does not block the catalog.
Stock variants use short numeric suffixes only when names repeat within a category;
single courts do not keep a Wood1 label. Stable court IDs and texture files stay unchanged.
Selecting a floor closes the catalog and updates the court
without switching tabs. The preview preserves its 2:1 texture aspect ratio.

## Stock UV Lines

The former Experimental build is now the main workspace. It combines local stock
or custom hardwood with 14 independent stock markings and two regulation-derived
three-point marking layers. Four independent paint controls fill the left/right
inner keys and secondary bands; two optional fills cover the two-point areas.
Paint defaults to #19583F. All settings and logo layers belong to the same project
and are included in its PNG and IFF exports.

Two optional Two-Point Area fills cover the stock NBA three-point enclosure outside the full outer key. College Three and High School Three are generated independently using official NCAA/NFHS outside-edge dimensions and two-inch lines. College uses a 22-foot 1.75-inch arc and 21-foot 7.875-inch corners; high school uses a 19-foot 9-inch semicircle. These four new layers default off. The stock court remains 94 by 50 feet: only the three-point markings change, not the court size, lane, or game rules. Basket centers are inferred from stock boundary inside edges and the regulation 63-inch setback, then checked against stock charge arcs. No PSD positioning is used. Shapely 2.1+ is required when rebuilding the geometry; run Setup Court Creator after updating an older Python environment.

Game UV mode projects each marking through the stock floor triangles' TEXCOORD0
coordinates and primary material transform, without using PSD bounds. Template
Aligned remains internal for comparison, not a visible workspace option. Game UV
IFF exports disable the material's secondary full-court sample to prevent
duplicate artwork, retaining the primary mapping. Projection coverage is checked
offline; alignment in a running game still needs verification.

Stock geometry preparation uses a temporary manifest pointing at the installed
game's archives and writes only into a temporary folder. The decoded polygons are
cached locally in `data/generated/experimental-stock-lines.json`, not in Git.
Visible stripe widths are reconstructed from the original line texture's alpha
mask. Missing or outdated geometry is prepared automatically on workspace load.

The standard PSD center-circle pixels are strengthened before preview resizing and export, without modifying the source PSD. Only that layer's image cache is invalidated.

## Logo Alignment

The native Logos tab edits artwork directly on the court. Optional Guides and
Snap controls use 17 stock-derived court
anchors in Game UV coordinates, matching the main preview and export. Center at
Anchor places a logo at a court center, free-throw center, paint center, key
corner, or court corner. Drag snapping uses an eight-screen-pixel tolerance; Alt
temporarily bypasses it. Mirrored copies use the derived court center. Guides
never become artwork or appear in exports. The former separate browser editor
remains available only in the Electron fallback.
