"""Describe the exact owned PNG handed from the Python worker to WPF."""
import ctypes
import hashlib
import os
from pathlib import Path

from .asset_io import MAX_ASSET_BYTES
from .export_io import owned_staging


def _handle_identity(stream, info):
    if os.name != "nt":
        return {"device": info.st_dev, "index": info.st_ino}
    import msvcrt

    class FileInformation(ctypes.Structure):
        _fields_ = [("attributes", ctypes.c_uint32),
                    ("times", ctypes.c_uint32 * 6),
                    ("volume", ctypes.c_uint32), ("size_high", ctypes.c_uint32),
                    ("size_low", ctypes.c_uint32), ("links", ctypes.c_uint32),
                    ("high", ctypes.c_uint32), ("low", ctypes.c_uint32)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    inspect = kernel.GetFileInformationByHandle
    inspect.argtypes = [ctypes.c_void_p, ctypes.POINTER(FileInformation)]
    inspect.restype = ctypes.c_int
    information = FileInformation()
    if not inspect(msvcrt.get_osfhandle(stream.fileno()), ctypes.byref(information)):
        raise ctypes.WinError(ctypes.get_last_error())
    if information.links != 1 or information.attributes & (0x10 | 0x400):
        raise ValueError("The preview file became shared or linked.")
    return {"volume": information.volume, "high": information.high, "low": information.low}


def stream_receipt(stream) -> dict:
    """Hash the creation handle, not a later replacement at the published path."""
    stream.flush()
    before = os.fstat(stream.fileno())
    identity = before.st_dev, before.st_ino
    path = Path(stream.name)
    owned_staging(path, identity, info=before)
    owned_staging(path, identity)
    if before.st_size < 1 or before.st_size > MAX_ASSET_BYTES:
        raise ValueError("Preview file size is outside the supported limit.")
    handle_identity = _handle_identity(stream, before)
    position = stream.tell()
    digest = hashlib.sha256()
    total = 0
    try:
        stream.seek(0)
        while chunk := stream.read(256 * 1024):
            total += len(chunk)
            if total > MAX_ASSET_BYTES:
                raise ValueError("Preview file exceeds the 512 MB limit.")
            digest.update(chunk)
    finally:
        stream.seek(position)
    after = os.fstat(stream.fileno())
    owned_staging(path, identity, info=after)
    owned_staging(path, identity)
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns) or total != before.st_size:
        raise ValueError("Preview file changed while its receipt was being created.")
    if _handle_identity(stream, after) != handle_identity:
        raise ValueError("Preview file identity changed while creating its receipt.")
    return {"version": 1, "sha256": digest.hexdigest(), "bytes": total, "identity": handle_identity}


def validate_receipt(path: Path, expected: dict) -> None:
    with Path(path).open("rb", buffering=0) as stream:
        if stream_receipt(stream) != expected:
            raise ValueError("Preview file changed before publication or cleanup.")
