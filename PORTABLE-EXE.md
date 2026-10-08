# Portable Windows EXE

The optional portable bundle is one self-contained Windows x64 EXE containing
the native app, .NET runtime, a relocatable Python interpreter, Pillow, Shapely,
NumPy and the first-run court preparation tools. It contains no extracted game
files, floor images, personal projects, settings or virtual-environment paths.

On first use, it extracts to a versioned directory under
`%LOCALAPPDATA%\2K Court Creator Portable`. It discovers the installed NBA 2K27
game through the existing Steam/game-location discovery, or asks for the folder
containing `manifest` and `mod.exe`. Only supported floor texture pairs are read
from the game archives into scratch space. The stock geometry, floor PNGs and
thumbnail catalog are generated locally. Completed PNGs survive an interrupted
preparation; the catalog is committed only after every supported floor succeeds.
Subsequent starts reuse that cache. The installed game archives and loose mods
are not replaced. Game-specific import/export still needs the game installation.

The launcher verifies its embedded payload SHA-256 before extraction, validates
entry paths and sizes, and installs through a private candidate directory. Its
normal user-facing launch then provisions the floors and opens the app. It does
not stage release updates; portable updates are supplied as a new bundle, leaving
earlier caches intact. Save portable projects outside the extracted app directory.

Build after verifying the native app against the matching released Canvas pair:

```powershell
python -B tools/build_portable.py --python-base <full-python-3.12-x64-folder> `
  --site-packages <court-runtime-Lib-site-packages> --canvas-feed <local-feed>
```

Outputs live in ignored `outputs`. Do not commit interpreters, .NET binaries,
bundles, generated floors or game archives. The bundled Python directory is a
complete standalone runtime; `pyvenv.cfg` is deliberately excluded.

For an off-screen packaging check, run the produced EXE with
`--extract-only <new-empty-path>`. This only extracts; it never opens the app or
prepares floors. Validate the extracted `portable-manifest.json`, then run its
Python with `-I` for binary-import checks and `tools/prepare_portable.py` to verify
first-run preparation against the installed game. The latter does not open the
application. `--check-only` checks cache readiness without preparing anything.
