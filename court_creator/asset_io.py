"""Keep image decoding tied to the original bytes used for native previews."""
from contextlib import contextmanager
import hashlib
import os
from pathlib import Path
import re


MAX_ASSET_BYTES = 512 * 1024 * 1024


def asset_revision(item: dict) -> str | None:
    if "sourceRevision" not in item:
        return None
    revision = item["sourceRevision"]
    if not isinstance(revision, str) or re.fullmatch(r"[0-9a-f]{64}", revision) is None:
        raise ValueError("Invalid artwork source revision.")
    return revision


def _stream_revision(stream) -> str:
    stream.seek(0)
    digest = hashlib.sha256()
    total = 0
    while chunk := stream.read(256 * 1024):
        total += len(chunk)
        if total > MAX_ASSET_BYTES:
            raise ValueError("Artwork exceeds the 512 MB file size limit.")
        digest.update(chunk)
    return digest.hexdigest()


@contextmanager
def verified_asset_stream(path: Path, revision: str | None = None):
    path = Path(path)
    # Decoder seeks must not let the post-decode hash reuse prefetched source bytes.
    with path.open("rb", buffering=0) as stream:
        if os.fstat(stream.fileno()).st_size > MAX_ASSET_BYTES:
            raise ValueError("Artwork exceeds the 512 MB file size limit.")
        if revision is not None and _stream_revision(stream) != revision:
            raise ValueError("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before exporting.")
        stream.seek(0)
        yield stream
        if revision is not None and _stream_revision(stream) != revision:
            raise ValueError("Artwork changed while it was being decoded. Refresh the court or reimport the logo before exporting.")


def validate_asset_image(image) -> None:
    if image.width > 16384 or image.height > 16384 or image.width * image.height > 64 * 1024 * 1024:
        raise ValueError("Artwork is too large. Use an image up to 16384 pixels per side and 64 megapixels.")
