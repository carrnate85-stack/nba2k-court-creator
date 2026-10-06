# Shared Artwork Editor

## Workflow

Use Edit > Edit Artwork for the selected logo/graphic on the Logos tab, or the
selected hardwood elsewhere. The selected-court context menu and logo Edit button
also provide Edit Artwork. Court geometry, paints, markings and IFF rules stay in
Court Creator; this popup edits the source artwork, not court placement.

The host-owned popup embeds the released `TwoK.Canvas.Core` and
`TwoK.Canvas.Wpf` matching packages, minimum **0.6.0**. Shared tools provide selection, painting,
erasing, eyedropper, text, masks/layers, transforms, zoom and pan. The compact
configuration hides file management, document resize and mip controls; it supplies
court-green, white-marking and black-graphic color adjustment presets. It uses
the host's light/dark choice without changing application-global resources.

Supported host-driven edits use `CanvasEditor.Commands`: insertion, transforms,
selection, opacity, adjustments and undo/redo publish atomically with shared
history and automatic preview refresh. The host reports command notifications
in the editor footer. Lower-level pixel/text/mask fixtures remain in regression
checks to verify the legacy services and independent RGBA contract.

Apply accepts detached straight RGBA and an editable `.2kstudio` archive. It
prepares and validates new owned files before updating the selected court asset,
refreshes the preview, retains placement/UV coordinates and records one host undo
step. A changed source, invalid dimensions/metadata or preparation failure does
not apply. A failed Apply leaves the editor available for retry. Cancel, Escape
and window close discard the private draft; closing during Apply cancels it.
Only one artwork editor may open per Court Creator window; host mutations,
save/export and overlapping editor sessions are blocked during the transaction.

Editable archives and flattened PNGs live in
`%LOCALAPPDATA%\2K Studio\Court Creator\artwork` under unique directories.
Derived DDS files are produced only when the source document has a DDS profile,
using that original format/sRGB/mip/alpha profile. Original sources are never
overwritten. Committed artwork is retained for recovery and undo/redo. Failed or
canceled preparation removes only unchanged owned files; it never sweeps folders.
Process termination can leave an unreferenced private directory.

Save bundles the PNG, editable archive and optional DDS in the neighboring
`.assets` folder with relative paths and content revisions. Transfer that folder
with the `.court.json`. Reopening uses the archive, retaining layers, text, masks,
dimensions, channel labels and DDS metadata. A missing/changed editable archive
fails explicitly instead of silently flattening the project. Renamed, duplicated
or axis-mirrored logos retain the same editable source while keeping independent
court placement.

Game-data alpha remains independent of display transparency. PNG/archive pixels
retain alpha-zero RGB; DDS export uses the shared codec. Explicit `GameData`
artwork displays RGB as opaque in native previews and Python court composition,
then applies Court Creator's surface clipping/placement. Transparency artwork
retains its normal alpha behavior. This does not copy a source material channel
onto the assembled court's alpha: game-specific output rules remain unchanged.
DDS block compression is lossy, so decoded exports are compared with codec
tolerance, not byte-identical recompression.

## Shared Color Picker

Court layer swatches and the pinned color action instantiate the actual
`TextureStudio.ColorPickerDialog` from `TwoK.Canvas.Wpf`; Court Creator no longer
implements its own RGB slider dialog. Spectrum, hue, RGB/HSB, hex validation,
standard swatches and current-color restoration come from the central library.
The host adds Team Colors, hex-first focus, its layer heading and monitor fitting.
Theme resources are scoped to the dialog. Court colors remain RGB-only, without
changing artwork alpha. Inline hex fields and direct Team Colors remain available.

The host applies a valid accepted color as one court undo step. Cancel, invalid
input, unchanged colors and stale ownership leave the original court untouched.
The shared-picker gate verifies the real released dialog and its named controls,
palette integration, light/dark resources, RGB-only alpha and compact scrolling.
These are off-screen checks, not native mouse/focus acceptance.

## Build Dependency

A source build needs the x64 .NET 8 SDK, the app-owned Python backend and both
matching stable NuGet packages, minimum **0.6.0**. Normal Build and the source
checkout's launcher use `tools/sync_canvas_toolkit.py` to find the newest pair
under the sibling Canvas project's `artifacts/published-*-v*` folders.

