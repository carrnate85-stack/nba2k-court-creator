"""Audit real release artifacts in an isolated installation; never start the app."""
from __future__ import annotations

import hashlib
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import updater
from tools import build_release as packaging


def fingerprint(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(updater.CHUNK_BYTES), b""):
            digest.update(chunk)
    metadata = path.stat()
    return metadata.st_size, metadata.st_mtime_ns, digest.hexdigest()


def snapshot(directory):
    return {path.relative_to(directory).as_posix(): fingerprint(path)
            for path in directory.rglob("*") if path.is_file()}


def check_native_manifest(path):
    if os.name != "nt":
        return {"embeddedDpiManifestVerified": False, "manifestCheckScope": "Windows resource APIs unavailable"}
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p, wintypes.DWORD]
    kernel.LoadLibraryExW.restype = ctypes.c_void_p
    kernel.FindResourceW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    kernel.FindResourceW.restype = ctypes.c_void_p
    kernel.LoadResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel.LoadResource.restype = ctypes.c_void_p
    kernel.LockResource.argtypes = [ctypes.c_void_p]
    kernel.LockResource.restype = ctypes.c_void_p
    kernel.SizeofResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel.SizeofResource.restype = wintypes.DWORD
    kernel.FreeLibrary.argtypes = [ctypes.c_void_p]
    kernel.FreeLibrary.restype = wintypes.BOOL
    # LOAD_LIBRARY_AS_DATAFILE reads resources without running executable code.
    module = kernel.LoadLibraryExW(str(path.resolve()), None, 0x00000002)
    if not module:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        resource = kernel.FindResourceW(module, 1, 24)
        assert resource, "Published executable has no application manifest"
        size = kernel.SizeofResource(module, resource)
        assert 0 < size <= 65536, "Invalid application manifest resource size"
        pointer = kernel.LockResource(kernel.LoadResource(module, resource))
        assert pointer, "Published manifest resource could not be read"
        manifest = ET.fromstring(ctypes.string_at(pointer, size).decode("utf-8-sig"))
        dpi = manifest.find(".//{http://schemas.microsoft.com/SMI/2016/WindowsSettings}dpiAwareness")
        legacy = manifest.find(".//{http://schemas.microsoft.com/SMI/2005/WindowsSettings}dpiAware")
        level = manifest.find(".//{urn:schemas-microsoft-com:asm.v3}requestedExecutionLevel")
        assert dpi is not None and [value.strip() for value in (dpi.text or "").split(",")] == ["PerMonitorV2", "PerMonitor"], "Published executable lost per-monitor DPI awareness"
        assert legacy is not None and (legacy.text or "").strip().lower() == "true/pm", "Published executable lost fallback DPI awareness"
        assert level is not None and level.attrib == {"level": "asInvoker", "uiAccess": "false"}, "Published executable requests unexpected elevation/UI access"
        return {"embeddedDpiManifestVerified": True, "dpiAwareness": dpi.text, "executionLevel": level.attrib["level"]}
    finally:
        kernel.FreeLibrary(module)


