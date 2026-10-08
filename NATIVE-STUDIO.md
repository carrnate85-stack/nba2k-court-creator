# Court Creator Native Studio

## Ownership

- `src/NBA2KCourtCreator/Studio`: the active WPF workspace and application workflow.
- `src/TwoK.Studio`: court placement canvas, transforms, anchors, and thin central theme/icon adapters.
- `TwoK.Canvas.Core` / `TwoK.Canvas.Wpf` matching releases (minimum 0.9.4): the shared native pixel editor,
  documents, layers/masks, text, tools and DDS codecs. See [Shared Artwork](SHARED-ARTWORK.md).
- `court_creator`: persistent Python engine for stock discovery, geometry, image composition, conversion, and IFF exports.
- `electron`: retained fallback, not the default launcher target.

## Hardwood Textures

The main hardwood fills the court surface. Hardwood selection lives in a strip
along the bottom of the workspace, leaving the right inspector for colors, lines
and logos. The main selector stays visible. Checking 2-point hardwood replaces
the checkbox with a second selector for the left and right two-point areas,
clipped to the outermost visible three-point enclosure (NBA, College, then High
School). Only High School enabled confines it to that line. With all three-point
lines off, the second hardwood is hidden but its selection is retained.
Its close button disables the second texture and restores the checkbox while
retaining the texture and its adjustments. Clicking either selector opens its
catalog and selects that texture for adjustment; the active selector has an accent
border. Both selectors show their own thumbnail and selected texture name.
When the second texture is enabled, the two selector cards have equal widths;
the close button sits outside their equal-width columns.
The enabled state survives recovery, undo/redo and portable save/reopen, and
native previews and PNG/IFF exports honor it. Older projects with a secondary
texture keep it enabled. Choosing a new secondary texture enables it.
The second hardwood also sits beneath both primary and secondary keys. Uncolored
(unchecked) key paint reveals that hardwood; enabled paint draws on top. Markings
and logos remain above the hardwood and paint. Preview and export use the same
visible-line boundary without regenerating floor assets.

The hardwood adjustment bar opens by default and stays open through inspector-tab
and appearance changes until another canvas tool is selected. Five equal-width
groups use the released Canvas toolkit's slider and button styles, with labels
above their controls so Brightness, Contrast, Saturation, Grain scale and Rotation
remain visible at the minimum window width without scrolling or an overflow menu.
The Main hardwood / 2-point hardwood dropdown chooses which texture the sliders
and reset edit without opening its catalog. Each target keeps independent settings.
A valid number draft commits to its original texture before switching targets;
invalid drafts keep their target until corrected or cancelled. At narrow widths
the dropdown moves above the sliders so every control remains visible.
Tool adjustment bars attach directly to the workspace's top-left edge beside the
left tool rail, directly under the document header. They overlay the canvas from
outside its inset card, with no top or left gap. The workspace keeps its full
bounds; the fitted artwork alone is
centered up to 24 DIPs lower inside it, limited by available space so fitted side
viewports stay fully visible. Fit uses the original scale, and zoom centers on
that artwork position. Showing or hiding hardwood or logo controls leaves the canvas
size, court screen position, zoom and pan unchanged, including in side viewports.
Undo/redo keeps the current workspace appearance while a transparent input shield
blocks overlapping edits. Unchanged hardwood and logo previews are reused only
when their saved source revision, path and editable-artwork metadata match the
retained preview. Unchanged color rows and logo layers retain their existing
controls and selection. Changed textures rebuild their drawing without decoding the
source again. Opening a project still validates and reads its external assets;
export and save retain their source-revision checks. Failed history preparation
keeps the existing court and history entry available for retry.
Choosing any left toolbar tool preserves the current right inspector tab. Move
and Transform can edit selected logos while Colors & Lines stays open; their
adjustments follow the active tool rather than the inspector tab. Hardwood opens
its adjustments without changing the tab. Import previews keep logo editing and
the eyedropper unavailable.
Hardwood controls regain their enabled state after document operations and logo
inspector synchronization, including undo and redo.
The reset icon stays at the right. Clicking a percentage or degree value opens
integer entry; Enter or leaving the field applies it, Escape cancels, and
out-of-range values block save until corrected. Reset affects only the hardwood
selected for editing.

## Primary Color, Paint and Text

Eyedropper stores the sampled primary color in the bottom-left swatch without
changing the selected row or court history. Clicking that swatch opens a primary
color picker. The primary color survives restarts in preferences. All shared
Canvas pickers show Primary first, optional Secondary next, then Preset Colors.
Saved presets have a right-click Remove preset action. Court pickers use one
left-hand Team Colors button, opening the court team chooser.

Paint (G) fills the clicked primary key, secondary key, two-point region,
three-point court area or outside region with the primary color. It enables that
region's paint and records one undo; painting the same color again is a no-op.
Turning a paint checkbox off reveals the hardwood underneath.
Two-point paint uses the same outermost enabled three-point boundary as the
hardwood. Changing the visible line automatically reshapes existing paint and
the remaining outer court area, including bucket hit testing and PNG/IFF export.
With no three-point line enabled, two-point paint is hidden while its color is
remembered. The Colors group ends with Outside Color; court Lines remain below.
Center Circle - Inner sits directly above Center Circle - Outer in the Lines list.

Text (T) opens the shared Canvas text editor at the clicked court position, with
font, size, fill, outline, bold/italic, alignment, line height and letter spacing.
Clicking existing text or Edit selected text reopens its settings. Text keeps its
position and transform when edited. Cancel leaves the court unchanged. Metadata
and a transparent rendered PNG survive undo, duplicate and portable save/reopen;
the PNG exports even on a PC without the original font. Text uses one of the four
court artwork slots. Move and Transform adjust its placement.
Colors use Canvas's adjustment service; scale/rotation repeat the full-court
texture around its center before clipping, keeping the texture inside its area.
Original image files and alpha are preserved. Live native previews are debounced
and rendered off the UI thread; dragging a slider records one undo step.

Both floor references and settings survive recovery, undo/redo and project
save/reopen; portable project saves bundle both source images. The Python PNG/IFF
exporter uses matching color math, alignment and clipping at full resolution.
Older projects keep one hardwood with default settings.

## Logo Actions

The logo action row has compact Flip and Mirror dropdowns instead of a Center
button. Flip X/Y changes the selected artwork in its own horizontal/vertical
coordinates without moving or rotating it. Mirror X/Y creates a copy at the
reflected court X/Y position: left/right across midcourt, or top/bottom across
the court's lengthwise centerline. Copies retain rotation, flip state,
appearance and editable artwork metadata. Mirror and Copy share the four-logo
limit; Flip remains available when all four slots are occupied.

Copy and Delete use compact icon/text buttons. The logo action bar contains no
Edit command; artwork editing belongs in 2K Canvas, while this bar handles court
placement. Up/Down move the selected row in the existing
bottom-first list: up toward the back, down toward the front, with disabled end
buttons and one undo step per reorder.

The panel shows an Image Layers heading and live count, outlined selected rows,
and an Add Image primary action. Numeric transforms no longer occupy the sidebar.
A selected logo in Move or Transform automatically populates a slim options strip above the court preview:
X/Y position, W/H in texture pixels, an aspect lock between W/H, and rotation in
degrees. Values update live during move, resize and rotation gestures, without
rebuilding controls or adding preview history. Hand/Zoom, deselection, another
section or New hides the strip. Its 42-DIP row remains reserved, so visibility
changes do not move or resize the court canvas or change its screen mapping.
Sidebar height stays unchanged. Alignment controls
are removed; the central Canvas palette remains authoritative in light/dark modes.

Rotation still uses Court Creator's dedicated top handle or the degree field;
this layout update does not implement Photoshop's outside-border rotation gesture
or a multi-operation Enter/Escape transform session. Those behaviors belong in
the shared Canvas tools, exposed for the court placement canvas to reuse.

Each edit uses one whole-court undo step and the existing live preview. Menus
respect selection and document-mutation guards. The focused `--logo-actions`
check covers both axes, source preservation, exact undo/redo, portable save/reopen,
capacity, order guards, absence of an Edit button, layer counts, contextual transform visibility and light/dark
compact layout; gesture and keyboard checks also cover
vertical flipping, committed poses and cancellation-time guards. These are
off-screen checks, not native popup mouse/focus automation.

```powershell
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/logo-actions-check --logo-actions
dotnet run --project tools/CourtStudio.Smoke -c Release -- outputs/live-transform-check --live-transforms
```

## Shared Controls and Images

`src/Directory.Build.props` gives the host and placement library one matching
Canvas version/feed. `TwoK.Studio.StudioTheme` delegates its palette and theme
state to central `TextureStudio.StudioTheme`; its three chrome resource names are
aliases, not copied colors. `TwoK.Studio.ToolIcon` adapts the existing court enum
to the actual central `TextureStudio.ToolIcon`, preserving toolbar dimensions.

Logo raster decoding uses `ImageFormatService` with the shared Windows
`WicRasterProfileConverter`; DDS uses `WpfTextureCodec` and its complete-2D
validation. Conversion is recorded when the shared profile adapter actually runs,
not inferred solely from header metadata. Path-based decoding reads a private,
create-new guarded snapshot, with the existing byte limit, source hash, identity,
cancellation and ownership-aware cleanup. No borrowed source is overwritten.
PNG/BMP can retain original bytes when no profile/orientation conversion is needed.
Other formats and converted artwork become owned PNGs through the shared encoder;
both the importer and direct placement use the same normalization. Save/reopen and
Python court export consume those normalized pixels, not the unconverted original.
The shared PNG writer preserves straight alpha and RGB beneath alpha zero.

`PreviewRenderer` supplies RGBA logo and RGB game-data views. Game-data preview
resizing disables alpha premultiplication, retaining hidden RGB without changing
source alpha. The existing finite revision caches, thumbnail scheduling, clipping
and court compositor remain host-owned.

`StudioImageMemory` calls central `MemoryPreflight` before image decode/conversion,
preview buffers, cleanup snapshots/flood-fill/reset and accepted-artwork copies.
Its injected available-memory source is only for off-screen rejection tests;
normal operation uses the shared physical/commit headroom check. Fixed file/pixel
limits and existing cache/history budgets still apply. Preflight estimates are
conservative and do not guarantee that third-party allocation cannot later fail.

`--shared-controls` verifies actual released theme/icon components, both palettes,
RGBA/RGB parity, hidden alpha-zero RGB, PNG/TGA/WebP/DDS/profile/orientation imports,
normalization in UI and direct placement, source preservation, cancellation and
low-memory retry/draft/history safety. A real normalized-logo workflow checks
undo/redo, portable save/reopen and 8192 x 4096 Python export. This gate is required
alongside artwork and color-picker checks before an automatic library update.
These checks never open native windows; pointer/focus and in-game acceptance remain
manual QA. Court geometry, IFF rules and whole-court project history stay separate.

The Python worker reads binary JSON-line requests with a 1 MiB frame limit and
bounded reads, including while discarding an oversized frame. It reports that
error before draining the remainder and accepts the next newline-delimited frame.
Only UTF-8 (with optional BOM), finite JSON numbers and at most 32 nesting levels
are accepted. LF and CRLF are supported; an unterminated EOF frame is rejected.
Request envelopes require an object, a safe integer ID and a supported command
array with exact argument counts/flags. Boolean options must be actual booleans;
text arguments reject null characters, invalid Unicode and lengths over 32768.
All existing command forms, including optional importer arguments, are retained.
Backend errors are capped at 4096 characters, and non-finite/unserializable results
become error replies rather than terminating the worker. Malformed frames with no
trustworthy ID receive a null ID; the native client's existing mismatch handling
restarts its worker for that protocol failure. These framing guards do not impose
a worker-side stdin idle deadline or cap every backend operation's resource use.
`python -B -m unittest discover -s tests -p test_service_input.py` reproduces the
old non-object crash, checks command compatibility and bounded read/discard behavior,
and runs real workers recovering from oversized/invalid-UTF-8/non-object frames.

