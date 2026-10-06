"""Publish complete custom floors and catalog updates without overwriting artwork."""
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import stat
import tempfile
import threading

from PIL import Image

from .asset_io import MAX_ASSET_BYTES, _stream_revision, validate_asset_image, verified_asset_stream
from .court_import import _base_file_lock
from .export_io import cleanup_staging, owned_staging, staged_export, staging_identity
from .json_io import read_document_snapshot


_IMPORT_LOCK = threading.Lock()


@dataclass(frozen=True)
class CopiedFloor:
    path: Path
    revision: str
    identity: tuple


def _identity(path):
    info = path.lstat()
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns


class CustomFloorStore:
    def __init__(self, root, directory, metadata):
        self.root = Path(root).resolve()
        self.directory = Path(directory).absolute()
        self.metadata = Path(metadata).absolute()

    def validate(self):
        if self.metadata.parent != self.directory or not self.directory.is_relative_to(self.root):
            raise ValueError("Custom-floor storage must stay inside the application folder.")
        current = self.directory
        while current != self.root:
            self._ordinary(current, directory=True)
            current = current.parent
        if not self.directory.resolve().is_relative_to(self.root) or not self.metadata.resolve().is_relative_to(self.root):
            raise ValueError("Custom-floor storage must stay inside the application folder.")
        self._ordinary(self.metadata)
        self._ordinary(self.metadata.with_name(self.metadata.name + ".lock"))

    @staticmethod
    def _ordinary(path, *, directory=False):
        try:
            info = path.lstat()
        except FileNotFoundError:
            return
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
            raise ValueError("Custom-floor storage cannot use linked files or folders.")
        if directory:
            if not stat.S_ISDIR(info.st_mode):
                raise ValueError("Custom-floor storage is not a directory.")
        elif not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise ValueError("The custom-floor catalog/lock must be an ordinary unshared file.")

    def snapshot(self):
        self.validate()
        try:
            metadata, revision = read_document_snapshot(self.metadata)
        except FileNotFoundError:
            return {"floors": []}, None
        if not isinstance(metadata, dict) or not isinstance(metadata.get("floors", []), list):
            raise ValueError("The existing custom-floor catalog must contain a floors array. Its file was left unchanged.")
        return metadata, revision

    @contextmanager
    def locked(self):
        self.validate()
        if not _IMPORT_LOCK.acquire(timeout=10):
            raise RuntimeError("Another custom-floor import is busy. Try again shortly.")
        try:
            self.validate()
            with _base_file_lock(self.metadata, timeout=10):
                self.validate()
                yield
        finally:
            _IMPORT_LOCK.release()

    def entry_paths(self, metadata):
        paths = set()
        for item in metadata.get("floors", []):
            if not isinstance(item, dict) or not isinstance(item.get("path"), str) or not item["path"]:
                continue
            try:
                path = Path(item["path"])
                paths.add((path if path.is_absolute() else self.root / path).resolve())
            except (OSError, ValueError, RuntimeError):
                continue
        return paths

    def copy_image(self, source, stem, suffix, *, reserved=()):
        self.validate()
        self.directory.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(prefix=".court-floor-import-", suffix=".tmp", dir=self.directory, delete=False) as stream:
            staging = Path(stream.name)
            identity = staging_identity(staging)
        failure = None
        try:
            digest = hashlib.sha256(); total = 0
            with verified_asset_stream(source) as incoming, staging.open("wb") as outgoing:
                while chunk := incoming.read(1024 * 1024):
                    total += len(chunk)
                    if total > MAX_ASSET_BYTES:
                        raise ValueError("Artwork exceeds the 512 MB file size limit.")
                    outgoing.write(chunk); digest.update(chunk)
                outgoing.flush(); os.fsync(outgoing.fileno())
                revision = digest.hexdigest()
                if _stream_revision(incoming) != revision:
                    raise ValueError("The custom floor changed while it was being imported. Try again.")
            with Image.open(staging) as image:
                validate_asset_image(image); image.load()
            counter = 1
            while True:
                self.validate()
                owned_staging(staging, identity)
                name = stem if counter == 1 else f"{stem}-{counter}"
                destination = self.directory / (name + suffix)
                if os.path.lexists(destination) or destination in reserved:
                    counter += 1; continue
                try:
                    if os.name == "nt":
                        os.rename(staging, destination)
                    else:
                        os.link(staging, destination); staging.unlink()
                    return CopiedFloor(destination, revision, _identity(destination))
                except FileExistsError:
                    counter += 1
        except BaseException as error:
            failure = error
            raise
        finally:
            cleanup_staging(staging, failure, identity=identity)

    def publish(self, payload, expected_revision, *, sources=()):
        def validate():
            _metadata, actual = self.snapshot()
            if actual != expected_revision:
                raise ValueError("The custom-floor catalog changed during import. Retry to use the updated catalog.")
        validate()
        try:
            with staged_export(self.metadata, sources=sources, validate=validate) as staging:
                staging.write_bytes(payload)
        except Exception:
            # A rename can commit before an OS wrapper reports an error. Keep its referenced image.
            try:
                _metadata, actual = self.snapshot()
                if actual == hashlib.sha256(payload).hexdigest():
                    return
            except (OSError, ValueError):
                pass
            raise

    def discard(self, copied):
        try:
            self.validate()
            metadata, _revision = self.snapshot()
            if copied.path in self.entry_paths(metadata):
                return False
            self._ordinary(copied.path)
            if _identity(copied.path) != copied.identity:
                return False
            with copied.path.open("rb") as stream:
                if _stream_revision(stream) != copied.revision:
                    return False
            copied.path.unlink()
            return True
        except FileNotFoundError:
            return True