def main():
    inputs = packaging.collect_files(ROOT)
    original = {path.relative_to(ROOT).as_posix(): fingerprint(path) for path in inputs}
    report = {"scope": "Real published artifacts; temporary standalone installation; no app launch, network, or upload."}
    report.update(check_native_manifest(ROOT / "desktop/NBA2KCourtCreator.exe"))
    with tempfile.TemporaryDirectory(prefix="court-release-audit-") as temporary:
        directory = Path(temporary)
        source, installed = directory / "source", directory / "installed"
        source.mkdir()
        installed.mkdir()
        for path in inputs:
            target = source / path.relative_to(ROOT)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
        archive = packaging.build(source)
        archive_revision = fingerprint(archive)
        manifest = json.loads((source / "package.json").read_text(encoding="utf-8-sig"))
        old_manifest = dict(manifest, version="0.0.0")
        (installed / "package.json").write_text(json.dumps(old_manifest), encoding="utf-8")
        for name in ("studio-build.json", "requirements.txt"):
            shutil.copy2(source / name, installed / name)
        (installed / "desktop").mkdir()
        for name in updater.NATIVE_FILES:
            target = installed / "desktop" / name
            if name.endswith("runtimeconfig.json"):
                shutil.copy2(source / "desktop" / name, target)
            else:
                target.write_bytes(b"old installation sentinel: " + name.encode("ascii"))
        personal = {
            "logos/personal.png": b"personal logo sentinel",
            "custom_floors/personal.png": b"personal hardwood sentinel",
            "runtime/python/personal.txt": b"runtime sentinel",
            "assets/library/personal.png": b"court library sentinel",
            "data/court_presets.json": b'{"personal":"preset"}',
            "data/game_installation.json": b'{"personal":"game path"}',
            "data/update_config.json": b'{"personal":"update preference"}',
        }
        for name, data in personal.items():
            target = installed / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        before = snapshot(installed)
        updates = installed / "updates"
        release = {"tag_name": "v" + manifest["version"], "assets": [{
            "name": archive.name, "size": archive_revision[0],
            "browser_download_url": "https://github.com" + updater.DOWNLOAD_PREFIX + "v" + manifest["version"] + "/" + archive.name}]}

        def transfer(_url, stream, limit):
            assert limit == archive_revision[0]
            with archive.open("rb") as incoming:
                shutil.copyfileobj(incoming, stream, updater.CHUNK_BYTES)
            return limit

        with patch.object(updater, "ROOT", installed), patch.object(updater, "UPDATES", updates), \
                patch.object(updater, "fetch", return_value=json.dumps(release).encode()), \
                patch.object(updater, "transfer", side_effect=transfer):
            updater.stage()
            assert all(fingerprint(installed / name) == value for name, value in before.items()), "Staging changed installation"
            pending = updates / "pending"
            verified = updater.verify_inventory(pending)
            assert len(verified) == len(inputs), "Incomplete staged inventory"
            backup = updates / "rollback/desktop/NBA2KCourtCreator.exe"
            backup.parent.mkdir(parents=True)
            backup.write_bytes(b"last good rollback sentinel")
            backup_state = fingerprint(backup)
            changed = pending / "desktop/TwoK.Studio.dll"
            metadata = changed.stat()
            with changed.open("r+b") as stream:
                first = stream.read(1)
                assert first, "Expected a nonempty real DLL"
                stream.seek(0)
                stream.write(bytes([first[0] ^ 1]))
            os.utime(changed, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
            try:
                updater.apply()
            except ValueError as error:
                assert "pending update file changed" in str(error), str(error)
            else:
                raise AssertionError("Changed real DLL was installed")
            assert all(fingerprint(installed / name) == value for name, value in before.items()), "Integrity failure changed installation"
            assert fingerprint(backup) == backup_state, "Integrity failure changed last good rollback"
            assert not (updates / "apply-journal.json").exists(), "Integrity failure created an install journal"
            shutil.copy2(source / "desktop/TwoK.Studio.dll", changed)
            updater.apply()
            for path in inputs:
                name = path.relative_to(ROOT).as_posix()
                expected = original[name]
                actual = fingerprint(installed / name)
                assert (actual[0], actual[2]) == (expected[0], expected[2]), "Installed bytes mismatch: " + name
            for name in personal:
                assert fingerprint(installed / name) == before[name], "Personal data changed: " + name
            assert not (installed / updater.INVENTORY_NAME).exists(), "Private inventory leaked into app root"
            assert not pending.exists() and not (updates / "apply-journal.json").exists(), "Incomplete update cleanup"
            assert not list(installed.rglob(".court-update-*.tmp")), "Leftover copy staging files"
            assert not list(updates.glob("stage-*")), "Leftover download staging directory"
            assert not list(source.rglob(".court-release-*.tmp")), "Leftover package staging files"
        report.update({"releaseVersion": manifest["version"], "files": len(inputs),
                       "expandedBytes": sum(value[0] for value in original.values()),
                       "archiveBytes": archive_revision[0], "archiveSha256": archive_revision[2],
                       "changedDllSameSizeAndTimestampRejected": True,
                       "lastGoodRollbackPreservedOnRejection": True,
                       "allInstalledFilesMatchOriginal": True, "personalFilesPreserved": len(personal),
                       "privateInventoryNotInstalled": True, "updateScratchRemoved": True})
    assert {path.relative_to(ROOT).as_posix(): fingerprint(path) for path in packaging.collect_files(ROOT)} == original, "Original project files changed"
    assert not directory.exists(), "Temporary audit installation remains"
    report.update({"originalSourceBytesAndTimestampsUnchanged": True, "temporaryInstallationRemoved": True})
    output = ROOT / "outputs/release-integrity-audit.json"
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