Publish a new version of both central packages; on the next user-requested launch
Court Creator adopts it without manually updating its references or copying DLLs.
This is release synchronization, not hot replacement of a running app or a watcher
of unpublished source edits. No scheduled/background task is installed.

The sync checks package identity, stable version, WPF's matching Core dependency
and package/assembly hashes. It builds a private candidate, verifies both published
and test assemblies against the packages, then runs the off-screen artwork,
shared-color-picker and shared-control gates, including the real stock 4096 x 2048
Philadelphia 76ers workflow, all 23 inline color targets, normalized logo exports,
central theme/icons and low-memory safety. See [Native Studio](NATIVE-STUDIO.md)
for the five shared-control/image integrations.
Failed builds/tests leave `desktop` unchanged. Successful swaps use a serialized
recovery journal; the app must be closed. Source changes during verification,
same-version package republishing and unintended downgrades are refused.
No-change launches compare the recorded source/package/output revisions and skip
building. The shared updater lock serializes local build swaps and release updates.

Set `CanvasToolkitRoot` for a differently located central project, or
`CanvasToolkitFeed` for another private feed. Set `CanvasToolkitVersion` to pin
a stable release intentionally. A raw `dotnet` build defaults to 0.6.0;
use the build script for automatic latest-release resolution. For a deliberate
direct build:

```powershell
dotnet publish src/NBA2KCourtCreator/NBA2KCourtCreator.csproj -c Release --self-contained false -o desktop -p:CanvasToolkitVersion=0.6.0 -p:CanvasToolkitFeed=C:\path\to\feed
```

The `CanvasToolkitFeed` environment variable can also select that feed for
`Build Court Creator.bat` or Setup. A published copy includes the shared DLLs and
does not need the Canvas desktop application or the NuGet feed to run. Toolkit
binaries are not vendored into this source repository. Newer Canvas development
sources are not substituted for a published release. Standalone copies do not
attempt a local development rebuild; they retain bundled libraries and use the
normal complete-app release updater. `desktop/.canvas-toolkit.json` records the
actual installed version and validated file hashes. `studio-build.json` records
the court host contract separately so compatible complete-app updates can include
newer toolkit versions. Public binary publishing is still a separate operation.

Preserve `updates/canvas-install.json` and any named backup if recovery is blocked.
A normal successful/failed build cleans its temporary stage. Abrupt process
termination may leave an unreferenced `updates/canvas-build-*` directory; it is
not swept automatically. The active journal/backup must never be deleted to
bypass a recovery error.

The shared engine transitively brings BCnEncoder.Net 2.3.0,
BCnEncoder.Net.ImageSharp 1.1.3, CommunityToolkit.HighPerformance 8.4.0 and
SixLabors.ImageSharp 3.1.12. Their package metadata/license documents are retained
under `desktop/ThirdParty`. ImageSharp's supplied split-license conditions and
all applicable distribution terms require review before public binary sharing;
this integration does not grant a commercial license or publish a release.

## Verification

```powershell
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/artwork-editor-check --artwork-editor
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/color-picker-check --color-pickers
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/shared-controls-check --shared-controls
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/shared-artwork-audit
runtime\python\python.exe -B -m unittest discover -s tests -p 'test_*.py'
runtime\python\python.exe -B tests/check_release.py
```

The focused checks use actual versioned shared controls, independent light/dark
sessions, all configured tools, editable text/masks, compact renders, source
isolation, Apply/Cancel/Escape/close, cancel during Apply, retry/cleanup, one host
undo/redo, unchanged placement, hardwood preview, portable save/reopen, source
revision failure, dimension failure and DDS metadata/data-alpha round trips.
Backend checks cover normal/large logo and hardwood RGB-data-alpha rendering
before resampling without modifying source bytes.

These are off-screen/in-process checks, not computer automation. Real mouse/focus,
mixed-monitor DPI and exported IFF loading in NBA 2K27 still need manual acceptance.
No app is automatically opened after builds; no public binary release is created.

## Verified Build

Version 1.6.5, audited 2026-10-06:

- Logo panel has clearer headings, a live image-layer count, outlined rows,
  Copy/Delete icons, direct Edit and guarded Up/Down order buttons. Position,
  Size and Rotation are separate boxes; Alignment is removed. The aspect lock
  stays in Size and the central Canvas palette remains unchanged.
- Focused logo actions/layout, inspector ownership, draft save/reopen, gesture
  lifecycle and keyboard checks passed against matching Core/WPF 0.7.0. Compact
  and full-size light/dark renders were inspected; Rotation is reachable by
  scrolling and numeric edits preserve exact history.
- Artwork, color-picker and shared-control integration gates passed before
  publication. Runtime/build readiness reports ready; the desktop shortcut
  still targets the local launcher. No app was opened automatically.
- Temporary 1.6.5 package audit passed: 47 files, 7,376,116 expanded bytes,
  3,482,944 archive bytes. Tampering rejected; rollback and seven personal files
  preserved; source bytes/timestamps unchanged; temporary installation removed.

Evidence is under `outputs/logo-panel-final-verified`, `outputs/logo-panel-inspector`,
`outputs/logo-panel-save`, `outputs/logo-panel-gestures` and
`outputs/logo-panel-keyboard`; generated assets remain untracked. Real native
mouse/focus remains manual acceptance, and no public binary release was uploaded.

Version 1.6.4, audited 2026-10-06 (historical baseline):