Backend request files and palette/preset/floor-library metadata use
`court_creator/json_io.py`. Metadata reads stop at 8 MiB and request files at
16 MiB, with file-size preflight plus a bounded read that also catches file growth.
Parsing rejects non-finite values (including exponent overflow), invalid encoding
and nesting beyond 32 container levels. UTF-8, UTF-8 BOM and BOM-marked UTF-16
are supported. Requests must be objects and fail before render/export preparation;
they do not silently become empty settings. Optional metadata keeps its fallback
behavior without rewriting a damaged file. Wrong collection shapes/row types are
ignored; valid palette values are unchanged, invalid textual fields/colors are
omitted, and unusable custom bounds use the existing court bounds. Structurally
invalid newest floor indexes can fall back to an older valid library as before.
Adding a custom floor uses a strict catalog read before geometry preparation or
copying the image, so an unreadable/oversized/wrong-root catalog cannot become an
empty catalog that overwrites the original. Successful appends retain existing
rows (including missing assets) and extra top-level fields, rather than saving
only the currently usable rows.
Serialized catalog size is checked before writing, and a failed append removes
the newly copied floor without deleting existing artwork or the source image.

Export/catalog staging and custom-floor image staging use the same bounded cleanup
helper. Windows access/sharing/lock errors (5/32/33) get at most four attempts with
25/50/100 ms delays; unrelated errors are not retried. Each attempt compares the
original filesystem identity and rejects linked/shared/replaced staging files.
A cleanup failure preserves the original render/decoder/sync/publication/cancel
exception and adds a note naming the retained path. Worker error replies include
these notes within their existing 4096-character cap. Without a prior error,
cleanup failures remain errors rather than silently succeeding. Cleanup visits
only the exact owned path, never a folder sweep. Persistent locks or replacement
files can therefore leave a reported stage behind; there is no startup janitor.
Identity checks do not detect in-place edits or eliminate an uncooperative change
between the final check and unlink, and do not undo an export already committed.
`python -B -m unittest discover -s tests -p test_export_publication.py` covers real
temporary/permanent Windows locks, bounded retries, original-error preservation,
retry-time replacements/shared aliases and filename reuse after publication.

Publication also checks staging ownership before opening for flush, validates the
actual opened file handle, and rechecks the staging identity and destination's
source aliases before every replace attempt, including after validation callbacks.
Only the original unshared regular file is accepted; replaced files, hard-linked
files and Windows reparse points are rejected without publishing or removing the
foreign path. Custom image imports repeat the same ownership check after decoding
and on filename-collision retries. Regression tests cover actual PNG rendering,
retry-time substitution, shared aliases, a redirected sync handle, callback changes
and a real Windows junction. File-symlink coverage is skipped on hosts lacking the
required privilege. These identity checks do not pin in-place staging byte edits
or eliminate a non-cooperating change between the final check and rename; they do
not provide a transaction rollback after a successful rename.

Custom-floor storage now uses `court_creator/custom_floor_store.py`. Image bytes
copy to a unique private staging file with the 512 MiB asset limit, source hash
recheck, file flush and full image validation (16384 pixels per side / 64 MP).
Publication uses a no-overwrite rename on Windows (hard-link publication on other
platforms); existing paths, concurrent filename collisions and names reserved by
missing catalog assets are never replaced. Thread and process-level file locks
serialize cooperating imports, with bounded lock waits. The small `.lock` file
persists so another waiting process cannot lock a different replacement inode.

Catalog JSON writes stage beside the target, flush, then atomically replace it.
The exact-byte revision read with the parsed snapshot is checked before each
publication attempt/retry. External catalog edits are retained; an error reported
after a completed rename is recognized by its committed payload hash so referenced
artwork is not deleted. Linked/reparse-point storage and shared catalog/lock files
are rejected. Exception rollback removes only an unchanged owned copy that the
current catalog does not reference; externally edited/adopted copies are retained.
Unrelated files and staging files are never swept. The two files are not a single
filesystem transaction: process termination can leave an unreferenced complete
copy or private staging file. Revision checks also do not promise protection from
every non-cooperating filesystem race or power-loss scenario.
`python -B -m unittest discover -s tests -p test_custom_floor_store.py` covers
partial staging writes, sync/image/source failures, real Windows sharing locks,
retry-time external edits, ambiguous post-commit errors, missing-name reservations,
safe cleanup/adoption, shared aliases/junctions and real concurrent processes.
`python -B -m unittest discover -s tests -p test_json_input.py` covers these limits,
encoding/depth boundaries, source bytes/timestamps, mixed metadata, the real team
catalog and a persistent worker rejecting a bad request then loading stock data.
The geometry cache and conversion journals retain their separate readers; this
does not claim every JSON reader shares these limits.

Read-only game discovery uses the bounded JSON reader for saved installation
settings, with a 1 MiB cap, finite numbers/depth checks and the same BOM support.
Wrong top-level shapes and invalid path values are ignored rather than crashing;
valid saved roots still precede environment roots. A valid known root returns
before Steam metadata or registry discovery. Explicit invalid selections report
failure without silently choosing another installation. Discovery never rewrites
the input settings or Steam files.
Steam library metadata is read as bounded UTF-8/BOM bytes (1 MiB), with stat
preflight and a growth-safe read before tokenization. Its existing supported
key/value forms retain quoted/unquoted tokens, escaped Windows paths, modern
object and legacy string library entries. Comment scanning preserves slashes
inside quoted strings; malformed quotes/structure, over 65536 tokens or over 32
object levels fail with a bounded ValueError and retain default-library fallback.
Unsupported registry/path value types and unavailable candidates are ignored.
`python -B -m unittest discover -s tests -p test_game_discovery.py` reproduces the
previous shape/recursion/comment failures and checks priorities, BOMs, exact
source bytes/timestamps, limits, growing files, permissions and valid libraries.
This is a bounded Steam-library reader, not a complete Valve KeyValues
implementation. Filesystem/network lookup deadlines remain a separate concern.

Game-folder persistence now saves only after successful stock-base preparation or
validation of a usable cached base. Explicit selection reports a failed settings
save; automatic preparation can still return the prepared base while leaving
damaged, unwritable or concurrently edited settings unchanged. Existing object
fields are retained, and a no-op preserves exact encoding, bytes and timestamp.
Input and serialized output both have a 1 MiB budget. Invalid existing JSON or
wrong top-level shapes require repair or renaming; they are never reset silently.
Thread and persistent process-file locks serialize cooperating writers, with
bounded acquisition waits. Linked folders, shared/reparse-point settings or lock
files, and game-source collisions are rejected before writing. Exact snapshot
revisions are checked on every publication attempt, including sharing retries;
an ambiguous post-commit error is recognized only by the complete payload hash.
Staging publication and cleanup retain the export helper's ownership checks.
The settings and base cache are not one filesystem transaction: an explicit
settings-save failure can leave a successfully prepared cache available. Revision
checks do not eliminate the final check-to-rename race with non-cooperating tools.
The generic JSON writer remains separate.
`python -B -m unittest discover -s tests -p test_game_settings.py` checks field
retention, no-op bytes, extraction/inspection/cleanup failures, damaged and
oversized documents, shared aliases/junctions, real Windows destination locks,
retry-time external changes, post-commit errors and concurrent threads/processes.

Legacy interrupted-extraction recovery uses bounded 64 KiB records with BOM,
finite-number/depth, path and completion-marker validation. Orphaned backups are
retained for manual recovery rather than restoring into a discovered or guessed
installation. Ordinary unshared files and non-linked cache/mod folders are
required. Recovery file reads are capped at 512 MiB and stream SHA-256 revisions
with filesystem identity, size and modification-time checks. The recorded game
must still be available; no phantom mod folder is created for a missing game.
The current floor is copied, synced and read back before an original backup is
staged into the game folder. Exact record, backup, current-floor and preserved-copy
snapshots are rechecked on publication retries. Sources are never moved away
before restoration. A floor added when there was no original backup is retained
in the game folder as well as in its preserved copy. Copies use exclusive handles,
owned staging and byte readback; changed/shared/linked paths are not swept.
An exact `restoredRevision` marker is published before backup cleanup. Once the
restore commits, cleanup locks or reported post-commit errors can be retried
without another restore or duplicate floor copy; a later user edit retains its
record and backup for manual review instead of overwriting the edit. Cleanup
rechecks identities and bytes on each bounded Windows sharing retry. Errors after
preservation include the complete retained-copy path.
This is not a multi-file/power-loss transaction or protection against every final
check-to-rename/unlink race. Ambiguous old records without a backup or completion
marker require manual recovery; complete preserved copies are intentionally kept.
Modern stock extraction remains isolated in scratch space and does not create
these legacy journals or change the installed game floor.
`python -B -m unittest discover -s tests -p test_extraction_recovery.py` reproduces
the former unsafe cases and checks invalid/oversized records, BOMs, orphan/shared
backups, inconsistent state, copy/readback failures, cancellation, real locks and
junctions, same-size retry-time edits, post-commit resumption, completed cleanup
and foreign preservation filenames. All game/mod fixtures are temporary. Failures
before restoration can intentionally leave complete preserved copies; those are
not automatically removed or reused without verified provenance.

Stock-base cleaning and fresh-cache publication now use owned staged exports,
sync-before-publish, bounded Windows sharing retries and ownership-aware cleanup.
Exact full-file snapshots (identity, size, modification time and SHA-256) retain
cache edits or externally created caches on every publication attempt. Failed
validation/copy/sync/publication leaves the preceding cache available; a reported
error after commit leaves the complete cache reusable on the next request.
Compressed and total inflated archive sizes are capped at 512 MiB, with generated
archive preflight before CRC inflation, all-member CRC checks, exact cleaned scene
validation and target texture inspection. Scratch-copy bytes are read back and
rechecked against their pinned source before publication. Game files and packs
remain read-only: a cache inside an available game installation, source aliases,
shared cache/lock files and linked cache folders are rejected before writing.
Process locks pin their opened file identity and validate it before acquisition
and guarded cache publication; persistent lock files are not swept. Normal valid
cache hits still work offline without full-file hashing, copying or rewriting.
Unavailable installations are discovered once per request, not rescanned after
an offline cache miss. An optional read-only discovery pass identifies source
boundaries without making a usable cache depend on a present game installation.
The cache/settings pair is not a multi-file transaction, and this does not remove
the final non-cooperating check-to-rename race or add a universal disk/network
deadline. Full-cache CRC validation is performed during cleaning/preparation, not
on every valid cache hit. `python -B -m unittest discover -s tests -p
test_base_cache_publication.py` checks same-size/same-time external edits,
retry-time conflicts, archive budgets, personal aliases, real lock failures,
primary-error cleanup, foreign stages, offline reuse and independent processes
sharing one extraction. All game/mod inputs in this suite are temporary fixtures.
The 2026-10-05 real stock audit also rebuilt the BC7 8192x4096 base successfully
with this guarded path: fresh base 7.655 seconds, reused base 13.286 milliseconds,
fresh geometry 6.100 seconds. Rebuilt geometry matched the current app, installed
input/mod metadata and protected input hashes were unchanged, the existing base
cache was unchanged, and scratch files were removed. These are local preparation
measurements with OS caches uncleared, not cold-start, Electron or in-game checks.

IFF packaging pins the base's open read handle and an exact identity/size/time/
SHA-256 revision before ZIP metadata is read, then validates again after parsing.
Unbuffered reads prevent a later validation from reusing old
prefetched bytes. Base changes during inspection, encoding, copying or publication
retries abort the export and retain the preceding output and the external edit.
Read-only hard-linked bases remain supported; this adds no source writes or lock files.
Compressed and total inflated base sizes are capped at 512 MiB before converter
work, replacement payload totals are checked before staging, and generated sizes
are checked before all-member CRC inflation. Missing replacement scene entries
also fail before conversion. The ZIP writer uses a borrowed creation handle from
`staged_export(writable_stream=True)`, checks pathname/handle ownership before
truncation and each entry, then flushes, verifies and closes before publication.
Creation identities now come from `fstat`, not a later pathname lookup. Path-mode
callers retain their API; close, sync, cancellation and cleanup errors preserve
the primary failure and do not overwrite the preceding output. These checks do
not provide a multi-file/power-loss transaction, a universal I/O deadline or remove
the final non-cooperating validation-to-rename race. Full hashing is export-only,
not added to startup or cached preview selection. `python -B -m unittest discover
-s tests -p test_iff_export_base.py` covers exact-byte changes with identical size/
timestamps, inspection/copy/retry boundaries, source aliases, archive budgets,
foreign staging and preserved outputs. `test_export_publication.py` also exercises
the stream writer's sync/publication/interruption/close failures.
The final 2026-10-05 stock audit with unbuffered archive reads rebuilt the base
in 7.676 seconds and geometry in 5.647 seconds; reused base/geometry preparation
took 12.616/57.289 milliseconds. Geometry matched, installed inputs and the
existing cache were unchanged, and scratch files were removed. OS caches were
not cleared; these are preparation timings, not visible startup measurements.

