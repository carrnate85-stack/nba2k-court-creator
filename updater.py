"""Stage bounded compatible releases and apply them with serialized rollback."""
from contextlib import contextmanager
from io import BytesIO
import hashlib
import json
import os
import re
import shutil
import stat
import struct
import sys
import tempfile
import time
import zipfile
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent
UPDATES = ROOT / "updates"
ALLOWED = {"desktop", "electron", "court_creator", "tools"}
FILES = {"package.json", "studio-build.json", "requirements.txt", "updater.py", "Launch NBA 2K Court Creator.bat", "Setup Court Creator.bat", "Build Court Creator.bat", "data/team_palettes.json"}
STATIC_FOLDERS = ("data/palette_sources",)
NATIVE_FILES = ("NBA2KCourtCreator.exe", "NBA2KCourtCreator.dll", "NBA2KCourtCreator.deps.json",
                "NBA2KCourtCreator.runtimeconfig.json", "TwoK.Studio.dll")
RELEASE_URL = "https://api.github.com/repos/carrnate85-stack/nba2k-court-creator/releases/latest"
DOWNLOAD_PREFIX = "/carrnate85-stack/nba2k-court-creator/releases/download/"
MAX_JSON_BYTES = 1024 * 1024
MAX_JOURNAL_BYTES = 8 * 1024 * 1024
INVENTORY_NAME = ".update-inventory.json"
MAX_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_DIRECTORY_BYTES = 8 * 1024 * 1024
MAX_ENTRIES = 10000
MAX_FILE_BYTES = 256 * 1024 * 1024
MAX_EXPANDED_BYTES = 1024 * 1024 * 1024
TRANSFER_SECONDS = 120
CHUNK_BYTES = 64 * 1024


class UpdateBusy(RuntimeError):
    pass


class UpdateRecoveryError(RuntimeError):
    pass


