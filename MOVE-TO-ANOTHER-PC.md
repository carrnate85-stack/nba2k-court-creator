# Set up on another Windows PC

1. Clone https://github.com/carrnate85-stack/nba2k-court-creator using GitHub Desktop.
2. For a source checkout, install 64-bit Python 3.12 or newer and the x64 .NET 8 SDK, then run `Setup Court Creator.bat`. A complete published copy can reuse its bundled Python and needs only the x64 .NET 8 Desktop Runtime, not the SDK. The console/ASP.NET runtime alone cannot run this WPF application.
3. The PSD `templates` folder is optional while the legacy template system is hidden.
4. Transfer the local `assets` folder into the cloned repository. This contains the extracted NBA 2K27 court library. The app automatically chooses the newest library found there.
5. Transfer `custom_floors`, `logos`, and personal `data\court_presets.json` if needed.
6. Run `Launch NBA 2K Court Creator.bat`.

Source builds now also require the matching `TwoK.Canvas.Core` and
`TwoK.Canvas.Wpf` 0.3.1 packages from the suite's private feed. Put that feed in
the sibling `2k Texture Studio\artifacts\published-shared-v0.3.1` folder or set
`CanvasToolkitFeed` to its location before Setup/Build. A complete published
desktop folder already contains these libraries and needs no separate Canvas
installation. See [Shared Artwork](SHARED-ARTWORK.md).

The launcher starts the native C#/WPF studio with the app-owned Python backend.
Builds publish into `desktop` using `Build Court Creator.bat`. The earlier
Electron version is retained as `Launch Electron Fallback.bat`; it needs Node.js
and `npm install` only when using that fallback.

Setup reuses an existing bundled or virtual-environment Python executable. It
does not create a new environment over a populated `runtime\python` folder.
Missing/out-of-range dependencies are installed into the app-owned runtime;
damaged binary imports can trigger a targeted requirements reinstall. Healthy
runtime packages and the published desktop build are left untouched. Setup will
ask you to close the app before dependency/build changes, and never opens it.

For a read-only diagnostic, run `Setup Court Creator.bat --check-only`. Structured
output is available with `--check-only --json`. These checks cover runtimes and
build files, not the separately transferred court library or in-game behavior.
Python virtual environments should be recreated on a new PC rather than copied;
the complete standalone bundled runtime is a separate distribution. See
[Python's venv portability guidance](https://docs.python.org/3.12/library/venv.html)
and [Microsoft's runtime/SDK guidance](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
An incomplete runtime folder is preserved with a repair message, not deleted.

Use Save to store editable `.court.json` files. New native saves also create an
adjacent `<project-name>.assets` folder with the selected hardwood, custom floors,
and available logos, including editable artwork archives and derived DDS files.
Transfer the JSON and this folder together; their relative
paths work from the new location. Identical images are stored once by content
hash. The app's full court library is still transferred separately in step 4.
The saved hardwood remains authoritative when its ID also exists in the new PC's
library, including after paint edits and undo/redo. Missing saved artwork can use
the local library with a notice; transfer the assets folder to retain custom pixels.

Native recovery and preferences are stored in
`%LOCALAPPDATA%\2K Studio\Court Creator`. The native app can adopt existing Electron
recovery. Imported logos are also retained in the local `logos` folder for recovery
and undo. Older projects can relocate app-owned paths, but arbitrary external
paths may need updating. Re-save an older project in the native studio before
transferring it to create a portable assets folder. If startup fails, Retry is
available in the status bar after fixing the missing runtime or court asset.

Recovery and preference writes run in the background and coalesce rapid changes.
Favorites, recent courts and theme use their own settings format. Normal app close
briefly disables editing while the newest recovery and settings snapshots finish,
without freezing the window. If either cannot be saved, keep the app open to
correct the problem or explicitly choose to close without that latest copy.
Previous valid files are retained. Damaged or unrecognized `preferences.json`
files are preserved; the app uses defaults and reports the problem. Repair or
rename the indicated file before saving new preferences. Forced termination/OS
shutdown may interrupt unfinished writes; recovery is not a substitute for saving
a portable project.

Project files are read and validated in the background. The prior court stays
visible while loading, with editing/save/export temporarily disabled. Failed
opens retain that court and its undo/redo history. Closing during loading cancels
the incoming project and saves recovery/settings for the preceding court before
closing. Late read/decode results cannot replace it. Operating-system reads may
finish later; they are not forcibly interrupted. If the final save fails, the app
stays open and releases editing when cancellation finishes. Retry close after
correcting the save problem. A project save already in progress must finish
before closing.

For development, use GitHub Desktop to pull and push. Automatic release installation is disabled in Git checkouts so local code changes remain protected. Standalone copies check releases in the background, stage an update, and install it on the next launch while the app is closed. A rollback copy is retained in `updates\rollback`. Interrupted installs are recovered from `updates\apply-journal.json` before retrying. If the launcher reports that recovery needs attention, preserve the entire `updates` folder and review `updates\last-error.txt`; do not delete the journal or rollback files to bypass the check.

To publish a compatible update, increment `package.json`, run `Build Court
Creator.bat`, run `python tools/build_release.py`, and attach
`outputs\court-creator-update.zip` to a GitHub release tagged `v<version>`.
`studio-build.json` prevents older Electron releases from replacing the native
application. Changes to the desktop runtime or Python dependencies require a
full setup update. Publishing a release is a separate, explicit operation.

Packaging verifies a temporary ZIP before atomically replacing the prior archive.
Updates include the managed team-color catalog and its source metadata, but not
personal presets, game locations, logos, court assets or the Python runtime.
Staged updates retain file hashes and are rechecked before installation; altered
or older unsealed native queues are refused with a download-again message while
the current app remains usable. This protects against accidental staged changes,
not a substitute for independently signed releases. A local artifact audit is
available with `runtime\python\python.exe -B tests\check_release.py`; it uses a
temporary installation and does not open the app or access GitHub.