DDS encoder output now shares the import header validator before export staging.
The generated texture must be one supported 2D surface, with the selected format,
8192x4096 dimensions, matching mip count and the exact block payload size for all
mips. Arrays, cubemaps, volume headers, truncated payloads and trailing data are
rejected. Legacy import remains tolerant of trailing data. The encoder file is
size-preflighted against 256 MiB before opening; body reads use the validated
file size plus one byte to detect growth. Ordinary unshared file identity,
size/time and the inspected header are rechecked before retaining immutable DDS
bytes. This bounds encoder reads, not total Python/encoder memory, and does not
prove every compressed block's visual correctness or in-game shader behavior.
`python -B -m unittest discover -s tests -p test_dds_export_validation.py` covers
BC1/BC3/BC7 linear and sRGB headers, NPOT mip sizes, exact limits, file changes,
shared files and failed exports preserving prior outputs and source bytes/times.
Header interpretation follows Microsoft's
[DDS format guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dx-graphics-dds-pguide).
The 2026-10-05 real export audit passed this stricter path with an 8192x4096
BC7_UNORM texture, 14 mips and exactly 44,739,428 bytes. Archive CRCs, decoded
paint/line pixels and all four placed logos passed; only the target DDS and
intended scene changed, and the source base hash was unchanged. This is an
off-screen file/decode audit, not in-game verification.

Floor scenes now use the shared bounded in-memory JSON parser rather than an
unrestricted `json.loads`. Raw UTF-8 scenes retain their 16 MiB budget before
decoding, accept an optional UTF-8 BOM, and support both complete objects and the
game's outer-brace-free fragments. The temporary fragment wrapper adds only two
bytes; the logical 32-level nesting limit is the same in both forms. Non-finite
numbers, floating overflow and invalid encodings/types fail as floor-scene errors.
Model/Material/Effect mappings, model primitives and material/effect resource and
parameter containers are checked before traversal. Missing/inherited primitive
materials and opaque metadata remain supported; unknown fields are not discarded.
Cleaning and native-material updates share a finite-only serializer and reject
output expansion beyond the scene byte budget, without changing the input bytes.
Replacement scenes must contain a recognized full-court floor surface and pass
these checks before base inspection, pixel loading or DDS converter work. Mutable
replacement byte arrays are frozen before conversion; valid supplied scene bytes
remain exact in the resulting IFF. This checks the supported container structure,
not every game's shader parameter meaning, binary reference or in-game behavior.
`python -B -m unittest discover -s tests -p test_scene_validation.py` reproduces
the former invalid-number/shape acceptance and checks budgets, exact depth,
wrapping/BOMs, inheritance, metadata, serialization growth, unchanged input/output
files and an actual archive build during a mutable scene-buffer edit. The current
real cached 103,454-byte stock scene also passes parsing, cleaning and native
material conversion unchanged by this read-only compatibility check.

The shared library follows 2K Canvas's palette and transform conventions. The
workspace uses Canvas's actual ToolIcon geometry and copied button/menu templates,
including tool dimensions, hover/focus/disabled states, and tooltip timing. These
copies live inside Court Creator; the Texture Studio checkout is unchanged.

`Setup Court Creator.bat` delegates to `tools/setup_court_creator.py`. It reuses
the same bundled/venv executable order as the native Python service, validates
64-bit Python 3.12+, package constraints with the installed packaging parser,
Pillow/Shapely binary imports, x64 .NET 8 Desktop Runtime, and published file
presence/runtime configuration. An SDK is needed only when building missing or
incomplete desktop files from available sources. Populated incomplete runtime
folders, external runtime links/prefixes, invalid/direct-URL requirements and
changes while the app is running are rejected without creating a replacement
environment. Healthy packages/builds are not reinstalled/rebuilt. Repairs use
the selected app-owned interpreter; creation is allowed only in an absent/empty
runtime folder, and subprocesses have bounded timeouts. Failed installs do not
fall back to system-wide package installation or delete the existing runtime.
Pip itself may have modified packages before reporting a failed install; setup
does not claim package-level rollback. `--check-only --json` performs diagnostics
without installing/building/launching and labels its scope runtime/build-only.
The normal desktop launcher does not run these probes, so startup gains no extra
preflight delay. Compatible updates carry the setup/build wrappers and helper,
while the app-owned Python runtime remains outside the update allowlist.
Requirement inspection reads at most 64 KiB and requires active Pillow/Shapely
entries. Before rebuilding, linked desktop output and source directories that
resolve outside the project are rejected. These checks guard ordinary damaged
or relocated installations; they are not a sandbox for untrusted build sources.
`tests/check_setup.py` audits both the real batch diagnostics and healthy setup
path against SHA-256 hashes and modification times of the runtime/desktop files.

Colors & Lines and Logos are prominent icon-and-label tabs directly below the
selected hardwood card. Colors & Lines opens by default and after New. Per-layer
rows expose a toggle, swatch, editable hex, and direct Team Colors action together.
Team Colors remains available inside each swatch and pinned color picker;
those actions now use the actual central Canvas WPF `ColorPickerDialog`, not a
separate Court Creator RGB slider implementation. Shared spectrum/hue, RGB/HSB,
hex validation and swatches are retained; the host adds Team Colors and starts
with the hex field selected. The dialog uses isolated host-matched theme resources
and preserves RGB-only court colors. Central package updates must pass both
artwork, shared-color and shared-control integration gates before replacing the desktop build.
Duplicate header buttons and the short layer search
are removed. Switching tabs preserves the palette target. Save and Export stay in
the upper right. Fine dividers separate the global document chrome. The entire
selected hardwood card opens the catalog with All selected and a fresh search,
while retaining the current court's selection. Catalog selection/cancel preserves
the active Colors/Logos inspector. The card uses one accessible button with hover,
pressed and keyboard focus feedback; there is no separate hardwood sidebar.

Color/visibility changes retain the existing layer row, toggle, hex input and
accordion. Only the affected row's swatch/visibility changes; switching selection
restores each other row's original alternating background. Color controls are
collapsed for hidden layers rather than recreated on each toggle. Actual setting
changes take one undo snapshot and update the preview immediately; unchanged
settings skip snapshots, redraws and field scheduling, retaining redo history.
Hex readouts coalesce on `CompositionTarget.Rendering`, matching the logo-field
scheduler, so rapid changes do not force repeated pending court renders through
attached text-box updates. A pending readout flushes before blur validation; newer
typing cancels that field's pending update. Detached row events cannot change a
restored document. Restore/theme rebuilds and close cancel the rendering hook.
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --layer-edits outputs/layer-edit-check`
checks these behaviors off-screen, including compact renders and stale events.
Physical keyboard focus and real mouse/frame latency remain separate native QA.

Native color dialogs capture their exact layer, document version, selection, and
starting color. Inline dialogs also capture the originating row's ownership.
Accepting after New/Open/Undo, a hidden layer, changed selection/color, retired row,
busy operation, or close cannot change the replacement/current document. The same
checks run after gesture/opacity cleanup, since its callbacks can change ownership.
Only one top-level layer picker can open per workspace. Cancel, invalid, and
unchanged results retain history and any live logo drag; accepted changes retain
the stable row and make one color undo entry. The pinned picker is disabled when
its layer is hidden or the document cannot be edited. Double-click toggling is
limited to the layer name and left mouse button, not hex or palette button text.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/color-picker-check --color-pickers`
checks all 23 row targets, swatch/direct-team/pinned paths, undo/redo (including a
retained redo after no-op acceptance), real New/Open/Undo transitions, blocked/stale
results, recursive picker rejection, post-cleanup ownership, canceled/no-op live
drags, double-click boundaries, failure/retry, and 1000/1440px rendered layouts.
Dialog results and routed click counts are injected; these off-screen checks do
not prove physical focus, native modal ownership, or real pointer latency.

The 2026-10-05 off-screen benchmark recorded a 100-edit average of 8.50 ms before
this change and 0.81 ms after, including a final synthetic coalesced field frame.
These are programmatic batch measurements, not single-event display latency or
an Electron comparison. Native initialization stayed around 0.73 seconds in the
corresponding runs, excluding visible first frame, recovery and cleared OS caches.
The benchmark now reports initial samples, median/max edit time, final field-flush
work and its scope separately; local JSON reports live in `outputs/`.

The hardwood catalog binds lightweight floor rows to a recycling virtualized
list. Construction, filtering and sorting do not read image files. Only realized
rows request thumbnails, with two concurrent background decoders per catalog.
Recycling, filter changes and close cancel queued/stale requests; late results
cannot fill another court's row. Unloaded images release their pixel references,
while the existing bounded bitmap cache can reuse decoded content. Search is
debounced for 150 ms and matches court names, categories and IDs. Favorites update
in place outside the Favorites filter, without rebuilding the list or reloading
previews. The selection action stays outside the scrolling list, with an explicit
empty-results state. Unreadable or unsupported previews show a placeholder and
tooltip without disabling court selection; reopening retries repaired files.
The selected court is scrolled into view when the dialog opens, and the catalog
uses the same work-area fitting as the main window and other native dialogs.

The color picker also uses shared fitting, with a scrolling body and fixed Apply /
Team Colors actions for short client areas. Work-area placement uses physical HWND
coordinates instead of dividing virtual-desktop origins by a monitor's DPI.
DPI/display/work-area notifications coalesce a normal-window refit; nominal minimum
dimensions are restored when enough space is available. Maximized sizing respects
the taskbar and DPI-scaled minimum track sizes. Missing/invalid monitor information
leaves native message fields/default handling unchanged. Pending refits and hooks
are removed on close, and repeated attachment is ignored.

`src/NBA2KCourtCreator/app.manifest` explicitly requests PerMonitorV2 with PerMonitor
fallback, retaining asInvoker/no-uiAccess execution. The smoke harness shares that
manifest. `tests/check_release.py` reads the actual published executable's embedded
resource as data on Windows, without starting it, and rejects missing DPI settings
or changed privilege settings. This follows Microsoft's manifest guidance:
https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --window-bounds outputs/window-bounds-check`
checks automatic dimensions, four monitor/taskbar layouts with negative origins,
four DPI scales, unchanged native fields on lookup failure, compact color-picker
scrolling/fixed actions and raster output. These synthetic/off-screen tests do not
verify physical monitor transitions, modal focus or OS DPI changes.

Hardwood preparation uses one background decoder per workspace, including project
restoration. A new selection cancels the preceding request before queuing its work.
Canceled requests waiting for the decoder perform no image reads; an in-progress
synchronous read cannot be interrupted, but cancellation stops subsequent thumbnail
reads and rejects its result. New/Open/Undo, Save and accepted Close invalidate
pending floor selections. A stale failure does not replace the current court or
surface an obsolete error; a current failure remains reportable and retryable.
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --floor-requests outputs/floor-requests-check`
holds a decoder during 101 requests and verifies one active decoder, two full reads,
one thumbnail read, latest selection, failures and document/save/close boundaries.
These deterministic off-screen checks are not a cold-start or disk-speed benchmark.

Native stock names omit the material's Wood label and remove arena numbers only
for standard NBA IDs 000-031. Duplicate names within a category receive the
material's numeric variant (including 1-4); single courts remain unnumbered.
Repeated/missing/invalid material numbers use stable ID ordering and available
suffixes without colliding with literal existing names. Custom names retain their
wording. IDs, source paths, source metadata and library files are not renamed or
rewritten. Project snapshots carry the visible label; old stock labels can adopt
the matching library variant by ID without replacing intentionally different names.
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --floor-names outputs/floor-names-check`
checks naming permutations/collisions and actual stock selection, portable
save/open/undo/redo, legacy names and unchanged source files without showing UI.

`dotnet run --project tools/CourtStudio.Smoke -c Release -- --catalog outputs/catalog-check`
exercises 5,000 synthetic courts, held decoding/dispatcher responsiveness, the
two-worker bound, far-row search, recycled/closed result rejection, bad-image
fallback/retry, favorites, filters, debounce, compact/desktop renders and full
scrolling without opening a window. Recorded constructor timings and realized
row/decode counts are synthetic, not a cold-disk or visible-modal benchmark.

When a floor's full texture and selected thumbnail resolve to the same Windows
path, the native default loader opens it once and computes one complete SHA-256
revision, then retrieves both decoded widths from that still-open read-shared
stream. Both images carry the same source revision. Cached pairs still hash the
complete source on every selection, detecting same-size/same-timestamp changes;
no timestamp-only shortcut replaces export/source protection. Independent preview
files and injected per-image loaders retain their existing behavior. Decoder
reads can still revisit the stream on a cache miss: one hash pass is not a claim
that total filesystem traffic or selection latency is halved.

Single and paired loads share content-hash/width cache keys, the existing 128 MiB
conservative decoded-image budget and 80-entry bound, file/dimension limits and
frozen images. Paired cancellation is checked before opening and between hashing
and each decode; success, cancellation and decode failures dispose the source
handle. A later valid source can retry without a failed pending cache entry.
Internal counters track successful source opens and completed hash-byte passes.
The off-screen benchmark reports those counters separately for initialization,
first and cached selections; they exclude WIC decoder reads and Python I/O and
do not establish physical-disk traffic, cold startup or visible frame latency.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/paired-image-check --paired-images`
reproduces the former duplicate hash pass, verifies actual native selection,
identical-width/cache sharing, same-size/time source replacement and matching
preview/thumbnail revisions, normalized case/path aliases, twelve concurrent
pairs, cancellation/width rejection before reads, exclusive handle reopening,
malformed-source retry and cache bounds. No native windows are opened.

