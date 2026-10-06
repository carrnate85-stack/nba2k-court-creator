"""Build and atomically publish a verified, allowlisted native update archive."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import updater

ROOT_FILES = ("package.json", "studio-build.json", "requirements.txt", "updater.py",
              "Launch NBA 2K Court Creator.bat", "Setup Court Creator.bat", "Build Court Creator.bat", "data/team_palettes.json")
TOOL_FILES = ("texconv.exe", "texconv-LICENSE.txt", "court_logo_web.py", "export_2k26_court_texture.py", "setup_court_creator.py", "sync_canvas_toolkit.py")


def safe_path(root, path):
    path = Path(path).absolute()
    if not path.is_relative_to(root) or not path.resolve().is_relative_to(root):
        raise ValueError("Release path points outside the project: " + str(path))
    for part in (path, *path.parents):
        if part == root:
            break
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Linked release paths are not supported: " + str(part))
    if path.is_file() and path.stat().st_nlink > 1:
        raise ValueError("Hard-linked release paths are not supported: " + str(path))
    return path


def collect_files(root):
    root = Path(root).resolve()
    files = []
    for name in ("desktop", "court_creator", *updater.STATIC_FOLDERS):
        directory = safe_path(root, root / name)
        if not directory.is_dir():
            raise ValueError("Missing release directory: " + name)
        directories = [directory]
        count = 0
        while directories:
            with os.scandir(directories.pop()) as entries:
                for entry in entries:
                    if entry.name == "__pycache__":
                        continue
                    count += 1
                    if count > updater.MAX_ENTRIES:
                        raise ValueError("Too many release entries")
                    path = safe_path(root, Path(entry.path))
                    if entry.is_dir(follow_symlinks=False):
                        directories.append(path)
                    elif entry.is_file(follow_symlinks=False):
                        files.append(path)
                    else:
                        raise ValueError("Release contains a special file")
    files.extend(root / name for name in ROOT_FILES)
    files.extend(root / "tools" / name for name in TOOL_FILES)
    names, materialized, total, directory_bytes = set(), set(), 0, 0
    for path in files:
        safe_path(root, path)
        if not path.is_file():
            raise ValueError("Missing release input: " + str(path.relative_to(root)))
        relative = path.relative_to(root).as_posix()
        updater.relative_path(relative)
        name = relative.casefold()
        if name in names:
            raise ValueError("Duplicate release path")
        names.add(name)
        while name:
            materialized.add(name)
            name = name.rpartition("/")[0]
        size = path.stat().st_size
        if size > updater.MAX_FILE_BYTES:
            raise ValueError("Release input exceeds its file size limit")
        total += size
        directory_bytes += zipfile.sizeCentralDir + len(relative.encode("utf-8")) + 32
    if len(materialized) + 1 > updater.MAX_ENTRIES or total > updater.MAX_EXPANDED_BYTES or directory_bytes > updater.MAX_DIRECTORY_BYTES:
        raise ValueError("Release exceeds updater inspection limits")
    return sorted(files)


def read_metadata(root, path):
    with safe_path(root, path).open("rb") as stream:
        data = stream.read(updater.MAX_JSON_BYTES + 1)
    if len(data) > updater.MAX_JSON_BYTES:
        raise ValueError("Release metadata exceeds its size limit")
    result = json.loads(data.decode("utf-8-sig"))
    if not isinstance(result, dict):
        raise ValueError("Release metadata must be an object")
    return result


def validate_native(root):
    if any(not safe_path(root, root / "desktop" / name).is_file() for name in updater.NATIVE_FILES):
        raise ValueError("Run Build Court Creator.bat before packaging a complete native release.")
    updater.version(read_metadata(root, root / "package.json").get("version"))
    if read_metadata(root, root / "studio-build.json").get("runtime") != "wpf-net8":
        raise ValueError("Package the native WPF build, not an Electron release")
    config = read_metadata(root, root / "desktop/NBA2KCourtCreator.runtimeconfig.json").get("runtimeOptions")
    frameworks = config.get("frameworks") if isinstance(config, dict) else None
    if (not isinstance(frameworks, list) or len(frameworks) != 2
            or any(not isinstance(item, dict) or item.get("version") != "8.0.0" or not isinstance(item.get("name"), str) for item in frameworks)
            or {item["name"] for item in frameworks} != {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}):
        raise ValueError("Unsupported published desktop runtime configuration")


def verify_archive(path, records):
    if path.stat().st_size > updater.MAX_ARCHIVE_BYTES:
        raise ValueError("Release archive exceeds the download size limit")
    with path.open("rb") as stream:
        reader = updater.DirectoryReader(stream)
        with zipfile.ZipFile(reader) as archive:
            reader.inspecting = False
            if set(archive.namelist()) != records.keys() or len(archive.infolist()) != len(records):
                raise ValueError("Release archive file list mismatch")
            for member in archive.infolist():
                count, digest = 0, hashlib.sha256()
                with archive.open(member) as source:
                    while chunk := source.read(updater.CHUNK_BYTES):
                        count += len(chunk)
                        if count > updater.MAX_FILE_BYTES:
                            raise ValueError("Release member exceeds its size limit")
                        digest.update(chunk)
                if (count, digest.hexdigest()) != records[member.filename]:
                    raise ValueError("Release archive verification failed: " + member.filename)


class BoundedWriter:
    def __init__(self, stream):
        self.stream = stream

    def __getattr__(self, name):
        return getattr(self.stream, name)

    def write(self, data):
        if self.stream.tell() + len(data) > updater.MAX_ARCHIVE_BYTES:
            raise ValueError("Release archive grew beyond the download size limit")
        return self.stream.write(data)


def build(root=ROOT, output=None):
    root = Path(root).resolve()
    files = collect_files(root)
    validate_native(root)
    output = Path(output) if output is not None else Path("outputs/court-creator-update.zip")
    output = safe_path(root, output if output.is_absolute() else root / output)
    if output.exists() and not output.is_file():
        raise ValueError("Release output must be a regular file")
    if output in files or any(output.exists() and output.samefile(path) for path in files):
        raise ValueError("Release output must not replace a source file")
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".court-release-", suffix=".tmp", dir=output.parent)
    os.close(descriptor)
    temporary = Path(name)
    records = {}
    total = 0
    try:
        with temporary.open("w+b") as stream, zipfile.ZipFile(BoundedWriter(stream), "w", zipfile.ZIP_DEFLATED, strict_timestamps=False) as archive:
            for path in files:
                safe_path(root, path)
                relative = path.relative_to(root).as_posix()
                info = zipfile.ZipInfo.from_file(path, relative, strict_timestamps=False)
                info.compress_type = zipfile.ZIP_DEFLATED
                count, digest = 0, hashlib.sha256()
                with path.open("rb") as source, archive.open(info, "w") as destination:
                    while chunk := source.read(updater.CHUNK_BYTES):
                        count += len(chunk)
                        total += len(chunk)
                        if count > updater.MAX_FILE_BYTES or total > updater.MAX_EXPANDED_BYTES:
                            raise ValueError("Release input grew beyond its size limit")
                        destination.write(chunk)
                        digest.update(chunk)
                if count != info.file_size:
                    raise ValueError("Release input changed size while packaging")
                records[relative] = (count, digest.hexdigest())
        verify_archive(temporary, records)
        if collect_files(root) != files:
            raise ValueError("Release input file list changed while packaging")
        for path in files:
            safe_path(root, path)
            count, digest = 0, hashlib.sha256()
            with path.open("rb") as source:
                while chunk := source.read(updater.CHUNK_BYTES):
                    count += len(chunk)
                    if count > updater.MAX_FILE_BYTES:
                        raise ValueError("Release input grew beyond its size limit")
                    digest.update(chunk)
            if (count, digest.hexdigest()) != records[path.relative_to(root).as_posix()]:
                raise ValueError("Release input changed after packaging: " + str(path.relative_to(root)))
        with temporary.open("r+b") as stream:
            stream.flush()
            os.fsync(stream.fileno())
        safe_path(root, output)
        os.replace(temporary, output)
        return output
    finally:
        temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        print(build(args.project_root, args.output))
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print("Release packaging failed: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