- Logo actions now provide compact Flip X/Y and Mirror X/Y dropdowns; the Center
  button is removed. Mirror copies retain artwork orientation. See
  [Native Studio](NATIVE-STUDIO.md#logo-actions) for axis semantics and checks.
- Focused logo-action, gesture-lifecycle and keyboard-command checks passed against
  matching Core/WPF 0.7.0: exact undo/redo, portable reopen, four-slot/selection
  limits, committed drag poses, cancellation-time guards and compact light/dark
  renders. The three central integration gates also passed before publication.
- Temporary 1.6.4 package audit passed: 47 files, 7,373,344 expanded bytes,
  3,480,926 archive bytes; tampering rejected, rollback and seven personal files
  preserved, original source unchanged and temporary installation removed.
- The launcher build was updated without launching the app. Verification was
  off-screen; native dropdown mouse/focus acceptance remains manual QA.

Evidence is under `outputs/logo-actions-check`, `outputs/logo-actions-gestures`
and `outputs/logo-actions-guards`; generated assets remain untracked.

Version 1.6.3, audited 2026-10-06 (historical baseline):

- All five conversions completed: central theme/token aliases, actual Canvas tool
  icons, shared RGBA/RGB previews, guarded raster/DDS/profile-aware logo decoding
  and normalization, and shared allocation preflight. Existing court geometry,
  inline hex/Team Colors controls and whole-court history remain host-owned.
- Full off-screen native suites passed against matching Core/WPF 0.6.0 and 0.7.0,
  including all 15 layouts, managed import safety, normalized-logo undo/redo,
  portable save/reopen, and real 8192 x 4096 PNG/BC7 IFF export/conversion.
- The real stock Philadelphia 76ers artwork workflow passed: Commands edits,
  selection, undo/redo, Accept, save/reopen and separate Cancel. Source dimensions,
  selections, independent alpha and hidden RGB checks passed.
- The verified sync found the newly published matching 0.7.0 pair, passed artwork,
  color-picker and shared-control gates, checked package/assembly hashes and
  installed it. A subsequent sync skipped rebuilding. The raw-build minimum and
  default remain 0.6.0; the installed build records 0.7.0.
- Backend suite: 415 tests run, 414 passed, one platform-specific skip. New sync
  checks reject releases below 0.6.0 and preserve the prior build if the third gate
  fails. Low-memory checks preserve draft pixels, source files and undo history.
- Light/dark and compact off-screen renders inspected. Runtime/build readiness
  reported ready; the desktop shortcut still points to the local native launcher.
- Temporary package audit passed: 47 files, 7,371,868 expanded bytes, 3,480,120
  archive bytes. Tampering rejected; rollback and seven personal files preserved;
  original source bytes/timestamps unchanged and temporary installation removed.
- No app was launched, no native mouse/focus automation was performed, and no
  public binary release was uploaded. In-game loading remains manual acceptance.

Evidence is under `outputs/shared-conversions-final-check`,
`outputs/shared-conversions-final-audit` and
`outputs/shared-conversions-canvas-070-audit`; generated assets remain untracked.

Version 1.6.2, audited 2026-10-06 (historical baseline):

- Matching published Core/WPF 0.6.0 installed through the verified sync path;
  package and assembly hashes checked. A subsequent sync skipped rebuilding.
- Court color actions use the actual shared Canvas WPF dialog. Shared controls,
  RGB-only alpha, original restoration, palette acceptance/cancel/invalid/nesting,
  closed-dialog safety, hex-first selection and isolated light/dark themes passed.
- All 23 inline targets, one-step undo/redo, stale ownership and compact scrolling
  passed. Light/dark and compact picker screenshots were inspected. The focused
  picker gate also passed against the documented minimum 0.4.1 packages.
- Full native suite passed, including real stock artwork Accept/save/reopen/Cancel
  and full-resolution PNG/IFF export/conversion. All windows remained invisible.
- Backend suite: 413 tests run, 412 passed, one platform-specific skip. Sync tests
  verify artwork then color-picker gates and preservation of the prior build if
  the second gate fails.
- Temporary package audit passed: 47 files, 7,329,842 expanded bytes, 3,464,955
  archive bytes. Tampering rejected, rollback and seven personal files preserved.
- Runtime/build readiness reported ready. Desktop shortcut still targets the
  local launcher. No app was started and no public binary release was uploaded.

Baseline evidence is under `outputs/shared-color-picker-full-audit` and
`outputs/shared-color-picker-check`; binaries and game assets remain untracked.
Real native mouse/focus and in-game loading remain manual acceptance checks.

Version 1.6.1, audited 2026-10-06 (historical baseline):

- Matching published Core/WPF 0.4.1 packages and exact assembly hashes verified.
- Full native suite passed, including actual-size PNG/IFF export/conversion,
  placement, compact layouts and the shared artwork checks.
- Real stock Philadelphia 76ers 4096 x 2048 hardwood: command selection,
  insertion/transform, automatic preview/notifications, undo/redo, Accept,
  portable save/reopen with retained selections/layers, and separate Cancel passed.
- Existing DDS profile, dimensions, hidden RGB and independent alpha checks passed.
- Central sync tests cover newest stable pairs, no-change checks, first-time SDK
  selection, no source/standalone feed, missing/mismatched/stale packages, failed
  gates, source changes, busy builds, immutable versions, downgrade prevention,
  directory publication rollback and interrupted recovery.
- Launcher and Setup use the verified sync path. No app was opened automatically.

Baseline native evidence is under `outputs/canvas-041-full-audit`; the source
repository includes repeatable checks, not extracted game assets or binaries.

Previous version 1.6.0, audited 2026-10-06 (historical baseline):

- Released toolkit 0.3.1 confirmed in the published dependency manifest.
- Full native suite passed against the final source, including 15 layouts and
  real 8192 x 4096 PNG/BC7 IFF conversion/export round trips.
- Focused shared-editor checks passed within that full suite: independent sessions,
  tools/presets, Apply/Cancel/Escape/host-close, close during Accept/preparation,
  retry, exact one-step court undo/redo, placement and preview refresh,
  portable layers/text/masks, complete DDS profile preservation and data-alpha
  round trips, dimension/source failure safety, cleanup and mutation guards.
- Backend suite: 398 tests run, 397 passed, one platform-specific skip.
- Compact editor screenshots inspected at 1080 x 720 and 920 x 560.
- Local desktop publication succeeded; all 11 required native artifacts match
  the tested Release build. Runtime-only Setup diagnostics report ready.
- Temporary package audit passed: 45 files, 7,258,172 expanded bytes,
  3,436,065 archive bytes; tampering rejected, rollback and seven personal files
  preserved, all installed bytes correct, temporary installation removed.
- Existing desktop shortcut still targets the local launcher. No Court Creator
  window was opened; no public binary release was uploaded.

Native evidence is under `outputs/shared-artwork-final-verified`; focused editor
renders are under `outputs/artwork-editor-check`. The commands above reproduce
the checks. Source sharing remains separate from public binary release approval.