The global and document chrome rows are both 36 DIP, with #F8F9FB/#EFF1F4 light
backgrounds and #D9DDE3 dividers. Canvas Fit/zoom/Hand/Undo/Redo controls use its
exact icon geometry, dimensions, menu, and actual-pixel zoom percentages. The
second-row navigation/history group centers over the actual inspector width,
with a reserved inset maintained when its panels are hidden. Full
Court/Left Half/Right Half remain in the zoom menu. The maximize glyph, tooltip
and accessible name follow the actual WindowState, including system changes.
The moon path scales as a complete stroked glyph in a Viewbox; it has no fixed
19-DIP path clip. The selected hardwood preview uses aspect-preserving contain. The frameless window respects
the monitor work area when maximized; initial dimensions fit the work area at
the current DPI. Pinned color/appearance controls have aligned 32-pixel hit areas,
a 2-pixel gap and an 8-pixel bottom margin above the status bar.
Light/dark appearance continues to use the existing remembered preference.

Click the project tab to rename inline. Enter applies a trimmed name of 1–120
characters; Escape or leaving the field cancels. Display names persist as
`projectName` metadata, participate in undo, and do not rename/move project files.
An unsaved project uses its display name as the suggested Save As filename.

Team Colors initially shows collapsed league/category caret headings, then team
headings in NBA, WNBA, EuroLeague, D1 order. Existing D1 palette data remains intact.
Swatches are built only for expanded/visible rows. Search reveals
matching teams/colors across collapsed groups; clearing it restores expansion
state. The list recycles containers and scrolls in pixels. Wheel deltas are
proportional, immediately bounded, and accumulated per frame without animation.
Existing NBA/NCAA entries are preserved. WNBA and EuroLeague court presets retain
per-team official source provenance in `data/palette_sources`; artwork-derived
values and curated roles are distinguished from documented HEX specifications,
and a neutral fallback is explicitly labeled. Repeated hex swatches are omitted.

## Focused Tools

The Logos section uses alpha-aware selection for unselected artwork; the entire
rotated bounding box of the selected logo can be dragged, with handles taking
priority. Movement has a drag threshold. Resizing retains the opposite corner,
is proportional by default, and Shift temporarily inverts the visible aspect
lock. Mode changes rebase continuously without a jump. Rotation, keyboard nudging,
visibility, opacity, inline layer naming, ordering, mirror-position copies, and
stock anchors remain available. The list takes only the space its thumbnails need,
properties are grouped compactly, and there is one outer inspector scroll viewer.
X/Y/Width/Height readouts update at most once per composition frame during
movement/resizing; commit and cancel immediately flush the final/restored values.
The existing textboxes stay in place and a focused typed edit is left intact.
No Python render, project snapshot or undo entry runs per pointer move. Transform
notifications are reused, and resize captures the current state only for a
modifier-mode rebase. Layer visibility uses the exact Canvas 26x30 eye template
left of each thumbnail; it does not select/rename the layer. Guides and snapping
are disabled and their controls are removed.
Displayed decimals never overwrite untouched precise values. Import and copying
respect the four-slot export capacity; arrange/mirror commands are in More.
View controls provide fit, wheel zoom, panning, and left/right half views. Every
completed transform is undoable. Losing mouse capture cancels an unfinished edit.
Changing selection, removing artwork, disabling editing/the canvas, hiding the
active artwork, zero opacity and unloading also restore the original pose and end
its preview once, without an undo entry. View changes (zoom, fit, document/half
view and canvas size) cancel unfinished artwork transforms before the new mapping
can move or commit them. Update/commit checks cover WPF's deferred size-change
notification. Pointer-centered zoom is retained; unchanged view assignments and
unchanged/rejected zoom factors do not interrupt a gesture. Cancellation callbacks
cannot start a replacement gesture before cancellation finishes.

Each move/resize write verifies the selected layer and exact gesture identity
after synchronous property callbacks, so the remainder of an old update cannot
modify a newly selected layer or a value-equal replacement gesture. Commit events
capture their final pose before preview-ended observers run. Delete, flip,
center, copy/mirror and reorder cancel any pending drag before their undo snapshot;
copies are built from committed coordinates, not transient preview coordinates.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/canvas-gesture-check --canvas-gestures`
tests these boundaries, reentrant callbacks, pointer-centered zoom and exact
command undo/redo off-screen. Physical mouse capture, keyboard focus, mixed-DPI
resizing and trackpad behavior still require native QA; these checks do not open
any app windows or establish real input/frame latency.

Arrow-key nudges use that same transform lifecycle: cancel any unfinished drag,
capture the committed pose, apply one document-pixel step (ten with Shift), then
commit one undo action. Synchronous X/Y selection callbacks cancel and restore
the nudge rather than applying another layer's pose or recording partial history.
Editing is rechecked after cancellation. Hidden/zero-opacity artwork, unavailable
images, disabled editing/canvas and layers outside the document cannot receive
canvas nudges or Delete requests, nor start a drag through the selected box.

The shared document-change guard now includes workspace readiness and an open
hardwood catalog. It checks again after cancelling a pending gesture, since
cancellation observers can change readiness. Delete, flip, center, duplicate,
mirror and reorder check document membership before acting; rejected commands
leave selection/history/readiness intact. Copy selects only an actually added
layer, and reorder cannot use an absent layer's negative index. Reordering past
the front/back limit remains a no-op and does not interrupt a pending drag. Rejected project
renames return false rather than reporting a successful edit.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/keyboard-command-check --keyboard-commands`
exercises real routed key handlers with a synthetic, non-native presentation
source, exact native nudge undo/redo, eight explicitly injected read-only states,
foreign-layer rejection and cancellation-time readiness changes. Those injected
states isolate the command guard; broader off-screen load/save/recovery checks
exercise actual background operations. Physical keyboard focus, auto-repeat and
modifier-key delivery still require native QA.

Logo numeric fields, aspect-lock and alignment buttons carry the inspector's
build revision as well as their selected-layer ownership. Selection changes,
same-logo rebuilds, theme changes, New and undo retire prior controls. Their late
events cannot cancel a newer drag or write to the old layer. Ownership is checked
again inside the document change after cancellation callbacks run. Inline name
inputs must still belong to the current logo list and their bound layer; detached
inputs and cancellation-time rebinding cannot rename a retired owner. Numeric,
name, paint and opacity edits share the document-readiness guard.

Save and Save As commit the currently focused, still-owned numeric, visible hex,
logo-name or active project-name field before taking the portable snapshot or
opening a Save As dialog. Invalid drafts reject saving without resetting their
text or replacing the last saved file. Retired inspector controls are ignored;
busy, uninitialized and closing documents reject before consuming an input.
Typed X/Y values must be finite and within the persisted +/-1,000,000 coordinate
range. New inline logo names reject control characters and retain the existing
120-character limit. Unchanged rounded numeric readouts retain original precision.
Active opacity and pending field edits keep separate, correctly ordered undo
entries. A no-op focused save does not create history or clear redo.

A successful field commit is an ordinary document edit even if subsequent file
publication fails: the edit remains undoable and available for retry, while the
last good project is preserved and controls become available again. Save As is
available from File and Ctrl+Shift+S; Ctrl+S saves, Ctrl+Z undoes, and Ctrl+Y or
Ctrl+Shift+Z redoes outside text inputs. Alt-modified combinations do not invoke
these commands. Text inputs retain their own undo/redo handling.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/focused-save-check --focused-save`
checks exact sync/async field saves, invalid draft/model/file preservation, eight
injected readiness states, retired controls, opacity/field undo-redo ordering,
no-op redo preservation and real Windows locked-file failure/retry/backups for
each field type. Focus selection is injected into the actual field-commit path;
shortcut policies are checked directly. No native windows or dialogs are opened,
so physical focus changes, key delivery and the Save As dialog still need native QA.

PNG/IFF export and Canvas exchange use the same focused-field commit rules before
capturing their immutable project. An unavailable workspace or overlapping export
rejects before canceling an active gesture or creating an exchange directory.
Readiness is rechecked after field application, gesture cancellation and opacity
completion because callbacks can change the document's availability. Pending
opacity and field edits remain separate undo actions; no-op outputs retain redo.
Export dialogs and Canvas executable selection also validate the focused draft
before opening. Edits after an export begins still affect the live court, not the
already captured export or its companion Canvas project.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/focused-export-check --focused-exports`
reproduces missing numeric/hex/name drafts across all three output paths. A
snapshot-only worker writes explicitly named JSON markers for request assertions;
it is not evidence of PNG or IFF rendering. Those checks cover invalid drafts,
nine blocked states, cancellation-time changes/retry, exact ordered undo/redo and
Canvas snapshot consistency. Separate actual-backend checks export an 8192x4096
PNG at the typed logo position, inspect exact focused-apron-color PNG pixels, and
export/reimport a real BC7 IFF to check a second focused hex within compression
tolerance. No app, dialog or external Canvas window is opened. Native focus and
NBA 2K27 in-game loading remain unverified.

Normal close commits the current editable numeric, hex, logo-name or project-name
draft before checking unsaved changes or taking the final recovery snapshot.
Invalid focused input cancels close, retains its text/model and leaves the last
good recovery untouched. A save, export, synchronization or catalog operation
rejects close before canceling a live drag; the status names the active operation.
Readiness is rechecked after cancellation callbacks. A completed close does not
repeat the field edit or gesture cancellation when the final event is dispatched.

Pending logo imports may coexist with an otherwise editable close: current fields
are committed, then pending artwork is canceled by the existing document-operation
invalidation. Close does not wait for late import preparation. Closing during a
project restore retains the existing cancellation/final-recovery behavior rather
than attempting to apply a disabled field to the incoming project. Failed final
recovery leaves the committed field edit undoable, preserves prior recovery files,
re-enables editing and allows a successful retry.

Project-name blur now commits a valid name like the logo-name fields; invalid
names remain editable, and Enter/Escape retain apply/cancel semantics. Busy name
events cannot apply a draft. Only a successfully prepared replacement retires the
old project's rename UI, so a failed load keeps the draft while successful New,
Open or undo cannot later adopt an old name through a blur event.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/focused-close-check --focused-close`
checks all eight focused field types against actual temporary recovery files,
invalid draft/file/timestamp preservation, four injected busy states, callback
readiness, held failing final writes with undo/redo/retry, pending artwork
cancellation and routed name blur/replacement. Windows remain invisible; injected
focus and routed events do not establish physical focus transitions, native
confirmation dialogs or OS shutdown behavior.

Opacity previews retain one undo baseline, reject obsolete slider value events,
and recheck selection after cancelling a pending transform. Rejected values restore
the current selected layer's readout without changing document/history. Copy,
delete, flip, reorder, typed edits, project rename and paint changes finish an
active opacity edit before taking their own snapshot. Undo also finishes it before
choosing its target, so a live opacity edit with otherwise empty history is undoable.
No-op paint settings do not end the preview or generate history. Escape restores
the opacity baseline without an undo entry and respects read-only states.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/inspector-event-check --inspector-events`
checks 42 retired-control cases, eight injected guarded states, cancellation-time
owner changes, exact typed/name/opacity undo-redo and opacity followed by each of
those seven document commands. These are invisible routed-event/model checks;
they do not prove physical slider capture, focus transitions or general atomicity
against arbitrary external property observers.

The shared canvas now announces a cancelable transform start before capturing its
original pose, for move, anchored resize, rotation and keyboard nudge. The native
workspace checks readiness and finishes live opacity at this boundary, preventing
opacity and geometry undo entries from being ordered backwards. A canceled drag
retains the preceding opacity edit but never adds a transform entry. Start callbacks
that veto, change selection, remove/disable artwork, cancel, attempt a nested edit
or throw cannot leave a stale transform baseline; a subsequent gesture can retry.
This notification runs once per accepted start, not once per pointer update.

