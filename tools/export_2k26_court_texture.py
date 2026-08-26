from __future__ import annotations

import argparse
import ctypes
from pathlib import Path
import struct


DDS_MAGIC = b"DDS "
DDSD_CAPS = 0x1
DDSD_HEIGHT = 0x2
DDSD_WIDTH = 0x4
DDSD_PIXELFORMAT = 0x1000
DDSD_LINEARSIZE = 0x80000
DDPF_FOURCC = 0x4
DDSCAPS_TEXTURE = 0x1000


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Export an NBA 2K26 court .mip0/.tld texture pair to DDS."
    )
    parser.add_argument("--game-root", required=True, help="NBA 2K26 install folder.")
    parser.add_argument("--mip0", required=True, help="Extracted .mip0 texture payload.")
    parser.add_argument("--tld", help="Matching .tld sidecar. Defaults to same stem as --mip0.")
    parser.add_argument("--output", required=True, help="DDS output path.")
    parser.add_argument(
        "--format",
        choices=["auto", "DXT1", "DXT5"],
        default="auto",
        help="DDS block format. Auto uses the .tld mip-chain byte count.",
    )
    args = parser.parse_args()

    game_root = Path(args.game_root)
    mip0_path = Path(args.mip0)
    tld_path = Path(args.tld) if args.tld else mip0_path.with_suffix(".tld")
    output_path = Path(args.output)

    width, height, chain_bytes = read_tld_metadata(tld_path)
    fourcc, top_mip_size = choose_format(width, height, chain_bytes, args.format)
    raw_texture = oodle_decompress(game_root, mip0_path.read_bytes(), top_mip_size)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(make_dds(width, height, fourcc, raw_texture))
    print(
        f"wrote {output_path} ({width}x{height}, {fourcc}, "
        f"{len(raw_texture):,} decompressed bytes)"
    )


def read_tld_metadata(path: Path) -> tuple[int, int, int]:
    data = path.read_bytes()
    if len(data) < 32 or data[:4] != b"TLD ":
        raise SystemExit(f"Not a TLD sidecar: {path}")
    width, height = struct.unpack_from("<HH", data, 16)
    chain_bytes = struct.unpack_from("<I", data, 24)[0]
    if width == 0 or height == 0 or chain_bytes == 0:
        raise SystemExit(f"Could not read texture metadata from: {path}")
    return width, height, chain_bytes


def choose_format(width: int, height: int, chain_bytes: int, requested: str) -> tuple[str, int]:
    formats = {
        "DXT1": 8,
        "DXT5": 16,
    }
    if requested != "auto":
        block_bytes = formats[requested]
        return requested, top_mip_bytes(width, height, block_bytes)

    for fourcc, block_bytes in formats.items():
        if mip_chain_bytes(width, height, block_bytes) == chain_bytes:
            return fourcc, top_mip_bytes(width, height, block_bytes)

    expected = ", ".join(
        f"{fourcc}={mip_chain_bytes(width, height, block_bytes)}"
        for fourcc, block_bytes in formats.items()
    )
    raise SystemExit(
        f"Unknown DDS block format for {width}x{height}, chain bytes {chain_bytes}. "
        f"Expected one of: {expected}"
    )


def top_mip_bytes(width: int, height: int, block_bytes: int) -> int:
    return max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * block_bytes


def mip_chain_bytes(width: int, height: int, block_bytes: int) -> int:
    total = 0
    while True:
        total += top_mip_bytes(width, height, block_bytes)
        if width == 1 and height == 1:
            return total
        width = max(1, width // 2)
        height = max(1, height // 2)


def oodle_decompress(game_root: Path, compressed: bytes, raw_size: int) -> bytes:
    oodle_path = game_root / "data" / "oodle" / "oo2core_9_win64.dll"
    if not oodle_path.exists():
        raise SystemExit(f"Oodle DLL not found: {oodle_path}")

    library = ctypes.WinDLL(str(oodle_path))
    decompress = library.OodleLZ_Decompress
    decompress.restype = ctypes.c_int
    decompress.argtypes = [
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_int,
    ]

    output = ctypes.create_string_buffer(raw_size)
    decoded = decompress(
        compressed,
        len(compressed),
        output,
        raw_size,
        1,
        0,
        0,
        None,
        0,
        None,
        None,
        None,
        0,
        3,
    )
    if decoded != raw_size:
        raise SystemExit(f"Oodle decompressed {decoded} bytes, expected {raw_size}.")
    return output.raw


def make_dds(width: int, height: int, fourcc: str, texture_data: bytes) -> bytes:
    pitch_or_linear_size = len(texture_data)
    header = bytearray(124)
    struct.pack_into("<I", header, 0, 124)
    struct.pack_into(
        "<I",
        header,
        4,
        DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_LINEARSIZE,
    )
    struct.pack_into("<I", header, 8, height)
    struct.pack_into("<I", header, 12, width)
    struct.pack_into("<I", header, 16, pitch_or_linear_size)
    struct.pack_into("<I", header, 72, 32)
    struct.pack_into("<I", header, 76, DDPF_FOURCC)
    header[80:84] = fourcc.encode("ascii")
    struct.pack_into("<I", header, 104, DDSCAPS_TEXTURE)
    return DDS_MAGIC + bytes(header) + texture_data


if __name__ == "__main__":
    main()