def owned_path(path):
    path = Path(path).absolute()
    root = ROOT.resolve()
    if not path.is_relative_to(ROOT.absolute()) or not path.resolve().is_relative_to(root):
        raise ValueError("Update path points outside the application: " + str(path))
    for item in (path, *path.parents):
        if item == ROOT.absolute():
            break
        if item.is_symlink() or (item.exists() and getattr(item.lstat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Linked update paths are not supported: " + str(item))
    if path.is_file() and path.stat().st_nlink > 1:
        raise ValueError("Hard-linked update files are not supported: " + str(path))
    return path


def read_json(path, limit=MAX_JSON_BYTES):
    with owned_path(path).open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("Update metadata exceeds its size limit")
    result = json.loads(data.decode("utf-8-sig"))
    if not isinstance(result, dict):
        raise ValueError("Update metadata must be an object")
    return result


def allowed(relative):
    return bool(relative.parts) and (relative.parts[0] in ALLOWED or relative.as_posix() in FILES
                                    or any(relative.as_posix().startswith(folder + "/") for folder in STATIC_FOLDERS))


def relative_path(name):
    # Reject Windows aliases instead of letting ZIP extraction normalize them.
    if not isinstance(name, str) or not name or len(name) > 240 or "\\" in name or name.startswith("/") or name.endswith("//"):
        raise ValueError("Invalid update path")
    parts = name.rstrip("/").split("/")
    for part in parts:
        if (not part or part in (".", "..") or part[-1:] in (" ", ".")
                or any(ord(character) < 32 or character in ':<>"|?*' for character in part)
                or re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part)):
            raise ValueError("Invalid update path")
    return Path(*parts)


def tree_files(directory):
    directory = owned_path(directory)
    if not directory.is_dir():
        raise ValueError("Expected an update directory: " + str(directory))
    files, total, count = [], 0, 0
    directories = [directory]
    while directories:
        with os.scandir(directories.pop()) as entries:
            for entry in entries:
                count += 1
                if count > MAX_ENTRIES:
                    raise ValueError("Update contains too many entries")
                path = owned_path(Path(entry.path))
                relative_path(path.relative_to(directory).as_posix())
                if entry.is_file(follow_symlinks=False):
                    size = entry.stat(follow_symlinks=False).st_size
                    if size > MAX_FILE_BYTES:
                        raise ValueError("Update file exceeds its size limit")
                    total += size
                    if total > MAX_EXPANDED_BYTES:
                        raise ValueError("Update exceeds its expanded size limit")
                    files.append(path)
                elif entry.is_dir(follow_symlinks=False):
                    directories.append(path)
                else:
                    raise ValueError("Update contains a special file")
    return sorted(files)


def remove_tree(directory):
    tree_files(directory)
    if not directory.absolute().is_relative_to(UPDATES.absolute()) or directory == UPDATES:
        raise ValueError("Refusing to remove an unowned update directory")
    shutil.rmtree(directory)


@contextmanager
def update_lock(*, wait=False, name="operation.lock"):
    owned_path(UPDATES).mkdir(exist_ok=True)
    if name not in ("operation.lock", "download.lock"):
        raise ValueError("Invalid update lock name")
    path = owned_path(UPDATES / name)
    with path.open("a+b", buffering=0) as stream:
        if stream.seek(0, os.SEEK_END) == 0:
            stream.write(b"\0")
        deadline = time.monotonic() + (30 if wait else 0)
        while True:
            stream.seek(0)
            try:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError as error:
                if time.monotonic() >= deadline:
                    raise UpdateBusy("Another Court Creator update is in progress. Try again shortly.") from error
                time.sleep(.05)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def validate_runtime(destination):
    current_marker = owned_path(ROOT / "studio-build.json")
    if not current_marker.exists():
        return
    current = read_json(current_marker)
    marker = owned_path(destination / "studio-build.json")
    candidate = read_json(marker) if marker.exists() else {}
    if any(candidate.get(key) != current.get(key) for key in ("runtime", "projectSchema", "backend", "exchangeSchema") if key in current):
        raise ValueError("This release uses a different desktop runtime; a full installation is required")
    if any(not owned_path(destination / "desktop" / name).is_file() for name in NATIVE_FILES):
        raise ValueError("The native desktop application is incomplete in this update")
    config = read_json(destination / "desktop/NBA2KCourtCreator.runtimeconfig.json").get("runtimeOptions")
    frameworks = config.get("frameworks") if isinstance(config, dict) else None
    if (not isinstance(frameworks, list) or len(frameworks) != 2
            or any(not isinstance(item, dict) or item.get("version") != "8.0.0" or not isinstance(item.get("name"), str) for item in frameworks)
            or {item.get("name") for item in frameworks} != {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}):
        raise ValueError("Unsupported native desktop runtime configuration")
    requirements = owned_path(destination / "requirements.txt")
    if not requirements.is_file() or requirements.stat().st_size > MAX_JSON_BYTES:
        raise ValueError("Python dependency metadata is missing or oversized")
    current_requirements = owned_path(ROOT / "requirements.txt")
    if current_requirements.stat().st_size > MAX_JSON_BYTES or requirements.read_bytes() != current_requirements.read_bytes():
        raise ValueError("Python dependencies changed; run a full setup update")


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r"v?\d{1,6}(?:\.\d{1,6}){1,3}", value):
        raise ValueError("Invalid update version")
    parts = tuple(int(part) for part in value.removeprefix("v").split("."))
    return parts + (0,) * (4 - len(parts))


def transfer(url, target, limit):
    address = urlsplit(url)
    if address.scheme != "https" or address.hostname not in ("api.github.com", "github.com") or address.username or address.password:
        raise ValueError("Update downloads must use GitHub HTTPS")
    deadline = time.monotonic() + TRANSFER_SECONDS
    count = 0
    with urlopen(Request(url, headers={"User-Agent": "CourtCreator-Updater", "Accept-Encoding": "identity"}), timeout=15) as response:
        final = urlsplit(response.geturl())
        if final.scheme != "https" or final.username or final.password or final.hostname not in ("api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"):
            raise ValueError("Invalid update download redirect")
        length = response.headers.get("Content-Length")
        if length is not None and (not length.isdecimal() or int(length) > limit):
            raise ValueError("Update download exceeds its size limit")
        while True:
            if time.monotonic() > deadline:
                raise TimeoutError("Update download exceeded its total time limit")
            chunk = response.read(min(CHUNK_BYTES, limit - count + 1))
            if time.monotonic() > deadline:
                raise TimeoutError("Update download exceeded its total time limit")
            if not chunk:
                break
            count += len(chunk)
            if count > limit:
                raise ValueError("Update download exceeds its size limit")
            target.write(chunk)
        if length is not None and count != int(length):
            raise ValueError("Incomplete update download")
    return count