Typed locked width/height, anchored drag resizing and centered resize use one
shared proportional scale bound for the 1-32768 pixel dimension range. Either
dimension reaching a limit stops both together. Unlocked dimensions remain
independent; a legal typed dimension retains its exact parsed value. Typed edits
retain position/rotation, anchored drags retain the opposite rotated corner, and
centered resizing retains the center. Invalid original dimensions and NaN scale
are rejected by the shared helper; extreme requested scales saturate proportionally.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/edit-boundary-check --edit-boundaries`
checks actual native opacity/transform undo-redo, cancellation, eight injected
window guards, start-callback transitions/retry, 216 typed dimension cases and
720 centered/rotated-corner geometry cases. It remains off-screen and does not
establish physical mouse/keyboard capture or in-game behavior.

Pointer gestures track their initiating button: artwork transforms finish on left
release, while middle-button pan and left-button Hand/Space pan finish on their
own release. Unrelated releases do not commit or end the gesture. Ignored right
and auxiliary-button presses do not reset the artwork drag's starting point.
Commit/cancel clear the button owner before releasing capture, retaining the
existing single-end/single-commit lifecycle.

Non-finite pointer coordinates and document-coordinate overflow are rejected
before starting/replacing a gesture, changing its threshold state, hit testing,
sampling or applying an update. Derived move/resize/pan coordinates are checked
before they enter the model or view. Zoom prepares a finite pointer-centered
mapping before cancellation and rechecks it after cancellation callbacks, rather
than temporarily publishing a bad zoom/pan pair. Document and viewport assignments
must have finite positive extents and a finite derived mapping; empty, zero,
non-finite and overflowing mappings fail before canceling a valid current drag.
Valid fractional viewports are retained. Mapping checks are repeated after
cancellation because a callback can change zoom and invalidate an earlier check.
These are numeric/mapping guards, not a new maximum document-pixel allocation
budget or a guarantee of rendering every enormous finite coordinate efficiently.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- --pointer-safety`
reproduces unrelated-button commits and drag-origin changes, checks all three
artwork transform modes, both pan owners, invalid pointer inputs before/after
the drag threshold, malformed views, cancellation-time mapping revalidation and
valid fractional/clamped zoom recovery. Events use the real routed handlers on
invisible controls; physical button delivery, focus and mouse capture still need
native QA. No app windows are opened by these checks.

The left rail provides Move, Resize/Rotate (enabled for a selected logo), the
artwork eyedropper, Hand, and Zoom. The eyedropper applies the visible artwork's
color to the selected paint/line layer. Flip, Center, mirror-position copies,
ordering, and Delete remain in the logo inspector. Existing logo limits and
game-UV coordinates are preserved.

## Logo Import And Cleanup

Import Logo in the Logos inspector opens a local importer with a checkerboard preview. Sample a color and use
Remove edge background for matching regions connected to an image edge. Magic
Wand removes only the clicked four-connected region by default, so enclosed
letter holes can be removed without deleting disconnected logo elements. An
explicit all-matching-colors option affects matching pixels throughout the image.
Tolerance ranges from 0 (exact RGB) to 255 (all opaque colors).

Cleanup preserves existing transparency and supports Undo and undoable Reset.
Cancel discards importer edits. Placement copies a cleaned PNG into the project's
managed logo assets; the source image is never overwritten. No cloud service or
AI segmentation is used. Cleanup supports images up to 24 million pixels and
keeps up to eight undo states within a 128 MiB history budget. After placement,
move, resize, stretch with scale unlocked, rotate, and flip using the court tools.

The native importer is bound to the document that opened it. New/Open/Undo, close,
busy operations, and cancellation callbacks cannot transfer an accepted result
into a replacement court. Construction/show/preparation failures release the
workspace's importer flag, and nested opens are rejected. The fourth logo slot
still selects its new logo and Move tool; an intervening full-capacity document
does not receive another layer. The final asynchronous placement also rechecks
document ownership after gesture cleanup, before adding the layer or recording
its undo action.

Image choices share one background decoder. New choices cancel queued work;
late pixels or errors cannot replace a newer source or update a closed importer.
Choosing another image retains the previous preview until successful preparation,
and failed loads restore controls without discarding it. Cleanup work captures its
own image instead of dereferencing a possibly replaced image in a worker.
Placement and Undo/Reset are unavailable during loading, cleanup, or preparation.

Untouched placement preserves original file bytes, but rehashes the complete
bounded source and rejects changes since the preview, including identical-size/
timestamp replacements. Cleaned placement freezes the preview pixels and writes
a new private PNG with create-new semantics, a handle-captured file identity and
flush-to-disk. Both paths retain a SHA-256 pin through asynchronous asset adoption;
changes after the dialog is accepted cannot silently become a different logo.
Edited pixels remain an independent snapshot even if the original file changes.
Cancel/close, rejected handoffs, stale preparation, and partial writes clean up
only owned temporary files. Completed PNG cleanup rechecks identity and content
on every bounded retry; changed/replaced/shared/linked files are retained with a
trace warning naming the path. Permanent locks can leave a reported file behind.
These checks do not eliminate an uncooperative change after the final check and
before deletion, and untouched originals are never candidates for deletion.

`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/logo-importer-check --logo-importers`
reproduces the former same-size/time source mismatch, checks borrowed-source
preservation, PNG alpha/revisions, edited/replaced temporary files, real transient
locks, partial-write failure/retry, close/reload during a held write, a 101-choice
burst (one decoder/two reads), stale/current failure recovery, New/Open/Undo and
constructor/gesture/handoff ownership, eight blocked states, nested/full/fourth
slots, exact undo/redo, and closed-workspace rejection. Dialog outcomes, held
decoders/writers, and routed gestures are injected; no native windows are opened.
Physical file-dialog focus, modal ownership, and pointer latency remain separate
native QA rather than claims established by these checks.

Managed logo adoption now streams external files into a private create-new asset,
rejecting inputs above the shared 512 MiB image-file limit before creating the
destination directory. The source stays read-shared during copying. Creation
captures the destination handle's file identity; incremental SHA-256 covers the
bytes actually written. Cancellation is checked between 64 KiB chunks, before
decoding, and after decoding. A synchronous filesystem read or WIC decode cannot
be interrupted mid-call. The decoded image must match the copied revision before
the asset can be adopted.

Canceled placement and failed project preparation clean up only an uncommitted
asset whose identity and complete content still match its receipt. Every bounded
lock retry rechecks both. Same-size/time edits, same-byte replacements, hard links
and permanently locked files are retained with a path-bearing trace warning;
committed and borrowed assets are never deleted. Disposal is idempotent, including
when the old filename has been reused. A partial write whose actual bytes cannot
be matched safely may leave a reported file behind. As with temporary PNG cleanup,
these checks cannot eliminate changes after the final check and before deletion.
Copy stream/incremental hashing is separate from the `StudioImages` source-read
and full-source-hash diagnostic counters.

`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/managed-logo-check --managed-logos`
checks owned/borrowed/committed cleanup, edits and replacements, shared files,
transient/permanent locks, retry-time edits, multi-chunk cancellation and failures,
decode-time cancellation/mutation, invalid/locked/missing/oversized sources, and
New/Undo/Close while actual prepared copies are pending. A later hardwood failure
also verifies prepared-asset cleanup without replacing the current project.
These checks use private fixtures and unshown windows, not native modal/input or
in-game loading verification.

Paints and lines remain independent stock-geometry layers with immediate native
preview updates. Equal-color adjacent paint regions rasterize as one geometry
fill, removing the faint shared-edge preview seam when Inner Key Lines is off.
Line drawings retain their separate visibility and ordering. PNG/IFF export
continues to use the existing full-resolution compositor. Export uses the same version-2 `game-uv` project contract as the
Electron build. Preview image caching never replaces full-resolution source pixels.

The Import section keeps the older-court texture extraction, boundary alignment,
and conversion backend. Normal and converted IFF exports use the existing
single-texture NBA 2K27 export base. In-game validation is still required.

Court inspection and its initial preview are prepared before replacing the
last valid source/preview pair. Newer source or texture selections cancel older
requests; late results and obsolete errors do not replace the current selection.
Converted exports are enabled only when the preview matches the source, bounds,
and hardwood currently in the controls. Changing hardwood or undoing a document
edit invalidates alignment until it is refreshed. New/Open clears the conversion
context, and closing cancels pending requests. Each native preview uses a unique
temporary directory. The Python writer now returns a versioned receipt containing
the PNG's SHA-256, byte count and Windows volume/file identity, captured from the
original creation handle before atomic publication. Publication and cleanup
recheck that receipt, including after a sharing retry. The native decoder verifies
the receipt and its retained bitmap revision before adopting the frozen image.
These additional hashes apply to conversion previews, not ordinary court selection
or app startup. The older fallback ignores the additional response field.

Native preview folders are created exclusively beneath the resolved temporary
root. A directory-read handle denies delete sharing during the request; final
cleanup rechecks the original ordinary, non-reparse directory identity. It removes
only the unchanged, unshared receipted PNG, then removes the folder nonrecursively.
Unknown/nested files, same-byte replacement files, same-size/time edited bytes,
hard links, redirected directories and persistently locked paths are retained
with the exact path in a trace warning. Cleanup does not mask decoding/request/
cancellation failures. A receipt already returned by the worker is accepted even
when decoding has just been canceled, allowing its unchanged PNG to be removed.
Cancellation before receiving a receipt can leave a reported unclaimed output or
worker stage; neither is swept or adopted. There is no startup janitor, and these
checks do not eliminate a non-cooperating race between a final check and deletion
or between directory creation and obtaining its handle.

`tests/test_preview_receipt.py` checks real small PNG publication, worker receipts,
handle hashes/budgets, same-size/time and retry-time edits, same-byte replacements,
shared aliases and failed/interrupt publication without a false success receipt.
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/preview-files-check --preview-files`
checks actual native cancellation/decode-failure cleanup, retained foreign files,
real transient/permanent Windows locks, retry-time edits, duplicate disposal,
create-new collisions and directory/junction replacement. It also reproduces the
metadata-only-handle rename gap and verifies that explicit directory-read access
blocks the ordinary replacement attempt. Full smoke runs include actual Python
preview receipts and conversion round trips. No native windows are opened.
The cross-language identity uses the same
[GetFileInformationByHandle fields](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)
as existing native staging checks; exclusive directory creation uses
[CreateDirectoryW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectoryw).
The backend still supports the older shared preview path for the Electron fallback.

Conversion targets the stock geometry's inside court corners, shared with the
main editor; the older normalized boundary is only a fallback when geometry is
unavailable. Image-based detection looks for long opposing edges before falling
back to nonblack content bounds. This handles colored aprons and interrupted
baselines where the paint matches the apron, but legacy bounds remain estimates
that can be corrected in the import controls.

New game-UV IFF exports record the selected DDS, its dimensions, and integer court
bounds in a versioned ZIP comment. Reimport validates that record against the
selected texture and avoids decoding pixels just to guess boundaries. Invalid
or mismatched records fall back to image detection. Reimporting an unchanged
full-court layout preserves its apron and artwork positions rather than nesting
the full texture inside the playing surface. Converted IFF exports disable the
secondary logo sampler just like normal native exports.

## Suite Exchange

Send Texture to 2K Canvas writes `court.png` and `court.court.json` into a unique
folder under `%LOCALAPPDATA%\2K Studio\Court Creator\exchange`. It opens Canvas
with `--open <texture path>`. The sibling Canvas app now accepts that entry point.
If it cannot be located, Court Creator asks for the Canvas executable.

This is a one-way flattened image handoff. There is no automatic return channel,
live synchronization, or cross-app undo history yet. Native raster brushes and
general photo/AI background segmentation are not included. The Electron fallback
retains its older editor tools.

The handoff PNG and project are captured from the same immutable document
snapshot. Edits made while the export runs remain in the open document but do not
change that exchange pair.

## Reliability And Cache Limits

Project files are limited to 16 MiB and validated before restoration, including
field types, colors, finite transforms, dimensions, and unique explicit logo IDs.
Floor and logo assets are prepared before replacing the current document. A
failed restore leaves the court intact, re-enables its controls, and does not
discard undo/redo history. Missing logo references remain recoverable metadata.
Manual Open now reserves the restore operation before reading any file bytes.
Reading, strict JSON decoding and validation run on background tasks, while the
dispatcher retains the preceding court until assets are prepared. Save, export,
edits, another Open and document replacement are blocked for the entire read /
prepare / commit interval, not only the asset-decode phase. An accepted close
request cancels the uncommitted load and flushes the preceding court and settings,
without waiting for a blocked background reader or decoder. Cancellation checks
after each asynchronous stage prevent late results from replacing that court.
Already-prepared, uncommitted logo copies are disposed before closing; a decoder
that finishes after cancellation also cleans up its newly owned file. Existing
managed artwork is not deleted. Blocking operating-system reads/decodes are not
forcibly interrupted, and their tasks may finish later. No overlapping restore is
allowed while the canceled operation remains pending in an open window. If the
final save fails, the window stays open; editing resumes when that canceled load
unwinds, and closing can be retried. Project saves still finish before closing
because their publication must not be abandoned midway.
Only a successful opened-project commit resets undo/redo and import context.
Failed JSON/UTF-8/depth/size checks, missing/locked files and failed asset loading
leave the court and its history intact and re-enable the workspace. In-memory
restore callers still get a synchronous private clone before asynchronous
validation, so later caller mutations cannot change the prepared document.
The focused off-screen harness is
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --project-open outputs/project-open-check`.
It reads a real multi-megabyte project, holds a controlled reader to prove
dispatcher responsiveness and mutation guards, verifies source hashes/timestamps,
exercises the failure paths and confirms recovery captures the opened document.
It also holds reads and logo preparation across close, tests late successes and
failures, checks immediate cleanup of previously prepared artwork, preserves
source bytes/history, and retries after a failed final flush. The current court,
not the incoming one, is retained in recovery when close cancels a load.
It is not a cold-disk or network-share latency benchmark.

