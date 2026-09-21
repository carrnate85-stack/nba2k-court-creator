# Set up on another Windows PC

1. Clone https://github.com/carrnate85-stack/nba2k-court-creator using GitHub Desktop.
2. Install Python 3.12 and Node.js, then run `Setup Court Creator.bat`.
3. Transfer the local `templates` folder into the cloned repository. The PSD is intentionally not in Git.
4. Transfer `OneDrive\Documents\2kcourtmodder` to that same location under the new Windows user. This contains the extracted NBA 2K27 court library. The app automatically chooses the newest library found there.
5. Transfer `custom_floors`, `logos`, and personal `data\court_presets.json` if needed.
6. Run `Launch NBA 2K Court Creator.bat`.

The Electron app uses an app-owned Python environment. The old WPF and Tkinter sources remain for reference; the launcher only starts Electron.

Use Save Project to store editable `.court.json` files. Recovery is stored in `%APPDATA%\nba2k-court-creator\recovery.json`. Projects currently reference image and template paths; when moving existing projects between computers, keep their asset paths consistent or update those paths in the project JSON.

For development, use GitHub Desktop to pull and push. Automatic release installation is disabled in Git checkouts so local code changes remain protected. Standalone copies check releases in the background, stage an update, and install it on the next launch while the app is closed. A rollback copy is retained in `updates\rollback`.

To publish a compatible application update, increment `package.json`, run `python tools/build_release.py`, and attach `outputs\court-creator-update.zip` to a GitHub release tagged `v<version>`. Changes to Electron or Python dependencies need a full setup update.