def fetch(url):
    target = BytesIO()
    transfer(url, target, MAX_JSON_BYTES)
    return target.getvalue()


class DirectoryReader:
    def __init__(self, stream):
        self.stream, self.inspecting = stream, True

    def __getattr__(self, name):
        return getattr(self.stream, name)

    def read(self, size=-1):
        if self.inspecting and (size < 0 or size > MAX_DIRECTORY_BYTES):
            raise ValueError("Update ZIP directory exceeds its size limit")
        data = self.stream.read(size)
        if self.inspecting and data.startswith(zipfile.stringCentralDir):
            cursor = count = 0
            while cursor + zipfile.sizeCentralDir <= len(data):
                header = struct.unpack_from(zipfile.structCentralDir, data, cursor)
                if header[zipfile._CD_SIGNATURE] != zipfile.stringCentralDir:
                    break
                count += 1
                if count > MAX_ENTRIES:
                    raise ValueError("Update contains too many entries")
                cursor += zipfile.sizeCentralDir + header[zipfile._CD_FILENAME_LENGTH] + header[zipfile._CD_EXTRA_FIELD_LENGTH] + header[zipfile._CD_COMMENT_LENGTH]
        return data


def extract_archive(path, destination):
    if path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError("Update archive exceeds its size limit")
    with path.open("rb") as stream:
        reader = DirectoryReader(stream)
        with zipfile.ZipFile(reader) as archive:
            reader.inspecting = False
            entries, names, total = [], {}, 0
            for member in archive.infolist():
                relative = relative_path(member.orig_filename)
                key = relative.as_posix().casefold()
                kind = stat.S_IFMT(member.external_attr >> 16)
                if member.flag_bits & 1 or kind not in (0, stat.S_IFREG, stat.S_IFDIR) or member.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    raise ValueError("Unsupported update ZIP entry")
                if key in names:
                    raise ValueError("Duplicate update ZIP path")
                names[key] = member.is_dir()
                if member.file_size > MAX_FILE_BYTES:
                    raise ValueError("Update file exceeds its size limit")
                total += member.file_size
                if total > MAX_EXPANDED_BYTES or len(names) > MAX_ENTRIES:
                    raise ValueError("Update exceeds its size or entry limit")
                entries.append((member, relative))
            for key in names:
                parent = key.rpartition("/")[0]
                while parent:
                    if names.get(parent) is False:
                        raise ValueError("Update ZIP file/directory collision")
                    parent = parent.rpartition("/")[0]
            materialized = set()
            for member, relative in entries:
                if not allowed(relative):
                    continue
                name = relative.as_posix().casefold()
                while name:
                    materialized.add(name)
                    if len(materialized) + 1 > MAX_ENTRIES:
                        raise ValueError("Update creates too many filesystem entries")
                    name = name.rpartition("/")[0]
            records = []
            for member, relative in entries:
                if not allowed(relative):
                    continue
                target = owned_path(destination / relative)
                if member.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                count = 0
                digest = hashlib.sha256()
                with archive.open(member) as source, target.open("xb") as output:
                    while chunk := source.read(min(CHUNK_BYTES, member.file_size - count + 1)):
                        count += len(chunk)
                        if count > member.file_size or count > MAX_FILE_BYTES:
                            raise ValueError("Update entry expanded beyond its declared size")
                        output.write(chunk)
                        digest.update(chunk)
                    if count != member.file_size:
                        raise ValueError("Incomplete update entry")
                records.append({"path": relative.as_posix(), "bytes": count, "revision": digest.hexdigest()})
            return records


def recover_pending():
    pending, previous = owned_path(UPDATES / "pending"), owned_path(UPDATES / "pending.previous")
    if previous.exists() and not pending.exists():
        validate_previous(previous)
        validate_runtime(previous)
        os.replace(previous, pending)
    return pending, previous


def validate_previous(directory):
    files = tree_files(directory)
    if any(not allowed(path.relative_to(directory)) and path.relative_to(directory).as_posix() != INVENTORY_NAME for path in files):
        raise ValueError("Unrecognized previous update files were preserved")
    version(read_json(directory / "package.json").get("version"))
    verify_inventory(directory, files)