Autosaves use atomic, disk-flushed writes with a last-valid `.bak` recovery copy;
corrupt recovery files do not replace that backup. Startup ignores malformed
preferences and can fall back to the default court if a recovery asset cannot be
decoded. Concurrent startup calls share one initialization; completed startup
does not append duplicate floors or layers. A failed initial load exposes a
compact Retry action in the status bar while protecting the unloaded workspace.
Retry also handles failure after floors and geometry have partially loaded.

Recovery snapshots are captured on the dispatcher, then a single background
writer handles validation, JSON serialization, backup checks, file-access retries
and disk flushes. At most one active and one latest pending snapshot are retained;
new edits replace the pending snapshot rather than starting overlapping writers.
Caller-owned JSON is cloned before it enters the queue. Failures are reported for
the latest revision and a later write can recover without restarting the app.
Startup recovery-file reading/validation also runs off the UI thread.

Preferences have a separate validated format containing favorites, recent courts
and theme, rather than passing through the court-project validator. Reads and
writes run off the dispatcher. The same snapshot queue implementation serves
recovery and preferences through independent writers; rapid settings changes
retain only one active write and the latest pending snapshot. Preference files
are limited to 1 MiB and eight JSON levels. Recent courts are normalized to 20
distinct IDs, and existing UTF-8/UTF-16 BOM files remain readable.
Writes use a unique disk-flushed staging file and atomic replacement with bounded
file-access retries. Damaged, oversized or unrecognized existing settings are
preserved, not silently reset. Startup uses defaults and reports the problem;
repair or rename the identified file before saving new settings. Linked paths
and hard-linked settings destinations are rejected. These checks protect against
accidental overwrites, not hostile filesystem races or all power-loss scenarios.

