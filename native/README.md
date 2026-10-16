# Court Creator — Rust / Tauri 2 preview

A rebuild of the NBA 2K Court Creator studio in **Rust, Tauri 2, React and TypeScript**. It runs as one
Windows x64 EXE with its UI embedded and uses the installed Microsoft Edge WebView2 runtime. No .NET,
Python, Node, Electron or WebView2 runtime is bundled or started. The existing WPF/Python app, its
launcher (`C:/Users/carrn/Desktop/NBA 2K Court Creator.lnk`), preferences and caches are untouched; this
preview keeps its own identifier (`com.2kstudio.courtcreator.nativepreview`) and data folder
(`%LOCALAPPDATA%\2K Studio\Court Creator Native`).

## Layout

| Path | Role |
| --- | --- |
| `crates/court-core` | Court engine: geometry, composition, projects, history, catalog, IFF/DDS export, game extraction |
| `src-tauri` | Desktop process: typed commands, background startup/preparation, recovery/preferences writers, hidden checks |
| `ui` | React studio (layout and controls of `StudioWindow.xaml` on the shared Canvas palette) |
| `tools/Build.ps1` | Type-check + build UI, Rust tests, release EXE, three hidden off-screen checks |
| `tools/Install-Preview.ps1` | Installs the verified EXE to `%LOCALAPPDATA%\Programs\NBA 2K Court Creator Rust Preview` with a separate desktop shortcut |

`court-core` ports: `experimental_lines.py` (geometry build, composition) → `geometry_build.rs`,
`geometry.rs`, `render.rs`; `hardwood_adjustments.py` → `hardwood.rs`; `court_import.py` /
`floor_geometry.py` → `iff.rs`, `scene.rs`, `floor_geometry.rs`, `game.rs`, `oodle.rs`, `import.rs`;
`tools/prepare_portable.py` + library builders → `prepare.rs`; WPF project/store/assets/validation →
`project.rs`, `document.rs`, `catalog.rs`; `LogoCleanupImage` → `cleanup.rs`. Pillow operations the
exporter relies on (integer polygon fill, Lanczos/bicubic resampling, bicubic affine rotation,
`alpha_composite`) are reproduced in `raster.rs`; Python's `json.dumps` text in `pyjson.rs`.

## Shared 2K Canvas code

Explicit local path dependencies on the sibling checkout `../../source/native`:
`crates/canvas-engine` (DDS BC1/BC3/BC7 encode/decode with mips) and `crates/canvas-text`
(DirectWrite/Direct2D text). The UI bundles `ui/theme.generated.css` and `ui/icons.generated.json` at
build time through the `@canvas` Vite alias. Nothing from the Canvas checkout is needed at runtime.

Built and verified against Canvas `0755659259f84fc0e768f575dd048e288ba325af` (0.44.0, clean checkout). Besides the two
crates, the build bundles Canvas UI sources through the `@canvas` alias: theme tokens, icons,
`CanvasViewport`, `CompositeSurface`, `TextEditor`, `LayerThumbnail`, `HexColorInput`, their workers
and `editor.css` (scoped to the Edit Artwork window at build time). SHA-256 of the sorted per-file
hashes of both crates' `src` + `Cargo.toml`: `c193a62e…fecca1e1`. No Canvas file was modified for this port. Court-specific
geometry, colors, hardwood settings and IFF rules stay in `court-core`.

## Build, check and install

```powershell
./native/tools/Build.ps1            # from court-portable-source
./native/tools/Install-Preview.ps1  # optional: installs the verified EXE + separate shortcut
```

Development needs Rust 1.99.0 (MSVC, see `rust-toolchain.toml`), Visual C++ Build Tools, Node and pnpm.
The EXE is `native/target/release/court-creator-desktop.exe`. Hidden checks use private data roots
(`COURT_CREATOR_NATIVE_DATA`) and never open a visible window or touch personal settings/projects.

## Data and first run

Startup shows the window immediately (spinner while loading). Geometry and the floor catalog load on
a background thread:

1. Geometry: own cache, else a *validated* existing cache copied read-only from an existing install,
   else built from the installed game (`mod.exe` extraction into private scratch + installed Oodle).
2. Floors: own complete library, else an existing install's complete library referenced read-only,
   else prepared from the game archives (first floor published early, 4 workers, resumable,
   cancellable; the catalog refresh keeps the open court and undo history).
3. Export base: own cache (`CourtCreator-FullCourt-v3`), else a fully validated existing base copied,
   else extracted and cleaned from the game (line meshes removed, Oodle buffers decoded to raw `.bin`,
   `.gz` references rewritten, `CompressionMethod` removed, regular arena-000 UV1 fit to the floor
   surface only). Game files, manifest and mods are never written.

If the game is not found, the UI offers *Choose NBA 2K27 folder…*.

## Verification record (2026-10-16)

All on this PC with NBA 2K27 installed (updated 2026-10-16). Off-screen only; no visible app launch.

