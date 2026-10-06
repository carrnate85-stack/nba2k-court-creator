from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import struct
import tempfile
import threading
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image, ImageChops

from court_creator import court_import
from tests.test_court_import import solid_dds


class ImportCacheTests(unittest.TestCase):
    def setUp(self):
        self.cache = OrderedDict()
        self.cache_patch = patch.object(court_import, "_DDS_IMAGE_CACHE", self.cache)
        self.cache_patch.start()
        self.addCleanup(self.cache_patch.stop)
        self.addCleanup(self.close_cache)
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)

    def close_cache(self):
        with court_import._DDS_IMAGE_CACHE_LOCK:
            for image in self.cache.values():
                image.close()
            self.cache.clear()

    def write_source(self, name, color=0xF800, comment=b""):
        path = self.root / name
        data = bytearray(solid_dds(64, 32))
        for offset in range(128, len(data), 8):
            struct.pack_into("<HH", data, offset, color, color)
        with ZipFile(path, "w") as archive:
            archive.writestr("bigcourt.dds", data)
            archive.comment = comment
        return path

    def test_inspection_alignment_changes_and_identical_paths_share_original_pixels(self):
        source = self.write_source("source.iff")
        identical = self.write_source("identical.iff")
        background = self.root / "background.png"
        with Image.new("RGB", (64, 32), "magenta") as image:
            image.save(background)
        original_decode = court_import._decode_dds
        with patch.object(court_import, "_decode_dds", wraps=original_decode) as decode:
            metadata = court_import.inspect_iff(source)
            cached = self.cache[metadata["sourceRevision"]]
            pixels = cached.tobytes()
            for index, bounds in enumerate(([0, 0, 64, 32], [4, 3, 60, 29])):
                output = self.root / f"aligned-{index}.png"
                with patch.object(court_import, "target_court_bounds", return_value=[158, 65, 1042, 536]):
                    court_import.render_texture(identical, "bigcourt.dds", bounds, background, output,
                                                preview=True, source_revision=metadata["sourceRevision"])
                    with court_import.read_dds(source, "bigcourt.dds") as independent:
                        with court_import._aligned_image(independent, bounds, background, (1200, 600), guide=True) as expected:
                            with Image.open(output) as actual, ImageChops.difference(expected, actual) as difference:
                                self.assertIsNone(difference.getbbox())
                self.assertEqual(cached.tobytes(), pixels)
            # Independent one-off reads intentionally decode without retaining/copying.
            self.assertEqual(decode.call_count, 3)
            self.assertEqual(len(self.cache), 1)
        with Image.open(self.root / "aligned-0.png") as first, Image.open(self.root / "aligned-1.png") as second:
            self.assertNotEqual(first.getpixel((145, 63)), second.getpixel((145, 63)))

    def test_cache_hits_still_reject_changed_bytes_and_layout_with_same_metadata(self):
        source = self.write_source("source.iff", comment=b"old")
        metadata = court_import.inspect_iff(source)
        old_image = self.cache[metadata["sourceRevision"]]
        stamp, size = source.stat().st_mtime_ns, source.stat().st_size
        background, output = self.root / "background.png", self.root / "previous.png"
        with Image.new("RGB", (32, 16), "yellow") as image:
            image.save(background)
        for color, comment in ((0x001F, b"old"), (0xF800, b"new")):
            self.write_source("source.iff", color, comment)
            os.utime(source, ns=(stamp, stamp))
            self.assertEqual(source.stat().st_size, size)
            output.write_bytes(b"previous export")
            with patch.object(court_import, "_decode_dds", side_effect=AssertionError("Stale source decoded")):
                with self.assertRaisesRegex(ValueError, "source court changed"):
                    court_import.render_texture(source, "bigcourt.dds", metadata["sourceBounds"], background, output,
                                                preview=True, source_revision=metadata["sourceRevision"])
            self.assertEqual(output.read_bytes(), b"previous export")
            self.assertEqual(old_image.getpixel((0, 0)), (255, 0, 0, 255))
        current = court_import.inspect_iff(source)
        self.assertNotEqual(current["sourceRevision"], metadata["sourceRevision"])
        court_import.render_texture(source, "bigcourt.dds", current["sourceBounds"], background, output,
                                    preview=True, source_revision=current["sourceRevision"])
        with Image.open(output) as result:
            self.assertEqual(result.getpixel((600, 300)), (255, 0, 0))

    def test_byte_budget_lru_entry_limit_and_oversized_bypass(self):
        sources = [self.write_source(f"{index}.iff", color) for index, color in enumerate((0xF800, 0x001F, 0x07E0))]
        with patch.object(court_import, "_DDS_IMAGE_CACHE_BYTES", 16384):
            revisions, images = [], []
            for source in sources[:2]:
                with court_import._import_pixels(source, "bigcourt.dds") as (image, revision):
                    images.append(image); revisions.append(revision)
            with court_import._import_pixels(sources[0], "bigcourt.dds"):
                pass
            with court_import._import_pixels(sources[2], "bigcourt.dds") as (_, third):
                self.assertEqual(list(self.cache), [revisions[0], third])
                self.assertEqual(sum(item.width * item.height * 4 for item in self.cache.values()), 16384)
            with self.assertRaises(ValueError):
                images[1].getpixel((0, 0))
            with patch.object(court_import, "_DDS_IMAGE_CACHE_LIMIT", 1):
                with court_import._import_pixels(sources[1], "bigcourt.dds") as (_, latest):
                    self.assertEqual(list(self.cache), [latest])
            with patch.object(court_import, "_DDS_IMAGE_CACHE_BYTES", 4096):
                with court_import._import_pixels(sources[2], "bigcourt.dds") as (oversized, _):
                    self.assertEqual(oversized.getpixel((0, 0)), (0, 255, 0, 255))
                    self.assertEqual(list(self.cache), [latest])
                with self.assertRaises(ValueError):
                    oversized.getpixel((0, 0))

    def test_decode_and_alignment_failures_leave_cache_usable(self):
        first, second = self.write_source("first.iff"), self.write_source("second.iff", 0x001F)
        with court_import._import_pixels(first, "bigcourt.dds") as (retained, revision):
            pass
        with patch.object(court_import, "_decode_dds", side_effect=ValueError("decode failed")):
            with self.assertRaisesRegex(ValueError, "decode failed"):
                with court_import._import_pixels(second, "bigcourt.dds"):
                    self.fail("Failed decode yielded pixels")
        self.assertEqual(list(self.cache), [revision])
        with self.assertRaisesRegex(RuntimeError, "alignment failed"):
            with court_import._import_pixels(first, "bigcourt.dds"):
                raise RuntimeError("alignment failed")
        with patch.object(court_import, "_decode_dds", side_effect=AssertionError("Valid cached pixels decoded again")):
            with court_import._import_pixels(first, "bigcourt.dds") as (image, _):
                self.assertIs(image, retained)
                self.assertEqual(image.getpixel((0, 0)), (255, 0, 0, 255))
        with court_import._import_pixels(second, "bigcourt.dds") as (image, _):
            self.assertEqual(image.getpixel((0, 0)), (0, 0, 255, 255))

    def test_concurrent_consumers_share_decode_and_eviction_waits_for_borrower(self):
        first, second = self.write_source("first.iff"), self.write_source("second.iff", 0x001F)
        original_decode = court_import._decode_dds
        def read(_):
            with court_import._import_pixels(first, "bigcourt.dds") as (image, _revision):
                self.assertEqual(image.getpixel((0, 0)), (255, 0, 0, 255))
                return id(image)
        with patch.object(court_import, "_decode_dds", wraps=original_decode) as decode, ThreadPoolExecutor(max_workers=4) as pool:
            identities = list(pool.map(read, range(8)))
            self.assertEqual(len(set(identities)), 1)
            self.assertEqual(decode.call_count, 1)
        entered, attempted, release = threading.Event(), threading.Event(), threading.Event()
        held = []
        def hold():
            with court_import._import_pixels(first, "bigcourt.dds") as (image, _):
                held.append(image); entered.set()
                self.assertTrue(release.wait(5))
                return image.getpixel((0, 0))
        def evict():
            attempted.set()
            with court_import._import_pixels(second, "bigcourt.dds") as (image, _):
                return image.getpixel((0, 0))
        with patch.object(court_import, "_DDS_IMAGE_CACHE_LIMIT", 1), ThreadPoolExecutor(max_workers=2) as pool:
            borrowed = pool.submit(hold)
            self.assertTrue(entered.wait(5))
            pending = pool.submit(evict)
            try:
                self.assertTrue(attempted.wait(5))
                self.assertFalse(pending.done())
            finally:
                release.set()
            self.assertEqual(borrowed.result(timeout=5), (255, 0, 0, 255))
            self.assertEqual(pending.result(timeout=5), (0, 0, 255, 255))
        with self.assertRaises(ValueError):
            held[0].getpixel((0, 0))

    def test_recorded_layout_still_skips_decode_and_inspection_mismatch_precedes_cache(self):
        layout = {"mapping": "game-uv", "texture": "bigcourt.dds", "size": [64, 32], "courtBounds": [9, 4, 55, 28]}
        comment = court_import.LAYOUT_PREFIX + json.dumps(layout).encode()
        source = self.write_source("recorded.iff", comment=comment)
        with patch.object(court_import, "_decode_dds", side_effect=AssertionError("Recorded layout decoded")):
            metadata = court_import.inspect_iff(source)
        self.assertEqual(metadata["sourceBounds"], layout["courtBounds"])
        self.assertFalse(self.cache)
        with court_import._import_pixels(source, "bigcourt.dds") as (image, _):
            pass
        for descriptor, prior_comment in (({**metadata["textures"][0], "bytes": 1}, comment), (metadata["textures"][0], b"different")):
            with patch.object(court_import, "_decode_dds", side_effect=AssertionError("Mismatched inspection decoded")):
                with self.assertRaisesRegex(ValueError, "changed during inspection"):
                    with court_import._import_pixels(source, "bigcourt.dds", inspection=(descriptor, prior_comment)):
                        self.fail("Mismatched inspection yielded cached pixels")
        self.assertEqual(image.getpixel((0, 0)), (255, 0, 0, 255))

    def test_alignment_closes_working_buffers_without_closing_borrowed_pixels(self):
        path = self.write_source("source.iff")
        background = self.root / "background.png"
        with Image.new("RGB", (32, 16), "yellow") as image:
            image.save(background)
        originals = {name: getattr(Image.Image, name) for name in ("convert", "resize", "transform", "getchannel")}
        for fail_paste in (False, True):
            with self.subTest(fail_paste=fail_paste), court_import._import_pixels(path, "bigcourt.dds") as (source, _):
                buffers = []
                def convert(image, *args, **kwargs):
                    result = originals["convert"](image, *args, **kwargs)
                    if image.mode == "RGB":
                        buffers.append(result)
                    return result
                def resize(image, *args, **kwargs):
                    result = originals["resize"](image, *args, **kwargs)
                    buffers.append(result)
                    return result
                def transform(image, *args, **kwargs):
                    result = originals["transform"](image, *args, **kwargs)
                    if image.mode == "RGBA":
                        buffers.append(result)
                    return result
                def getchannel(image, *args, **kwargs):
                    result = originals["getchannel"](image, *args, **kwargs)
                    buffers.append(result)
                    return result
                output = None
                with patch.object(Image.Image, "convert", new=convert), patch.object(Image.Image, "resize", new=resize), patch.object(
                        Image.Image, "transform", new=transform), patch.object(Image.Image, "getchannel", new=getchannel), patch.object(
                        court_import, "target_court_bounds", return_value=[8, 4, 56, 28]):
                    if fail_paste:
                        with patch.object(Image.Image, "paste", side_effect=OSError("paste failed")):
                            with self.assertRaisesRegex(OSError, "paste failed"):
                                court_import._aligned_image(source, [0, 0, 64, 32], background, (64, 32))
                    else:
                        output = court_import._aligned_image(source, [0, 0, 64, 32], background, (64, 32))
                self.assertEqual(source.getpixel((0, 0)), (255, 0, 0, 255))
                self.assertGreaterEqual(len(buffers), 4)
                for buffer in buffers:
                    if buffer is output:
                        self.assertEqual(buffer.getpixel((32, 16)), (255, 0, 0))
                    else:
                        with self.assertRaises(ValueError):
                            buffer.getpixel((0, 0))
                if output is not None:
                    output.close()


if __name__ == "__main__":
    unittest.main()