The focused off-screen harness is
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --preferences outputs/preferences-check`.
It injects temporary stores into the same startup/floor/theme/favorites/close
paths used in production, including restart, real file locks, malformed files,
linked paths, coalescing and failure/retry. Ordinary test mode without an injected
store still avoids real user settings. No app windows or real settings are used.

Normal window close cancels the immediate close, queues final recovery and settings,
invalidates pending document operations and waits asynchronously before closing.
Editing is disabled during this final flush, but the dispatcher stays responsive;
repeated close requests cannot bypass it or create duplicate writes. A failed
flush restores editing and preserves earlier files. The visible app asks before
closing without the latest recovery or settings, defaulting to keeping it open.
Forced process termination, explicit application shutdown and OS shutdown are
not guaranteed to wait for this flush. The recovery-only off-screen harness is
`dotnet run --project tools/CourtStudio.Smoke -c Release -- --recovery outputs/recovery-check`.
It covers 1,000-edit coalescing, mutation isolation, serialized writes, latest
revision/backup, real Windows locks, failure/retry, dispatcher responsiveness,
actual debounce and invisible-window close/retry. Physical modal interaction
still requires user QA. This follows WPF's
[dispatcher threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model)
and [cancellable window closing](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.closing).

Logo imports reserve one of the four slots before decoding. Import/copy controls
respect pending reservations; Save/Export stays disabled until pending artwork
is committed or discarded.
New/Open/Undo/Close invalidate pending work for the older document; late success
cannot add an orphan layer or selection, and late failures are ignored. Image
decoding and managed asset copies run off the UI thread. Uncommitted new copies
are removed, while successfully committed sources remain available for undo.
Independent paint edits made during a load retain separate history entries.

Stock base and geometry extraction share a private scratch folder and a rewritten
manifest whose archive paths point at the installed game. `mod.exe` runs with that
scratch folder as its working directory. Each distinct archive path is resolved
once during the manifest rewrite, not once per asset row. Extraction no longer moves, deletes, or
replaces a loose floor mod in the game's `mods` folder. The export base is staged
under a unique filename, inspected and cleaned, then atomically published while
the existing process/file locks serialize concurrent preparations. Failure keeps
the previous cache intact and removes the owned scratch/staging files during
normal exception unwinding. A forcibly terminated process may leave scratch files,
but installed mods no longer depend on its cleanup to be restored.

Recovery for older interrupted extraction journals remains supported. Malformed
records, unavailable game installations, and targets that resolve outside the
recorded game folder are rejected without discarding their backups.

The stock geometry cache is limited to 16 MiB and 250,000 polygon points. Its
version/source/size, required layer and guide identities, unique IDs, colors,
visibility, finite coordinates, UV matrix, hardwood bounds, surface envelope,
and projection coverage are validated before use. JSON `NaN`/`Infinity` values
are rejected even in metadata. An invalid/stale cache becomes a cache miss; a
failed regeneration leaves that file intact. Preparation is serialized within
the process and across separate instances, with a cache recheck under the lock.
New geometry is validated, written to a unique owned staging file, flushed to
disk, and atomically replaced. Serialization/flush failures clean up staging and
preserve the prior target. Readers see either the old cache or the committed one.

A real Windows two-process regression reproduced a cache-folder creation race:
non-strict `Path.resolve()` sometimes retained an equivalent `\\?\` prefix while
another process created `data/generated`, so a lexical safety check wrongly
reported linked folders. The stock-data lock now strictly resolves existing
ancestors and reconstructs only the missing suffix, then strictly checks the
complete parent again after `mkdir` and before opening its lock file. Actual
linked folders remain rejected. A deterministic regression reproduces the
observed prefix condition, and a real junction inserted during creation must
leave its other folder and sentinel untouched, with no lock file created there.
`tests/test_geometry_cache.py` also retains the real two-process single-builder
check. These checks address the demonstrated creation boundary; they do not
make pathname checks/opening atomic against non-cooperating folder substitutions.

Normal Save and Save As write a portable `.court.json` plus an adjacent
`<project-name>.assets` directory containing the selected hardwood, referenced
custom floors, and available logo sources. Asset paths in the saved file are
project-relative. Content hashes deduplicate identical sources and keep older
project backups' assets immutable; corrupted hash-named files are repaired from
the valid original on a later save. Copying and hashing run off the UI thread,
and the document is protected until the save finishes. The project JSON is
replaced atomically only after its asset writes finish. Failed asset reads leave
the prior saved/open project intact. Previously referenced assets are not
automatically deleted, since backups can still depend on them.

Portable artwork publication now uses `StudioPortableAssets`. Source hashing and
copying are bounded 64 KiB streams with the shared 512 MiB file budget. Source
size/pins are checked before storage creation; borrowed read-only hard-linked
sources remain supported. An exclusive creation handle captures each staging
file's identity. The SHA of successfully written bytes is compared to the source
snapshot, then the flushed copy is read back before publication. Partial-copy
cleanup uses that written-prefix receipt; an unrecorded partial filesystem write
can therefore leave a reported retained stage instead of deleting unknown bytes.

A directory-read handle prevents ordinary asset-folder renames during bundling.
Publication checks the owned ordinary/unshared staging file, full content hash,
source hash and initial destination identity/content on every bounded sharing
retry. Existing correct bundles are reused without rewriting them. A corrupted
ordinary bundle can still be repaired from the valid source, but a destination
created/edited/replaced/shared/redirected during preparation is not overwritten.
A concurrently published complete identical bundle is reused without replacing
it, including no-overwrite filename collisions. Shared or reparse-point destination
bundles are rejected; borrowed source aliases are not modified.

Cleanup targets only the exact unchanged owned stage. Same-byte replacement files,
same-size/time edited bytes, hard links, junctions and persistent locks are retained
with a path warning; the primary error remains intact. Every distinct bundled
asset's identity, byte count and full SHA is rechecked just before project JSON
publication and again after a sharing retry, including assets copied earlier in
the same save. Failed validation preserves the prior project/backup and supplied
snapshot. These guards do not create a multi-file/power-loss transaction or remove
all non-cooperating check-to-rename races. Fully published but unreferenced assets
can remain after a later project-write failure; there is no sweep, because another
project/save may already reference them. Folder creation and obtaining its guard
handle are also separate operations. These extra checks apply to portable saves,
not app startup or cached court selection.

`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- outputs/portable-assets-check --portable-assets`
first reproduced a wrong-hash publication and good-project overwrite from an
externally edited stage. It now checks real PNG save pins, 230,595-byte multi-chunk
copies, bounded/injected exact-size limits, failed written-prefix cleanup,
replacement/link/junction/foreign-target retention, dedup/corruption repair,
twelve concurrent copies, real Windows transient/permanent locks and per-retry
changes, directory/source handle guards, earlier-asset edits before JSON commit,
and actual asset receipts after a project sharing retry. Full smoke runs also
exercise moved-project Save As/reopen, backup restoration and real portable PNG
exports. No app windows or user projects are opened by these checks.

Move or share both the JSON file and its matching assets directory together.
Portable paths resolve beside the file actually opened, not its embedded old
machine path. A valid saved hardwood path takes precedence over a same-ID local
library entry, including absolute in-memory undo/redo snapshots captured after
opening a portable project. It must not silently switch back to the library on a
paint-only undo. A missing different saved path reports the local-library fallback;
the existing default-court fallback still applies when no matching floor is found.
Non-selected custom floors are restored to the catalog.
Available absolute paths remain authoritative for older projects; missing legacy
app-owned paths can still be relocated to this installation. Missing references
remain as metadata rather than being silently removed. A portable project does
not replace the app's full stock library or the game export base.

The floor-name harness also uses different-pixel artwork under an existing stock
ID. It verifies the bundled path/revision through save/open/undo/redo and samples
an actual full-resolution native PNG export, while preserving the original stock
texture and index. This catches source substitution even when IDs and labels match.

Python request deadlines include time spent waiting in the worker queue. Invalid
JSON, mismatched request IDs, malformed response envelopes, process exit, and an
active timeout discard the worker so the next request starts cleanly. A queued
timeout does not terminate someone else's active request. Cancellation and
window disposal terminate outstanding worker activity. Normal backend operation
errors do not unnecessarily restart it. Response reading/parsing is explicitly
scheduled away from the WPF synchronization context, including already-buffered
responses whose asynchronous reads complete synchronously.

Native worker responses use an 8,192-character read buffer and a maximum frame
length of 32 * 1,024 * 1,024 UTF-16 code units before the newline. This is a
response-size budget, not a limit on total process memory. UTF-8 decoding is
strict, without BOM-driven encoding switching; LF and CRLF JSON-lines framing
are supported, and EOF during an unterminated frame is rejected. Buffered bytes
from a following frame are retained rather than dropped. Oversize, malformed,
wrong-encoding and incomplete responses discard the worker; the next request
starts cleanly. The actual stock geometry/catalog load passes this transport.

Worker stderr is drained in fixed 1,024-character chunks even without newlines,
retaining only the final 4,000 characters for diagnostics. A stdout EOF waits at
most 250 milliseconds for remaining diagnostics, so a child that closes stdout
but keeps stderr open cannot delay protocol failure until the operation deadline.
Per-worker pumps are canceled on shutdown and cannot append logs to a replacement
worker. Fault-injection checks cover fragmented/exact-limit/buffered frames,
UTF-8 Unicode, UTF-16/invalid bytes, unfinished/oversized frames, excessive JSON
depth, newline-free log floods, diagnostic-tail retention and closed-stdout live
children, along with the existing request deadlines, disposal and retry checks.

Decoded preview images use a shared 128 MiB / 80-entry LRU cache, with one decode
for concurrent misses and eviction of least-recently-used entries. The byte
estimate conservatively accounts for two pixel buffers. This is a cache budget,
not a limit on total application memory: active artwork, undo, WPF, and Python
have separate allocations. Invalid or oversized source images are rejected
before full decoding. Full-resolution exports still read original source assets.

## Build And Check

```powershell
dotnet publish src/NBA2KCourtCreator/NBA2KCourtCreator.csproj -c Release --self-contained false -o desktop
dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release
runtime/python/python.exe -B -m unittest discover -s tests -p "test_*.py"
runtime/python/python.exe -B -m tests.check_game_uv_export
runtime/python/python.exe -B -m tests.check_stock_extraction
```

The native smoke harness defaults to entirely headless verification: it renders
15 off-screen layouts across five sections and three window sizes without
showing an app or dialog. It verifies native canvas pixels, colors/line visibility, hardwood
switching, undo/redo, logo transforms, locked resize/stretch, project round-trip,
repeated navigation/gesture cancellation, importer placement preparation, cleanup flood-fill boundaries and
coordinates, transparency, full-resolution PNG/IFF exports, and New defaults.
Fault-injection checks cover worker protocol corruption, exit, timeouts,
cancellation/disposal, temporary request cleanup, cache eviction/concurrency,
invalid projects, failed undo, preferences, autosave backup fallback, partial
startup failure and retry, moved projects with unavailable originals, portable
full-resolution Python rendering, deduplication/corruption repair, failed asset
saves, reserved logo capacity, delayed artwork after New/Open/Undo/Close, staged
asset cleanup, transactional conversion and latest-request selection, stale
alignment export guards, actual IFF preview decoding/temporary cleanup, and the
Canvas exchange snapshot during concurrent editing.

The stock extraction audit rebuilds a temporary base and temporary geometry from
the installed manifest, decodes the real court DDS, and compares the new geometry
with the application's existing stock geometry. It checks the manifest/extractor
and loose floor hashes, mod-folder metadata, source-archive metadata, and existing
cache hash before/after. Unit tests inject extraction failures, interrupts, retries,
invalid manifest entries, unsupported scenes, and simultaneous base preparation.
Geometry tests cover malformed/stale/oversized/non-finite cache data, missing and
duplicate geometry identities, point budgets, singular transforms, inconsistent
surface bounds/coverage, failed rebuild and retry, failed serialization/disk flush,
and two real Python processes sharing one preparation. The stock audit also
reports fresh/cached base and geometry timings and retains a CPU profile. These
are local preparation measurements, not visible app-launch or Electron comparisons.

On this development PC, the 2026-10-05 profiled audit measured fresh base
preparation at 203.550 seconds before archive-path memoization and 8.168 seconds
after; fresh geometry preparation fell from 207.444 to 6.317 seconds. Both runs
produced geometry identical to the existing cache and left installed inputs
unchanged. Baseline report/profile files are retained with the `baseline-` prefix
under `outputs/stock-extraction-audit`; these timings include profiling overhead
and are not universal startup guarantees.

Optional `--native-windows` polish checks also exercise native card hit targets/catalog select/cancel/reopen,
inline Enter/Escape naming and round-trips, native monitor maximize bounds, minimum
importer client areas and 100/125/150/200% DPI-scaled WPF renders. Large synthetic
palette checks cover virtualization, fast wheel bursts, reversals, bounds, search
and expansion restoration. DPI-scaled rendering does not change Windows display
settings; physical wheel/trackpad and every OS DPI configuration need manual QA.
Native keyboard focus and modal interaction are not proven by the headless run.

An optional `--benchmark <report.json>` mode measures fresh-process window
construction/initialization, first and cached floor selections, native color
edits, full-resolution PNG rendering, and harness memory. It does not clear OS
disk caches, load a recovery project, measure visible cold-start first-frame time,
or compare Electron. Python memory is excluded from the harness memory result.
Release archives contain the published native binaries and Python
code, not game archives, extracted courts, personal logos, or the Python runtime.

The desktop shortcut continues to target `Launch NBA 2K Court Creator.bat`.
Builds do not open the app. Development checkouts do not apply release updates.

Standalone updates now stream through a 64 KiB buffer with a 120-second total
deadline, a 1 MiB release-metadata limit and a 512 MiB archive limit. Download
URLs must belong to the configured GitHub release; redirects remain HTTPS on
GitHub asset hosts. ZIP inspection bounds the directory to 8 MiB before entry
allocation, counts actual directory records (not just the end-record count),
and caps 10,000 entries, 256 MiB per file and 1 GiB expanded data. Windows path
aliases, duplicate/case-colliding paths, file/directory collisions, symbolic or
special entries, encryption and non-store/deflate compression are rejected.
Selected entries are streamed and CRC-checked before pending publication.
Extraction retains each selected file's byte count and SHA-256 digest in a
bounded, atomically written private `.update-inventory.json`. Installation checks
the version, complete application-file list and every fingerprint before touching
the existing rollback folder or application files. It retains these verified
revisions through backup preparation and copying rather than adopting later edits.
Changed bytes are rejected even if size and timestamps remain unchanged. Old
native queues without an inventory must be downloaded again; the old app remains
usable. The private inventory stays in update staging, not the application root.

Downloads have their own OS-held exclusive lock; only short compatibility checks
and pending publication share the installation lock. Network transfer/extraction
cannot stall reopening the existing app. Publication rechecks the installed
version and compatibility after the download, so a concurrent installation cannot
cause a stale pending release. A leftover lock file does not keep the lock held
after process exit. New pending folders are prepared
separately and the previous pending folder survives validation/publication
failure. The rename gap is recoverable through `pending.previous`. A native
release needs all five core published files, the .NET 8 Desktop configuration,
matching runtime/project/exchange schemas and unchanged Python dependencies.
Stale pending versions cannot downgrade an installed copy. Runtime, courts,
logos, presets and other local-only folders remain outside the update allowlist.
Managed `data/team_palettes.json` and `data/palette_sources` are included so team
catalog fixes reach standalone installations. Personal presets, game locations
and update preferences are not packaged or replaced.

`tools/build_release.py` now builds a unique temporary ZIP, verifies its complete
CRC-checked contents against hashes captured during writing, and rechecks source
files before atomically replacing the final archive. Missing inputs, linked paths,
budget violations, changed source bytes/file lists and failed publication leave
the previous archive intact. Importing the module does not create an archive.
Both packaging and extraction count implicitly created parent directories in
their entry budgets. `tests/check_release.py` audits the actual published binaries
in a temporary standalone installation, including same-size/mtime corruption,
clean installation and personal-data preservation. It neither executes binaries
nor downloads/uploads a release, and records `outputs/release-integrity-audit.json`.

Before replacing application files, installation completes independent backups
and flushes an atomic `updates/apply-journal.json` with old/new SHA-256 revisions.
Every file copy uses unique same-directory staging, a revision check, flush and
atomic replacement. A prepared journal recovers the old installation on the
next launch, then retries the pending release; a committed journal completes
cleanup without rolling back the new installation. Corrupted backups, unrelated
target edits or failed recovery preserve the journal/backups and block launch
instead of overwriting them. Background staging never repairs an interrupted
installation while the app may be running. Git checkouts remain completely
excluded. Fault tests use temporary installations, including actual process
termination after the first installed file, cross-process locking, corrupt CRCs,
linked paths and blocked rollback/commit writes. This proves process-interruption
handling, not universal power-loss durability or protection against adversarial
filesystem changes between checks. Release publisher authenticity still relies
on the configured GitHub HTTPS source; files are not independently signed.
The launcher pre-parses its complete update/start tail before replacing its own
batch file. Sanitized tests use the actual launcher source with process checks,
application start and background start replaced by harmless fixture commands;
both direct `cmd /c` and batch `CALL` paths keep busy/recovery-error starts blocked.

Native startup pins a SHA-256 revision of the validated stock geometry. PNG/IFF
exports and conversion requests carry that revision; changed, missing or corrupt
geometry is rejected before rendering or preparing an export base. The project
and existing output remain untouched, with a reopen instruction. Each operation
uses one loaded geometry snapshot for drawing, alignment and IFF layout metadata,
so an on-disk cache change during encoding cannot mix layouts. Saved projects
remain portable and are interpreted using the current workspace geometry when
opened; the revision is a live-session check, not a persisted version lock.
Older clients without a revision retain their existing compatibility behavior.

PNG and IFF publication uses unique same-directory staging files, flushes the
completed file before replacement, and only replaces the destination after
encoding/verification succeeds. Concurrent operations cannot share or remove
each other's staging files. Failed writes leave the previous export intact.
Destination checks protect selected hardwood, all referenced logos (including
hidden logos), custom floors, the open project, import source/background, cached
geometry/export base and the DDS converter. Path aliases and existing hard links
are checked again before publication. These are accidental-overwrite guards,
not a guarantee against adversarial filesystem changes between system calls.
Python export publication also retries Win32 access/sharing/lock errors 5/32/33
at most four times, with 25/50/100 ms waits and a source-collision check before
each replacement. A missing staging file, source alias, disk error or other
permanent/non-access error is not retried. Persistent access denial retains the
last original exception and previous output without changing permissions.
Real Windows read-sharing locks and 100 two-writer PNG rounds are covered.
This was added after a concurrent PNG test produced WinError 5; the original
external trigger is unknown, so this is bounded resilience, not a root-cause
claim or a promise that every denied publication can succeed.

IFF packaging now pins the flattened PNG and its private encoder input, rather
than checking image dimensions and copying a potentially changed path later.
The common bounded export revision reader retains identity, size, timestamp and
complete SHA-256 bytes for both the base archive and flattened source. PNG copying
uses create-new semantics, bounded reads, flush-to-disk and complete copy-hash
verification. Inputs above 512 MiB and growth beyond that budget are rejected
before oversized data reaches the private copy. Borrowed read-only hard links
remain supported and are never modified.

Only an actual PNG with the required 8192 x 4096 dimensions reaches texconv. PNG
structural/CRC verification runs against the copied bytes without allocating an
extra full-resolution bitmap; texconv still performs the pixel decoding. Source
and private-copy identity/content are rechecked after preparation, after encoding,
and before each atomic publication attempt. Changed/replaced source or private
pixels, shared private files, damaged PNGs and retry-time changes retain the last
good IFF. The retained source handle also prevents some replacements under
Windows sharing rules. These checks do not prevent an uncooperative mutation
after the final validation and before the next filesystem operation.

`python -B -m unittest tests.test_flattened_png tests.test_iff_export_base tests.test_dds_export_validation tests.test_export_safety`
checks valid private encoder bytes, format/dimensions/CRC/truncation, exact and
oversized byte budgets, copy-time growth, borrowed hard links, same-size/time
source edits, same-byte replacements, private-input edits/replacement/sharing,
archive-copy and publication-retry mutations, and unchanged sources/last-good
output. Small actual PNGs use a patched output-size constant and an injected
encoder; full-resolution native export/reimport checks provide the separate
8192 x 4096 PNG/BC7 round-trip evidence. Neither proves in-game loading.

Stock extraction and texconv now share `court_creator/tool_process.py`. It reads
merged stdout/stderr through an unbuffered nonblocking pipe, keeping at most a
64 KiB raw diagnostic tail and reading at most 8 KiB per iteration. More than
16 MiB of diagnostic output fails the operation; newline-free floods do not grow
an in-memory log or create disk logs. Best-effort UTF-8 decoding replaces malformed
or truncated bytes instead of masking a tool's result. The extractor keeps its
180-second execution deadline and texconv keeps its 600-second deadline.

Tools receive closed stdin, an explicit argument list without a shell, and the
Windows no-console creation flag. The runner checks its deadline even during
continuous output, and waits for the direct process to exit even if it has already
closed stdout. After process exit, an inherited output pipe has only a one-second
drain allowance. Failure, timeout or interruption kills a still-running direct
child, waits up to two seconds, and closes the output pipe; no reader thread is
created. Process creation itself is not interruptible through this helper, and
it does not claim containment or termination of escaped descendants.

Windows nonblocking pipe support uses the bundled Python 3.12 API, documented in
[Python's `os.set_blocking` reference](https://docs.python.org/3.12/library/os.html#os.set_blocking).
`python -B -m unittest tests.test_tool_process tests.test_flattened_png tests.test_stock_extraction`
checks actual short-lived child processes, merged output, argument/cwd preservation,
stdin EOF, exact/over-limit budgets, floods, timeouts, output EOF before exit,
fragmented/invalid bytes, setup/read failures and interruption. An 8 MiB output
fixture verifies bounded traced Python allocation, not whole-process RSS. A held
ordinary pipe models inherited-writer behavior; escaped descendant termination
is not established. Existing export/cache and game-file bytes remain intact when
runner failures are injected at the real integration points.

A 2026-10-06 check also ran the installed NBA 2K27 `mod.exe` against the private
scratch manifest through this runner. It returned the expected 8192 x 4096
`BC7_UNORM` court DDS; full hashes and timestamps of the installed manifest and
extractor matched before/after. No loose stock floor existed before or after the
check, and scratch was removed.
This checks actual extraction, not game launch or the contents of every installed
archive.

Project Save/Save As also protects input floor, logo, preview and legacy template
paths, including aliases through junctions and existing hard links. Both the
project destination and its `.bak` path are checked before preparing assets and
again before publishing JSON. An unrecognized/damaged file at the backup path
is preserved and produces a Save As instruction rather than being overwritten.
Linked portable asset folders/files are not reused or written through; regular
OneDrive placeholder entries are not treated as symbolic links. Direct Save As
of a project-relative snapshot resolves input paths against the original project
directory, then bundles independent copies beside the new project.

Project reads use one bounded, read-shared stream rather than checking size and
reopening a potentially changed file. Invalid UTF-8 is rejected, existing UTF-8
and UTF-16 BOM compatibility is retained, and writes validate schema, byte size
and the same 32-level nesting limit as reads before publishing. Native regression
checks exercise input/backup/hard-link collisions, a real Windows junction,
portable relative Save As, malformed encodings, oversize/deep snapshots, and
recovery of enabled controls after rejected saves. These are accidental-data-loss
guards, not protection against hostile external filesystem races.

Native project/recovery writes make at most four attempts for Windows
access-denied, sharing-violation and lock-violation errors (25/50/100 ms waits).
Initial destination checks can retry, and publication rechecks source aliases
and backup validity on every attempt. Publication alone also retries Win32 1175
(`ERROR_UNABLE_TO_REMOVE_REPLACED`), which retains the original filenames and is
reproduced by holding an existing backup open without delete sharing. A real
temporary backup lock recovers; a persistent one retains project, backup and
staging and reports failure after four attempts. Backup edits between attempts
are revalidated and preserved. Generic file access does not retry this code.
A missing staging file stops retries; validation, disk/path errors and partial
`ReplaceFile` move failures 1176/1177 are not retried. No permission/attribute
changes or non-atomic copy fallback are
used. These classifications follow Microsoft's
[Win32 error codes](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-)
and [ReplaceFile failure states](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
An access-denied result is not assumed to be transient indefinitely: persistent
locks and real read-only files still report failure after the bounded attempts.
Staging cleanup has the same bounded retry and logs an unremovable owned temporary
instead of masking the primary save error. Tests use actual Windows file handles,
change backup/hard-link destinations between attempts, and verify native async
save keeps the dispatcher live, leaves the document/project intact and restores
controls on failure. The focused off-screen command is
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- --project-files outputs/project-publication-check`.
These checks reproduce file-lock failures, not the unidentified cause of the
single earlier recovery access-denied result; the rare original trigger is not
claimed to have been identified. Timer/close recovery writes use the background
snapshot queue described above; bounded file-access waits run on that worker.

