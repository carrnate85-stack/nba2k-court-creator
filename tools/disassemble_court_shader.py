"""Local diagnostic using Microsoft's IDxcCompiler3 disassembly interface."""
import argparse
import ctypes
from pathlib import Path
import uuid
from zipfile import ZipFile


class DxcBuffer(ctypes.Structure):
    _fields_ = [("pointer", ctypes.c_void_p), ("size", ctypes.c_size_t), ("encoding", ctypes.c_uint)]


def disassemble(data: bytes, compiler_dll: Path) -> str:
    dll = ctypes.WinDLL(str(compiler_dll))
    create = dll.DxcCreateInstance
    create.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    create.restype = ctypes.c_long

    def guid(value):
        return ctypes.create_string_buffer(uuid.UUID(value).bytes_le)

    def call(pointer, index, result_type, argument_types, *args):
        table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        method = ctypes.WINFUNCTYPE(result_type, ctypes.c_void_p, *argument_types)(table[index])
        return method(pointer, *args)

    def check(result):
        if result < 0:
            raise RuntimeError(f"DXC operation failed: 0x{result & 0xffffffff:08x}")

    compiler, result, blob = ctypes.c_void_p(), ctypes.c_void_p(), ctypes.c_void_p()
    check(create(guid("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0"), guid("228b4687-5a6a-4730-900c-9702b2203f54"), ctypes.byref(compiler)))
    try:
        storage = ctypes.create_string_buffer(data)
        buffer = DxcBuffer(ctypes.cast(storage, ctypes.c_void_p), len(data), 0)
        check(call(compiler, 4, ctypes.c_long, [ctypes.POINTER(DxcBuffer), ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)],
                   ctypes.byref(buffer), guid("58346cda-dde7-4497-9461-6f87af5e0659"), ctypes.byref(result)))
        status = ctypes.c_long()
        check(call(result, 3, ctypes.c_long, [ctypes.POINTER(ctypes.c_long)], ctypes.byref(status)))
        check(status.value)
        check(call(result, 4, ctypes.c_long, [ctypes.POINTER(ctypes.c_void_p)], ctypes.byref(blob)))
        pointer = call(blob, 3, ctypes.c_void_p, [])
        size = call(blob, 4, ctypes.c_size_t, [])
        return ctypes.string_at(pointer, size).rstrip(b"\x00").decode("utf-8")
    finally:
        for pointer in (blob, result, compiler):
            if pointer:
                call(pointer, 2, ctypes.c_uint, [])


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--iff", type=Path, required=True)
    parser.add_argument("--dxc-dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    with ZipFile(args.iff) as archive:
        for name in archive.namelist():
            if name.endswith(".shader"):
                destination = args.output / (name + ".txt")
                destination.write_text(disassemble(archive.read(name), args.dxc_dll), encoding="utf-8")
                print(destination.name)
