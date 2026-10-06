"""Bound local JSON inputs before parsing or using request contents."""
import codecs
import hashlib
import json
import math
import os
from pathlib import Path


MAX_METADATA_BYTES = 8 * 1024 * 1024
MAX_REQUEST_BYTES = 16 * 1024 * 1024
MAX_JSON_DEPTH = 32


def _reject_constant(_value):
    raise ValueError("JSON numbers must be finite.")


def _finite_float(text):
    value = float(text)
    if not math.isfinite(value):
        raise ValueError("JSON numbers must be finite.")
    return value


def _check_depth(document):
    stack = [(iter((document,)), 0)]
    while stack:
        values, parent_depth = stack[-1]
        try:
            value = next(values)
        except StopIteration:
            stack.pop()
            continue
        if isinstance(value, (dict, list)):
            depth = parent_depth + 1
            if depth > MAX_JSON_DEPTH:
                raise ValueError(f"JSON nesting exceeds the {MAX_JSON_DEPTH}-level limit.")
            stack.append((iter(value.values() if isinstance(value, dict) else value), depth))


def _read_bytes(path, max_bytes):
    if type(max_bytes) is not int or max_bytes <= 0:
        raise ValueError("A positive JSON byte limit is required.")
    with Path(path).open("rb") as stream:
        if os.fstat(stream.fileno()).st_size > max_bytes:
            raise ValueError(f"JSON file exceeds the {max_bytes / (1024 * 1024):g} MiB size limit.")
        data = stream.read(max_bytes + 1)
    if len(data) > max_bytes:
        raise ValueError(f"JSON file exceeds the {max_bytes / (1024 * 1024):g} MiB size limit.")
    return data


def _parse_document(data, *, encoding=None):
    if encoding is None:
        encoding = "utf-16" if data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)) else "utf-8-sig"
    try:
        document = json.loads(data.decode(encoding), parse_constant=_reject_constant, parse_float=_finite_float)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ValueError("Invalid JSON file: " + str(error)) from error
    _check_depth(document)
    return document


def parse_document(data, *, max_bytes=MAX_METADATA_BYTES, encoding=None):
    if not isinstance(data, (bytes, bytearray)):
        raise ValueError("JSON input must be bytes.")
    if type(max_bytes) is not int or max_bytes <= 0:
        raise ValueError("A positive JSON byte limit is required.")
    if len(data) > max_bytes:
        raise ValueError(f"JSON input exceeds the {max_bytes / (1024 * 1024):g} MiB size limit.")
    return _parse_document(data, encoding=encoding)


def read_document(path, *, max_bytes=MAX_METADATA_BYTES):
    return _parse_document(_read_bytes(path, max_bytes))


def read_document_snapshot(path, *, max_bytes=MAX_METADATA_BYTES):
    data = _read_bytes(path, max_bytes)
    return _parse_document(data), hashlib.sha256(data).hexdigest()


def read_request(path):
    request = read_document(path, max_bytes=MAX_REQUEST_BYTES)
    if not isinstance(request, dict):
        raise ValueError("Court request must be a JSON object.")
    return request
