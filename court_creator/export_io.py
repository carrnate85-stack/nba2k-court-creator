"""Publish complete exports without replacing their input assets."""
from contextlib import contextmanager
import os
from pathlib import Path
import stat
import tempfile
import time


def ensure_new_export(output_path: Path, *sources: Path) -> None:
    output = Path(output_path)
    resolved = output.resolve()
    for source in sources:
        source = Path(source)
        if resolved == source.resolve() or output.exists() and source.exists() and output.samefile(source):
            raise ValueError("Save the export as a new file; source assets must stay unchanged.")


def _publish_export(staging, output, sources, validate=None, *, identity=None):
    if identity is None:
        identity = staging_identity(staging)
    for attempt in range(4):
        try:
            ensure_new_export(output, *sources)
            owned_staging(staging, identity)
            if validate is not None:
                validate()
            ensure_new_export(output, *sources)
            owned_staging(staging, identity)
            os.replace(staging, output)
            return
        except OSError as error:
            if os.name != "nt" or getattr(error, "winerror", None) not in (5, 32, 33) or attempt == 3 or not staging.is_file():
                raise
            time.sleep(.025 * 2 ** attempt)


def staging_identity(staging: Path):
    info = staging.lstat()
    return info.st_dev, info.st_ino


def owned_staging(staging: Path, identity, *, info=None):
    info = staging.lstat() if info is None else info
    reparse = getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024)
    if not stat.S_ISREG(info.st_mode) or reparse or info.st_nlink != 1 or (info.st_dev, info.st_ino) != identity:
        raise OSError(f"Temporary-file identity changed or became shared/linked; refusing {staging}.")
    return info


def cleanup_staging(staging: Path, failure: BaseException | None = None, *, identity=None, validate=None):
    for attempt in range(4):
        try:
            try:
                info = staging.lstat()
            except FileNotFoundError:
                return
            if identity is None:
                identity = info.st_dev, info.st_ino
            owned_staging(staging, identity, info=info)
            if validate is not None:
                validate()
                owned_staging(staging, identity)
            staging.unlink(missing_ok=True)
            return
        except (OSError, ValueError) as error:
            if os.name == "nt" and getattr(error, "winerror", None) in (5, 32, 33) and attempt < 3:
                time.sleep(.025 * 2 ** attempt)
                continue
            if failure is None:
                raise
            failure.add_note(f"Temporary-file cleanup failed; retained {staging}: {error}")
            return


@contextmanager
def staged_export(output_path: Path, *, sources=(), validate=None, writable_stream=False, validate_cleanup=None):
    """Yield an owned path or its borrowed creation handle for stream writers."""
    output = Path(output_path)
    sources = tuple(Path(source) for source in sources)
    ensure_new_export(output, *sources)
    output.parent.mkdir(parents=True, exist_ok=True)
    stream = tempfile.NamedTemporaryFile(prefix=f".{output.name}.", suffix=".tmp", dir=output.parent, delete=False)
    staging = Path(stream.name)
    info = os.fstat(stream.fileno())
    identity = info.st_dev, info.st_ino
    failure = None
    try:
        owned_staging(staging, identity, info=info)
        owned_staging(staging, identity)
        if not writable_stream:
            stream.close()
        yield stream if writable_stream else staging
        owned_staging(staging, identity)
        if writable_stream:
            owned_staging(staging, identity, info=os.fstat(stream.fileno()))
            stream.flush()
            os.fsync(stream.fileno())
            stream.close()
        else:
            with staging.open("r+b") as sync:
                owned_staging(staging, identity, info=os.fstat(sync.fileno()))
                sync.flush()
                os.fsync(sync.fileno())
        _publish_export(staging, output, sources, validate, identity=identity)
    except BaseException as error:
        failure = error
        raise
    finally:
        close_error = None
        try:
            stream.close()
        except BaseException as error:
            if failure is None:
                failure = close_error = error
            else:
                failure.add_note(f"Temporary-file close failed for {staging}: {error}")
        cleanup_staging(staging, failure, identity=identity, validate=validate_cleanup)
        if close_error is not None:
            raise close_error