def publish_pending(destination):
    pending, previous = recover_pending()
    if pending.exists():
        tree_files(pending)
        validate_runtime(pending)
    if previous.exists():
        validate_previous(previous)
        validate_runtime(previous)
        remove_tree(previous)
    if pending.exists():
        os.replace(pending, previous)
    try:
        os.replace(destination, pending)
    except Exception:
        if previous.exists() and not pending.exists():
            os.replace(previous, pending)
        raise
    if previous.exists():
        remove_tree(previous)


def stage():
    if (ROOT / ".git").exists():
        return
    with update_lock(name="download.lock"):
        with update_lock():
            if (UPDATES / "apply-journal.json").exists():
                raise UpdateRecoveryError("An interrupted update must be recovered by the desktop launcher before another download.")
            current = read_json(ROOT / "package.json")
        release = json.loads(fetch(RELEASE_URL))
        if not isinstance(release, dict) or not isinstance(release.get("assets"), list):
            raise ValueError("Invalid release metadata")
        if version(release.get("tag_name")) <= version(current.get("version")):
            return
        asset = next((item for item in release["assets"] if isinstance(item, dict) and item.get("name") == "court-creator-update.zip"), None)
        if not asset:
            raise RuntimeError("Release requires a full installation")
        address = urlsplit(asset.get("browser_download_url", ""))
        if address.scheme != "https" or address.netloc != "github.com" or address.path != DOWNLOAD_PREFIX + release["tag_name"] + "/court-creator-update.zip" or address.query or address.fragment:
            raise ValueError("Invalid release asset URL")
        size = asset.get("size")
        if type(size) is not int or size <= 0 or size > MAX_ARCHIVE_BYTES:
            raise ValueError("Invalid release archive size")
        with tempfile.TemporaryDirectory(prefix="stage-", dir=UPDATES) as temporary:
            archive_path = Path(temporary) / "release.zip"
            with archive_path.open("xb") as output:
                if transfer(asset["browser_download_url"], output, size) != size:
                    raise ValueError("Release archive size mismatch")
            destination = Path(temporary) / "staged"
            destination.mkdir()
            records = extract_archive(archive_path, destination)
            manifest = read_json(destination / "package.json")
            if version(manifest.get("version")) != version(release["tag_name"]):
                raise ValueError("Release version mismatch")
            seal_inventory(destination, records)
            verify_inventory(destination)
            with update_lock(wait=True):
                if (UPDATES / "apply-journal.json").exists():
                    raise UpdateRecoveryError("An interrupted update must be recovered before publishing a pending release.")
                current = read_json(ROOT / "package.json")
                if version(manifest["version"]) <= version(current.get("version")):
                    return
                validate_runtime(destination)
                if manifest.get("devDependencies") != current.get("devDependencies"):
                    raise ValueError("This release requires a full installation")
                publish_pending(destination)


def replace_copy(source, target, expected_revision=None):
    owned_path(source)
    owned_path(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".court-update-", suffix=".tmp", dir=target.parent)
    staged = Path(temporary)
    try:
        with os.fdopen(descriptor, "wb") as output, source.open("rb") as incoming:
            count = 0
            digest = hashlib.sha256()
            while chunk := incoming.read(CHUNK_BYTES):
                count += len(chunk)
                if count > MAX_FILE_BYTES:
                    raise ValueError("Update file exceeds its size limit")
                output.write(chunk)
                digest.update(chunk)
            if expected_revision is not None and digest.hexdigest() != expected_revision:
                raise ValueError("Update file changed while it was being copied")
            output.flush()
            os.fsync(output.fileno())
        shutil.copystat(source, staged)
        owned_path(target)
        os.replace(staged, target)
    finally:
        staged.unlink(missing_ok=True)


def file_fingerprint(path):
    path = owned_path(path)
    digest, count = hashlib.sha256(), 0
    with path.open("rb") as stream:
        while chunk := stream.read(CHUNK_BYTES):
            count += len(chunk)
            if count > MAX_FILE_BYTES:
                raise ValueError("Update file exceeds its size limit")
            digest.update(chunk)
    return count, digest.hexdigest()