- **Composition parity:** five 1.7.17 fixture projects (`outputs/v1717/hardwood-tools/*.court.json`:
  two hardwoods with color/scale/rotation, disabled two-point, editable text, exact numbers,
  high-school two-point paint) rendered at 8192×4096 are **byte-identical** to the Python exporter
  (0 differing pixels). Full render ≈0.2 s (Python ≈19 s).
- **Geometry:** revision of the existing cache equals Python's `geometry_revision`. Geometry rebuilt
  from the game (2.3 s): all non-triangulated data bit-identical; triangulated regions (earcut instead
  of constrained Delaunay) differ only at edge pixels (≤530 px per layer); 4/5 fixtures still render
  identically, the high-school two-point fixture differs in 836 edge pixels (0.0025%).
- **Regular base** prepared from the game (5.7 s): scene bytes, member list and every member's
  contents identical to the verified `outputs/v1717/regular-base.iff`; `VertexBuffer.79a40ef282fca617.bin`
  3371 vertices, 28-byte stride, 94,388 bytes, UV1 identical, bounds X ±975.360352, Z −1889.76404…1889.77222.
- **IFF exports** (CLI and in-app): CRC-valid, same members as the base, only the court DDS and
  `level_floor.SCNE` change, scene equals Python `native_export_scene`, DDS exact single-surface
  BC7_UNORM 8192×4096 with 14 mips (44,739,428 bytes), layout comment identical to Python's, geometry
  members identical to the 1.7.17 real export; decoded BC7 vs rendered PNG max channel error 4–21,
  mean 0.1–0.3. Export ≈3–5 s.
- **Import older court:** 9 source IFFs — inspections identical to Python; preview and full PNGs
  0 differing pixels.
- **Floor preparation:** 266 floors from the current game; the 259 shared with the 1.7.17 library are
  pixel-identical PNGs; index byte-identical to Python's format. First floor 2.0 s, all 65 s; resume 5 s.
- **App (hidden WebView2):** layout geometry, hardwood bar default, overlay toggles keep the court
  transform, hardwood undo, paint bucket, two-point enable/disable, portable project open with
  relative assets, text placement, PNG/IFF export; in-app PNG export of the editable-text fixture is
  byte-identical to Python. True first run (no existing caches): usable after the first floor,
  preparation resumed after exit and completed (266 floors) with the catalog refreshed in place.
- **Edit Artwork** (hidden checks, light/dark/compact): logo Apply writes `artwork.png` +
  `artwork.2kstudio` under `artwork/<id>`, keeps placement, is one court undo/redo step and reopens
  with its layers; court edits are blocked while the editor is open; Cancel leaves the court
  unchanged; hardwood Apply keeps the floor id and texture settings; portable Save/Open bundles the
  editable archives with relative paths and validates their revisions. A missing or changed archive
  fails the open instead of flattening the artwork (unit test).
- **Color presets** are stored in `preferences.json` (`colorPresets`, normalized, at most 128, like
  `PaintColorPalette`; the WPF app kept them only for the session). Presets saved by the first
  preview build in WebView storage are moved there once.
- **Official run** (`tools/Build.ps1`): 27 core tests, type-check, release EXE
  (SHA-256 `7B0FDCCF…DBE6DE40`, 22.35 MB) and the hidden light, dark and compact suites (16 checks
  each) passed; `Install-Preview.ps1` re-ran the hidden suite on the installed copy from `%TEMP%`.
- **Dependencies:** EXE imports only Windows system DLLs (UCRT, DirectWrite/Direct2D, COM, user32 …).

Evidence scripts and outputs are in the session scratch area; `tools/Build.ps1` writes reports and
captures to `target/verification`.

## Known gaps

- **Edit Artwork** reuses the Canvas Rust editor's own React components (viewport, compositor,
  selection, layer thumbnails, inline text) and engine; the compact tool set, layer panel and court
  presets are a Court Creator shell rather than the full Canvas window. Canvas-only panels (masks
  UI, adjustments dialog, filters, channels, mips) are not exposed. `artwork.rs` keeps a subset copy
  of Canvas's `Edit` command enum and `artwork_text.rs` adapts Canvas's `text_edit.rs`; both must be
  re-synced if those Canvas commands change.
- The color picker, team colors, catalog, importer, text and artwork dialogs have not been tested by
  hand with a real mouse, keyboard or display scaling (hidden checks only).
- Text renders through `canvas-text` (DirectWrite), validated by Canvas against the released WPF
  renderer; a new text layer is not guaranteed byte-identical to a WPF-rendered PNG (saved text PNGs
  are reused as-is).
- DDS codec: `canvas-engine` BC7 instead of `texconv` (comparable error; not byte-identical).
- Hardwood Edit Artwork keeps the current texture settings; the WPF app re-read them from the floor's
  source object, which could reset them.
- **In-game acceptance of an exported IFF has not been performed** (the 1.7.17 regular export passed
  file checks but its in-game test is likewise pending).