Native project/recovery and preference writers capture their staging identity
from the newly created exclusive file handle, before writing. Publication and
cleanup inspect the named path without following its final reparse point and
accept only the original non-directory, non-reparse, single-link file. Identity
is checked before and after destination validation on every publication attempt,
and before every cleanup attempt. Replaced/shared/linked paths are retained;
cleanup logs the exact retained path without masking the original failure. Paths
whose successful creation was never recorded are not deleted. Normal commits
still use atomic move/replace, and a foreign file reusing the temporary filename
after commit is left intact. The metadata-only handle inspection follows
[CreateFile reparse-point semantics](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
and uses the existing volume/file-index identity comparison. These checks do not
depend on a process long-path manifest: native metadata lookup adds the extended
path prefix when needed. Real local paths exceeding 260 characters cover project
publication, backup replacement, preferences and cleanup. Network-share behavior
is not verified by that local test. These checks do not
pin in-place byte edits, guarantee identical file-ID behavior on every filesystem,
or eliminate a non-cooperating change between the last check and rename/delete.
The focused off-screen `--staging` smoke mode covers both publication modes,
retry-time substitution, hard links, real junctions, persistent cleanup locks,
cleanup ownership changes, original errors, filename reuse and the real
preferences writer failing safely then recovering:
`dotnet run --project tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj -c Release -- --staging outputs/native-staging-check`.

Autosave uses the app-owned recovery slots rather than requiring Save As. If its
`.bak` is damaged/unrecognized, that file is left untouched; the previous valid
recovery is atomically retained in `recovery.json.last-good.json` before the new
primary is published. Startup checks that fallback after the primary and usual
backup, so an unusable backup does not disable autosave or remove the last-good
recovery path. Tests cover continued saving, primary corruption, fallback and
resumed saving while preserving the unusable backup's bytes.

Source court inspection validates DDS header sizes, dimensions, mip counts,
complete supported block-compressed/uncompressed mip payloads, and single 2D
surfaces before decoding. Cube/volume/array textures are not court conversion
inputs. Legacy header flags are not required merely because older writers may
omit them, consistent with Microsoft's
[DDS header guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-header)
and [DX10 extension](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-header-dxt10).
Exact duplicate IFF entry names and missing explicit selections are rejected
instead of resolving to another entry or falling back to the first texture.

Native conversion requests pin a SHA-256 revision of the selected DDS bytes,
entry name and archive layout comment. A changed source is rejected before pixel
decoding/publication and requires reimport; the prior workspace preview/output
remain intact. The digest is updated without concatenating another full DDS
buffer. Already-RGBA DDS pixels are retained without an unnecessary RGBA copy,
and decoded source images are closed before PNG publication. Recorded layouts
still avoid pixel decoding during inspection (their selected bytes are read for
the revision). Older clients without a source revision retain compatibility.
Failed current preview refreshes retain the last visible drawing but invalidate
conversion readiness until a refresh/reimport succeeds. Native checks mutate the
comment of their own real exported IFF, verify changed-source rejection and intact
project/drawing, restore it, and verify reimport recovers conversion controls.

Native floor/logo preview decoding hashes the complete encoded image through the
same read-shared stream used for decoding. The bounded image cache is keyed by
that SHA-256 content revision, not size/mtime, so identical-size/timestamp edits
cannot reuse stale pixels. Identical file bytes at different paths share the same
decoded bitmap at a given decode width, including duplicate logo copies.
Preview decoding never upscales source pixels; large requested preview widths
cannot turn a small/tall valid image into an oversized decoded allocation.
Weak image-to-revision associations retain the original
revision while a bitmap remains in a preview or duplicate layer;
they do not add another permanent image cache. Imported/restored external logos
are copied first and then decoded from the actual owned file. Uncommitted copies
are cleaned if decoding, cancellation or a later project preparation fails.

Native snapshots include the loaded hardwood/logo source revisions. Python
rendering verifies those bytes before use and after decoding through one open
file; pinned logo cache hits also verify the source rather than trusting metadata
or samples. Verified reads are unbuffered so decoder prefix reads/seeks cannot
make the post-decode hash reuse prefetched old bytes from a changed small file.
Missing pinned visible logos fail instead of being silently omitted.
Library-path relocation preserves the expected revision. Conversion requests also
pin their hardwood background independently of the imported DDS revision.
Portable saves compare pinned assets while holding the source read-shared handle
before bundling; repeated-path deduplication checks the same revision. Mismatches
leave saved project/export files and the visible document intact, with controls
reenabled. Refreshing hardwood or explicitly reloading/reimporting artwork loads
a new revision. Project restore notices changed saved artwork and marks the
document dirty; legacy projects without revision metadata remain supported.

Hardwood/logo image file reads are limited to 512 MiB; decoded raster assets are limited
to 16,384 pixels per side and 64 megapixels. Python hash scans also check the byte
budget while reading. The source checks are accidental-staleness guards, not a
guarantee against an adversary changing and restoring a file during decoding.
Tests exercise same-size/same-timestamp replacements, retained native/cached
images, altered files during decoding, missing logos, relocated library entries,
real native PNG/IFF rejection, portable save protection, conversion background
mismatch and explicit refresh/reload/save/export recovery.
These raster budgets do not replace the separate legacy DDS import limits.

Court import inspection/alignment share a decoded DDS cache with a 128 MiB pixel
budget and at most two entries. The key includes the complete selected DDS bytes,
entry name and archive layout comment; each request rereads/verifies those inputs
before reusing pixels. The original RGBA pixels are borrowed read-only while a
lock prevents concurrent eviction/close, avoiding another full-image copy.
Alignment, preview size and background changes never modify the cached source.
Oversized sources remain supported under the separate DDS limits but are decoded
without retention and closed after use. One-off `read_dds` calls still return an
independent decoded image. Recorded export layouts continue to skip decoding
during inspection. Temporary background, overlay, alpha and final PNG images are
closed after rendering or a failed operation. These are retained-cache budgets,
not total process-memory limits. Tests cover exact cached/uncached pixels,
same-size/timestamp edits, layout changes, byte/entry limits, failure recovery and
concurrent borrowers. `tests/check_import_cache.py` audits repeated alignment of
the existing real 8192-by-4096 BC7 base without modifying game/base files.

Python's retained RGBA logo/floor image cache has a 64 MiB byte budget and eight
entries; the legacy PSD preview-layer cache has a 128 MiB budget and 64 entries.
Entries larger than their budget are not retained. LRU eviction/replacement closes
cache-owned images while returned copies remain independent. These limits apply
to retained pixel buffers, not total process memory or the active source/output.

Logo rendering keeps the existing LANCZOS resize/BICUBIC rotation for ordinary
transforms. When resized or rotated intermediates would exceed 16 megapixels,
it instead samples original pixels with a centered inverse affine transform into
only the visible canvas region. A 32768-by-32768 display size remains supported
without allocating that full resized/rotated image. Original source decoding and
the court canvas still consume memory within their separate limits. Fully
off-court logos avoid pixel decoding but still verify pinned source bytes.
Regression checks cover opacity, rotations, both flips, thin/partly clipped
logos, preview scales, byte-budget eviction and source changes; a real native
full-resolution PNG export exercises the maximum supported logo dimensions.

IFF opening bounds ZIP directory reads to 8 MiB before the standard ZIP parser
allocates its entry objects. A preflight using the bundled Python ZIP header
schema counts actual central-directory entries (maximum 20,000), even when an
end record advertises a false count. The standard library still owns ZIP/ZIP64
parsing, filename handling and CRC validation; commented and prefixed archives
remain supported. Floor scene reads/replacements are limited to 16 MiB before
scene parsing/conversion. These are metadata/scene budgets, not a blanket limit
on source IFF size or unselected member sizes.

Native worker request files are created with unique names and create-new semantics,
with the same 16 MiB UTF-8/depth-32 limits used by the backend. Serialization runs
off the dispatcher; the byte limit applies to serialized files, not all managed
allocations. A retained read-shared creation handle prevents ordinary writes and
renames while Python reads the request. Preparation, queueing and execution share
the caller's deadline and cancellation/disposal lifetime. Serialization and OS
I/O are not forcibly interruptible. Cleanup runs off the dispatcher and requires
the creation identity, one ordinary link, exact length and a full SHA-256 match;
changed, replaced, shared or persistently locked files are retained with their
path reported through tracing. No temporary-folder sweep is used. The focused
`--request-files` check covers real Python reads, Unicode, bounds, canceled partial
writes, changed/shared files and idempotent cleanup without opening app windows.

Base preparation and IFF export stream unchanged members in 1 MiB chunks rather
than retaining each complete member in memory. Copied entry descriptors preserve
metadata without mutating the source archive's descriptors. Replacement-byte
verification is also chunked. Finished archives pass CRC checks before publication;
failed copies/verifications leave the prior destination intact and remove only
their unique staging file. Archive regressions cover forged directory sizes/counts,
ZIP64/comments/prefixes, duplicate entries, bounded/truncated scenes, exact member
bytes/metadata, corruption during copying and foreign staging-file preservation.