def file_revision(path):
    return file_fingerprint(path)[1]


def write_metadata(target, document, *, prefix):
    data = json.dumps(document, separators=(",", ":"), allow_nan=False).encode("utf-8")
    if len(data) > MAX_JOURNAL_BYTES:
        raise ValueError("Update metadata exceeds its size limit")
    target = owned_path(target)
    descriptor, name = tempfile.mkstemp(prefix=prefix, suffix=".tmp", dir=target.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        owned_path(target)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def write_journal(document):
    write_metadata(UPDATES / "apply-journal.json", document, prefix="journal-")


def seal_inventory(directory, records=None):
    directory = owned_path(directory)
    if records is None:
        records = []
        for path in tree_files(directory):
            relative = path.relative_to(directory)
            if allowed(relative):
                size, revision = file_fingerprint(path)
                records.append({"path": relative.as_posix(), "bytes": size, "revision": revision})
    release_version = read_json(directory / "package.json").get("version")
    version(release_version)
    write_metadata(directory / INVENTORY_NAME, {"version": 1, "releaseVersion": release_version, "files": records}, prefix="inventory-")


def verify_inventory(directory, files=None):
    path = owned_path(directory / INVENTORY_NAME)
    if not path.is_file():
        if (ROOT / "studio-build.json").exists() or (directory / "studio-build.json").exists():
            raise ValueError("This pending update has no integrity inventory. Download it again before installation.")
        return None
    document = read_json(path, MAX_JOURNAL_BYTES)
    records = document.get("files")
    if type(document.get("version")) is not int or document["version"] != 1 or not isinstance(records, list) or not 0 < len(records) <= MAX_ENTRIES:
        raise ValueError("Invalid update integrity inventory")
    if version(document.get("releaseVersion")) != version(read_json(directory / "package.json").get("version")):
        raise ValueError("Update inventory version mismatch")
    expected = {}
    for record in records:
        if not isinstance(record, dict) or set(record) != {"path", "bytes", "revision"}:
            raise ValueError("Invalid update integrity entry")
        relative = relative_path(record["path"])
        name = relative.as_posix().casefold()
        if record["path"] != relative.as_posix() or not allowed(relative) or name in expected:
            raise ValueError("Invalid update integrity path")
        if type(record["bytes"]) is not int or not 0 <= record["bytes"] <= MAX_FILE_BYTES or not isinstance(record["revision"], str) or not re.fullmatch("[0-9a-f]{64}", record["revision"]):
            raise ValueError("Invalid update integrity fingerprint")
        expected[name] = record
    actual = {}
    for source in tree_files(directory) if files is None else files:
        relative = source.relative_to(directory)
        if allowed(relative):
            name = relative.as_posix().casefold()
            if name in actual:
                raise ValueError("Duplicate pending update path")
            actual[name] = source
    if expected.keys() != actual.keys():
        raise ValueError("The pending update file list changed. Download it again.")
    for name, source in actual.items():
        size, revision = file_fingerprint(source)
        if size != expected[name]["bytes"] or revision != expected[name]["revision"]:
            raise ValueError("A pending update file changed: " + str(source.relative_to(directory)) + ". Download it again.")
    return expected


def recover_application():
    journal = UPDATES / "apply-journal.json"
    if not journal.exists():
        return
    try:
        document = read_json(journal, MAX_JOURNAL_BYTES)
        entries = document.get("files")
        state = document.get("state")
        if type(document.get("version")) is not int or document["version"] != 1 or state not in ("prepared", "committed") or not isinstance(entries, list) or not 0 < len(entries) <= MAX_ENTRIES:
            raise ValueError("Invalid recovery journal")
        names, targets = set(), []
        for entry in entries:
            if not isinstance(entry, dict) or type(entry.get("existed")) is not bool:
                raise ValueError("Invalid recovery file entry")
            relative = relative_path(entry.get("path"))
            name = relative.as_posix().casefold()
            if not allowed(relative) or name in names:
                raise ValueError("Invalid recovery path")
            names.add(name)
            old, new = entry.get("oldRevision"), entry.get("newRevision")
            if not isinstance(new, str) or not re.fullmatch("[0-9a-f]{64}", new) or (entry["existed"] and (not isinstance(old, str) or not re.fullmatch("[0-9a-f]{64}", old))) or (not entry["existed"] and old is not None):
                raise ValueError("Invalid recovery fingerprints")
            target = owned_path(ROOT / relative)
            current = file_revision(target) if target.exists() else None
            if state == "committed":
                if current != new:
                    raise ValueError("A committed update file changed: " + str(relative))
            else:
                if current not in (old, new, None):
                    raise ValueError("A recovery target has unrelated edits: " + str(relative))
                if entry["existed"] and file_revision(UPDATES / "rollback" / relative) != old:
                    raise ValueError("A rollback file changed: " + str(relative))
            targets.append((entry, target))
        if state == "prepared":
            for entry, target in reversed(targets):
                current = file_revision(target) if target.exists() else None
                if current == entry["oldRevision"]:
                    continue
                if current not in (entry["newRevision"], None):
                    raise ValueError("A recovery target changed during rollback")
                if entry["existed"]:
                    replace_copy(UPDATES / "rollback" / entry["path"], target, entry["oldRevision"])
                elif current is not None:
                    owned_path(target).unlink()
        else:
            pending = UPDATES / "pending"
            if pending.exists():
                remove_tree(pending)
        owned_path(journal).unlink()
    except Exception as error:
        raise UpdateRecoveryError("Update rollback needs attention; do not start the app. " + str(error)) from error


def apply():
    if (ROOT / ".git").exists():
        return
    if not any((UPDATES / name).exists() for name in ("pending", "pending.previous", "apply-journal.json")):
        return
    with update_lock(wait=True):
        recover_application()
        pending, _ = recover_pending()
        if not pending.exists():
            return
        sources = tree_files(pending)
        validate_runtime(pending)
        if (ROOT / "package.json").is_file():
            if version(read_json(pending / "package.json").get("version")) <= version(read_json(ROOT / "package.json").get("version")):
                remove_tree(pending)
                return
        verified = verify_inventory(pending, sources)
        changes = [(source, ROOT / source.relative_to(pending)) for source in sources if allowed(source.relative_to(pending))]
        for source, target in changes:
            owned_path(target)
            if target.exists() and not target.is_file():
                raise ValueError("Update target is not a regular file")
        backup = owned_path(UPDATES / "rollback")
        if backup.exists():
            if any(not allowed(path.relative_to(backup)) for path in tree_files(backup)):
                raise ValueError("Unrecognized rollback files were preserved")
            remove_tree(backup)
        backup.mkdir(parents=True)
        plan = []
        for source, target in changes:
            relative = source.relative_to(pending)
            existed = target.exists()
            old = file_revision(target) if existed else None
            new = verified[relative.as_posix().casefold()]["revision"] if verified is not None else file_revision(source)
            if existed:
                replace_copy(target, backup / relative, old)
            plan.append({"path": relative.as_posix(), "existed": existed, "oldRevision": old, "newRevision": new})
        if not plan:
            raise ValueError("The update contains no application files")
        write_journal({"version": 1, "state": "prepared", "files": plan})
        try:
            for (source, target), entry in zip(changes, plan):
                current = file_revision(target) if target.exists() else None
                if current != entry["oldRevision"]:
                    raise ValueError("An installed file changed during update preparation")
                replace_copy(source, target, entry["newRevision"])
            write_journal({"version": 1, "state": "committed", "files": plan})
        except Exception:
            recover_application()
            raise
        remove_tree(pending)
        owned_path(UPDATES / "apply-journal.json").unlink()


def main(argv=None):
    arguments = sys.argv[1:] if argv is None else argv
    applying = "--apply" in arguments
    try:
        apply() if applying else stage()
        return 0
    except UpdateBusy as error:
        if not applying:
            return 0
        print(str(error), file=sys.stderr)
        return 2
    except Exception as error:
        try:
            owned_path(UPDATES).mkdir(exist_ok=True)
            path = owned_path(UPDATES / "last-error.txt")
            path.write_text(str(error)[:4000], encoding="utf-8")
        except Exception:
            pass
        print(str(error), file=sys.stderr)
        return 3 if isinstance(error, UpdateRecoveryError) else 1


if __name__ == "__main__":
    raise SystemExit(main())
