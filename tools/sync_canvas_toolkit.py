"""Adopt published central Canvas packages through an off-screen verified rebuild."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import updater

MINIMUM_VERSION = "0.6.0"
STAMP = ".canvas-toolkit.json"
JOURNAL = "canvas-install.json"
PACKAGES = ("TwoK.Canvas.Core", "TwoK.Canvas.Wpf")
ASSEMBLIES = ("Canvas.Core.dll", "Canvas.Wpf.dll")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package_info(path, expected_id):
    if path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("Oversized Canvas package")
    with zipfile.ZipFile(path) as archive:
        manifests = [item for item in archive.infolist() if item.filename.endswith(".nuspec")]
        if len(manifests) != 1 or manifests[0].file_size > 256 * 1024:
            raise ValueError("Invalid Canvas package metadata")
        document = ET.fromstring(archive.read(manifests[0]))
        def elements(name):
            return [item for item in document.iter() if item.tag.rsplit("}", 1)[-1] == name]
        if len(elements("id")) != 1 or elements("id")[0].text != expected_id or len(elements("version")) != 1:
            raise ValueError("Canvas package identity does not match its filename")
        version = elements("version")[0].text
        if not re.fullmatch(r"\d+\.\d+\.\d+", version or ""):
            raise ValueError("Only stable Canvas releases are adopted automatically")
        dependencies = {item.get("id"): item.get("version") for item in elements("dependency")}
        assembly = ASSEMBLIES[PACKAGES.index(expected_id)]
        members = [item for item in archive.infolist() if item.filename.startswith("lib/") and item.filename.endswith("/" + assembly)]
        if len(members) != 1 or members[0].file_size > 16 * 1024 * 1024:
            raise ValueError("Invalid Canvas package assembly")
        return {"version": version, "dependencies": dependencies, "packageHash": digest(path),
                "assemblyHash": hashlib.sha256(archive.read(members[0])).hexdigest(), "path": path}


def find_release(root):
    explicit = os.environ.get("CanvasToolkitFeed")
    central = Path(os.environ.get("CanvasToolkitRoot", str(root.parent / "2k Texture Studio")))
    if explicit:
        directories = [Path(explicit).resolve()]
    else:
        artifacts = central / "artifacts"
        directories = sorted(path for path in artifacts.glob("published-*-v*") if path.is_dir())
    requested = os.environ.get("CanvasToolkitVersion")
    releases = []
    for directory in directories:
        by_version = {}
        for name in PACKAGES:
            for path in directory.glob(name + ".*.nupkg"):
                info = package_info(path, name)
                by_version.setdefault(info["version"], {})[name] = info
        for version, pair in by_version.items():
            if updater.version(version) < updater.version(MINIMUM_VERSION) or requested and version != requested:
                continue
            if set(pair) != set(PACKAGES):
                raise ValueError(f"Published Canvas {version} is missing its matching Core/WPF package")
            dependency = pair[PACKAGES[1]]["dependencies"].get(PACKAGES[0])
            if dependency not in (version, f"[{version}]", f"[{version}, )"):
                raise ValueError("Canvas WPF does not depend on the matching Core version")
            releases.append((version, pair))
    if not releases:
        if explicit or requested:
            raise ValueError("The configured Canvas feed has no matching stable release >= " + MINIMUM_VERSION)
        return None
    releases.sort(key=lambda item: updater.version(item[0]), reverse=True)
    version, pair = releases[0]
    fingerprints = {name: pair[name]["packageHash"] for name in PACKAGES}
    if any({name: other[name]["packageHash"] for name in PACKAGES} != fingerprints for candidate, other in releases if candidate == version):
        raise ValueError("The same Canvas version was published with different package contents; publish a new version")
    return version, pair


def host_revision(root):
    files = [path for base in (root / "src", root / "tools/CourtStudio.Smoke", root / "court_creator")
             for path in base.rglob("*") if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(path.relative_to(base).parts)]
    files += [root / name for name in ("package.json", "studio-build.json", "requirements.txt", "updater.py",
                                      "Build Court Creator.bat", "Launch NBA 2K Court Creator.bat",
                                      "tools/setup_court_creator.py", "tools/sync_canvas_toolkit.py")]
    result = hashlib.sha256()
    for path in sorted(files):
        result.update(path.relative_to(root).as_posix().encode("utf-8"))
        result.update(bytes.fromhex(digest(path)))
    return result.hexdigest()


def inventory(directory):
    if not directory.exists():
        return None
    return {path.relative_to(directory).as_posix(): updater.file_revision(path) for path in updater.tree_files(directory)}


def recovery_path(value, prefix):
    if not isinstance(value, str) or not re.fullmatch(prefix + r"[0-9a-f]{32}", value):
        raise updater.UpdateRecoveryError("Invalid Canvas recovery path")
    return updater.owned_path(updater.UPDATES / value)


def recover_install():
    journal = updater.owned_path(updater.UPDATES / JOURNAL)
    if not journal.exists():
        return
    record = updater.read_json(journal)
    backup = recovery_path(record.get("backup"), "canvas-backup-")
    current = inventory(updater.ROOT / "desktop")
    old, new = record.get("old"), record.get("new")
    if not isinstance(new, dict) or not new or old is not None and not isinstance(old, dict):
        raise updater.UpdateRecoveryError("Invalid Canvas recovery inventory")
    if current == new:
        if backup.exists():
            if inventory(backup) != old:
                raise updater.UpdateRecoveryError("Canvas backup has unrelated changes; preserved")
            updater.remove_tree(backup)
    elif current is None and inventory(backup) == old and old is not None:
        os.replace(backup, updater.ROOT / "desktop")
    elif current == old:
        if backup.exists():
            raise updater.UpdateRecoveryError("Unexpected Canvas backup; preserved")
    else:
        raise updater.UpdateRecoveryError("Canvas desktop files changed during installation; preserved for recovery")
    journal.unlink()


def install_candidate(candidate):
    desktop = updater.owned_path(updater.ROOT / "desktop")
    candidate = updater.owned_path(candidate)
    if not candidate.is_relative_to(updater.UPDATES):
        raise ValueError("Canvas candidate must be staged inside updates")
    old, new = inventory(desktop), inventory(candidate)
    backup = updater.owned_path(updater.UPDATES / ("canvas-backup-" + uuid.uuid4().hex))
    journal = updater.UPDATES / JOURNAL
    updater.write_metadata(journal, {"old": old, "new": new, "backup": backup.name}, prefix="canvas-journal-")
    try:
        if inventory(desktop) != old or inventory(candidate) != new:
            raise ValueError("Canvas build files changed before installation")
        if desktop.exists():
            os.replace(desktop, backup)
        os.replace(candidate, desktop)
        recover_install()
    except Exception:
        recover_install()
        raise


def run(arguments, root):
    subprocess.run(arguments, cwd=root, check=True, timeout=900)


def native_running():
    if os.name != "nt":
        return False
    result = subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                             "if (Get-Process NBA2KCourtCreator -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"],
                            timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode not in (0, 1):
        raise RuntimeError("Could not check whether Court Creator is running")
    return result.returncode == 1


def sync(root=ROOT, *, force=False, automatic=False, runner=run, dotnet_path=None):
    root = Path(root).resolve()
    if root != updater.ROOT.resolve():
        raise ValueError("Sync must use its own Court Creator installation")
    with updater.update_lock():
        recover_install()
        if automatic and (not (root / ".git").exists() or not (root / "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj").is_file()):
            return "Standalone install: bundled Canvas libraries retained."
        release = find_release(root)
        if release is None:
            if automatic:
                return "No local published Canvas feed: existing build retained."
            raise ValueError("Publish the central Canvas libraries, or configure CanvasToolkitFeed / CanvasToolkitRoot")
        version, pair = release
        desired = {"version": version, "packages": {name: pair[name]["packageHash"] for name in PACKAGES}, "hostRevision": host_revision(root)}
        stamp_path = root / "desktop" / STAMP
        stamp = updater.read_json(stamp_path) if stamp_path.exists() else {}
        if stamp.get("version") == version and stamp.get("packages") not in (None, desired["packages"]):
            raise ValueError("Canvas package contents changed without a version bump; existing build retained")
        if stamp.get("version") and updater.version(version) < updater.version(stamp["version"]) and not os.environ.get("CanvasToolkitVersion"):
            raise ValueError("Automatic Canvas downgrade refused; existing build retained")
        current = inventory(root / "desktop")
        if current:
            current.pop(STAMP, None)
        if not force and all(stamp.get(key) == value for key, value in desired.items()) and current == stamp.get("files"):
            return "Court Creator already uses Canvas " + version
        if native_running():
            raise updater.UpdateBusy("Close Court Creator before updating its shared libraries")
        dotnet = dotnet_path or shutil.which("dotnet")
        if not dotnet:
            raise RuntimeError("A central library update requires the .NET 8 SDK; existing build retained")
        stage = Path(tempfile.mkdtemp(prefix="canvas-build-", dir=updater.UPDATES))
        try:
            feed = stage / "feed"; feed.mkdir()
            for name in PACKAGES:
                info = pair[name]; destination = feed / info["path"].name
                shutil.copyfile(info["path"], destination)
                if digest(destination) != info["packageHash"]:
                    raise ValueError("Published Canvas package changed while preparing the build")
            properties = [f"-p:CanvasToolkitVersion={version}", f"-p:CanvasToolkitFeed={feed}"]
            candidate = stage / "desktop"
            print("Verifying Court Creator against central Canvas " + version, flush=True)
            runner([str(dotnet), "publish", "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj", "-c", "Release",
                    "--self-contained", "false", "-o", str(candidate), "--nologo", *properties], root)
            for name in updater.NATIVE_FILES:
                if not (candidate / name).is_file():
                    raise ValueError("Incomplete candidate desktop build: " + name)
            dependencies = updater.read_json(candidate / "NBA2KCourtCreator.deps.json")
            if any(name + "/" + version not in dependencies.get("libraries", {}) for name in PACKAGES):
                raise ValueError("Candidate did not consume the matching Canvas package versions")
            for name, assembly in zip(PACKAGES, ASSEMBLIES):
                if digest(candidate / assembly) != pair[name]["assemblyHash"]:
                    raise ValueError("Candidate used a stale cached Canvas assembly; clear that NuGet version and retry")
            for gate in ("--artwork-editor", "--color-pickers", "--shared-controls"):
                runner([str(dotnet), "run", "--project", "tools/CourtStudio.Smoke/CourtStudio.Smoke.csproj", "-c", "Release",
                        *properties, "--", str(stage / "checks"), gate], root)
            for name, assembly in zip(PACKAGES, ASSEMBLIES):
                if digest(root / "tools/CourtStudio.Smoke/bin/Release/net8.0-windows" / assembly) != pair[name]["assemblyHash"]:
                    raise ValueError("Integration checks consumed a different Canvas package")
            if host_revision(root) != desired["hostRevision"] or any(digest(pair[name]["path"]) != desired["packages"][name] for name in PACKAGES):
                raise ValueError("Source or central packages changed during verification; retry the build")
            if native_running():
                raise updater.UpdateBusy("Court Creator opened during verification; close it and retry")
            desired["files"] = inventory(candidate)
            updater.write_metadata(candidate / STAMP, desired, prefix="canvas-stamp-")
            install_candidate(candidate)
            return "Court Creator updated to central Canvas " + version + "; integration checks passed."
        finally:
            if not (updater.UPDATES / JOURNAL).exists():
                updater.remove_tree(stage)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--auto", action="store_true", help="Check local suite releases before a user-requested launch")
    parser.add_argument("--build", action="store_true", help="Always rebuild and verify")
    parser.add_argument("--dotnet", type=Path, help="Use the x64 SDK executable verified by Setup")
    arguments = parser.parse_args(argv)
    try:
        result = sync(force=arguments.build, automatic=arguments.auto, dotnet_path=arguments.dotnet)
        if not arguments.auto or "updated" in result:
            print(result)
        return 0
    except Exception as error:
        print("Canvas sync: " + str(error), file=sys.stderr)
        if isinstance(error, updater.UpdateBusy):
            return 2
        return 3 if isinstance(error, updater.UpdateRecoveryError) or (updater.UPDATES / JOURNAL).exists() else 1


if __name__ == "__main__":
    raise SystemExit(main())
