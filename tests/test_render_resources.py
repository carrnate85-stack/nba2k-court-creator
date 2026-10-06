from collections import OrderedDict
import hashlib
import math
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image, ImageChops, ImageDraw, ImageStat

from court_creator import court_template


def cache_bytes(cache):
    return sum(court_template._cached_image(item).width * court_template._cached_image(item).height * 4
               for item in cache.values())


class RenderResourceTests(unittest.TestCase):
    def test_cache_byte_budget_lru_and_independent_copies(self):
        cache = OrderedDict()
        with Image.new("RGBA", (16, 16), "red") as image:
            for key in ("a", "b"):
                court_template._remember_image(cache, key, image, byte_budget=2048, entry_limit=8)
            retained_copy = cache["b"].copy()
            evicted = cache["b"]
            cache.move_to_end("a")
            court_template._remember_image(cache, "c", image, byte_budget=2048, entry_limit=8)
            self.assertEqual(list(cache), ["a", "c"])
            self.assertEqual(cache_bytes(cache), 2048)
            with self.assertRaises(ValueError):
                evicted.getpixel((0, 0))
            self.assertEqual(retained_copy.getpixel((0, 0)), (255, 0, 0, 255))
            retained_copy.close()
            replaced = cache["a"]
            court_template._remember_image(cache, "a", image, byte_budget=2048, entry_limit=8)
            self.assertEqual(list(cache), ["c", "a"])
            self.assertEqual(cache_bytes(cache), 2048)
            with self.assertRaises(ValueError):
                replaced.getpixel((0, 0))
            with Image.new("RGBA", (32, 32)) as too_large:
                court_template._remember_image(cache, "large", too_large, byte_budget=2048, entry_limit=8)
                self.assertNotIn("large", cache)
                self.assertIsNone(too_large.getbbox())
            court_template._remember_image(cache, "d", image, byte_budget=2048, entry_limit=1)
            self.assertEqual(list(cache), ["d"])
        for image in cache.values():
            image.close()

    def test_external_and_psd_caches_apply_their_byte_limits(self):
        external, layers = OrderedDict(), OrderedDict()
        with tempfile.TemporaryDirectory() as folder, patch.object(court_template, "_EXTERNAL_IMAGE_CACHE", external), patch.object(
                court_template, "_EXTERNAL_IMAGE_CACHE_BYTES", 2048), patch.object(court_template, "_PREVIEW_LAYER_CACHE", layers), patch.object(
                court_template, "_PREVIEW_LAYER_CACHE_BYTES", 2048):
            for index, color in enumerate(("red", "green", "blue")):
                path = Path(folder) / f"{index}.png"
                with Image.new("RGBA", (16, 16), color) as image:
                    image.save(path)
                    court_template._remember_preview_layer((index,), image, (index, index + 1))
                with court_template._cached_external_image(path, (16, 16), fit=False) as image:
                    self.assertEqual(image.size, (16, 16))
                self.assertLessEqual(cache_bytes(external), 2048)
                self.assertLessEqual(cache_bytes(layers), 2048)
            self.assertEqual(list(layers), [(1,), (2,)])
            self.assertEqual(layers[(2,)][1], (2, 3))
            self.assertEqual(len(external), 2)
            with Image.new("RGBA", (32, 32), "yellow") as large:
                court_template._remember_preview_layer((99,), large, (0, 0))
                self.assertNotIn((99,), layers)
        for cache in (external, layers):
            for entry in cache.values():
                court_template._cached_image(entry).close()

    def test_giant_logo_uses_only_canvas_sized_transform_and_preserves_opacity(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "logo.png"
            with Image.new("RGBA", (32, 16), (220, 30, 70, 255)) as source:
                source.save(path)
            revision = hashlib.sha256(path.read_bytes()).hexdigest()
            canvas = Image.new("RGBA", (256, 128))
            logo = {"path": str(path), "sourceRevision": revision, "x": 128 - 16384, "y": 64 - 16384,
                    "width": 32768, "height": 32768, "rotation": 37, "opacity": 40}
            sizes = []
            buffers = []
            original = Image.Image.transform
            original_convert = Image.Image.convert
            original_channel = Image.Image.getchannel
            original_point = Image.Image.point

            def transform(image, size, *args, **kwargs):
                sizes.append(size)
                result = original(image, size, *args, **kwargs)
                if image.mode == "RGBA":
                    buffers.append(result)
                return result

            def convert(image, *args, **kwargs):
                result = original_convert(image, *args, **kwargs)
                if image.format == "PNG":
                    buffers.append(result)
                return result

            def channel(image, *args, **kwargs):
                result = original_channel(image, *args, **kwargs)
                buffers.append(result)
                return result

            def point(image, *args, **kwargs):
                result = original_point(image, *args, **kwargs)
                buffers.append(result)
                return result

            with patch.object(Image.Image, "resize", side_effect=AssertionError("Giant resize allocated")), patch.object(
                    Image.Image, "transform", new=transform), patch.object(Image.Image, "convert", new=convert), patch.object(
                    Image.Image, "getchannel", new=channel), patch.object(Image.Image, "point", new=point):
                court_template._composite_logo(canvas, logo, 1)
            self.assertTrue(sizes)
            self.assertTrue(all(width <= canvas.width and height <= canvas.height for width, height in sizes))
            pixel = canvas.getpixel((128, 64))
            self.assertEqual(pixel[3], 102)
            self.assertTrue(all(abs(actual - expected) <= 3 for actual, expected in zip(pixel[:3], (220, 30, 70))))
            self.assertEqual(canvas.getbbox(), (0, 0, 256, 128))
            self.assertGreaterEqual(len(buffers), 4)
            for buffer in buffers:
                with self.assertRaises(ValueError):
                    buffer.getpixel((0, 0))
            canvas.close()

    def test_large_transform_matches_normal_placement_rotation_and_flips(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "quadrants.png"
            source = Image.new("RGBA", (64, 48))
            draw = ImageDraw.Draw(source)
            colors = [(230, 20, 30, 255), (20, 220, 40, 255), (25, 35, 225, 255), (220, 210, 30, 255)]
            for color, box in zip(colors, ((2, 2, 31, 23), (32, 2, 61, 23), (2, 24, 31, 45), (32, 24, 61, 45))):
                draw.rectangle(box, fill=color)
            source.save(path); source.close()
            for angle in (0, 15, 45, 90, 135, 271):
                for flip_x, flip_y in ((False, False), (True, False), (False, True), (True, True)):
                    logo = {"path": str(path), "x": 56, "y": 48, "width": 144, "height": 96, "rotation": angle,
                            "flipX": flip_x, "flipY": flip_y, "opacity": 70}
                    ordinary = Image.new("RGBA", (256, 192))
                    clipped = Image.new("RGBA", (256, 192))
                    court_template._composite_logo(ordinary, logo, 1)
                    with patch.object(court_template, "_LOGO_INTERMEDIATE_MAX_PIXELS", 0):
                        court_template._composite_logo(clipped, logo, 1)
                    with self.subTest(angle=angle, flip_x=flip_x, flip_y=flip_y):
                        for (u, v), color in zip(((.25, .25), (.75, .25), (.25, .75), (.75, .75)), colors):
                            dx = (u - .5) * 144 * (-1 if flip_x else 1)
                            dy = (v - .5) * 96 * (-1 if flip_y else 1)
                            radians = math.radians(angle)
                            point = (round(128 + dx * math.cos(radians) - dy * math.sin(radians)),
                                     round(96 + dx * math.sin(radians) + dy * math.cos(radians)))
                            self.assertEqual(clipped.getpixel(point), (*color[:3], 178))
                            self.assertEqual(ordinary.getpixel(point), clipped.getpixel(point))
                        with ImageChops.difference(ordinary, clipped) as difference:
                            self.assertLess(max(ImageStat.Stat(difference).mean), 9)
                    ordinary.close(); clipped.close()

    def test_off_canvas_logo_does_not_decode_but_still_checks_pinned_source(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "logo.png"
            with Image.new("RGBA", (16, 16), "red") as source:
                source.save(path)
            canvas = Image.new("RGBA", (64, 32))
            logo = {"path": str(path), "sourceRevision": hashlib.sha256(path.read_bytes()).hexdigest(),
                    "x": 10000, "y": 10000, "width": 100, "height": 100}
            with patch.object(Image, "open", side_effect=AssertionError("Invisible logo decoded")):
                court_template._composite_logo(canvas, logo, 1)
                path.write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "changed after its preview"):
                    court_template._composite_logo(canvas, logo, 1)
            self.assertIsNone(canvas.getbbox()); canvas.close()

    def test_clipped_transform_is_consistent_at_preview_scales(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "clipped.png"
            with Image.new("RGBA", (32, 24)) as source:
                draw = ImageDraw.Draw(source)
                draw.rectangle((2, 2, 15, 21), fill=(220, 30, 80, 180))
                draw.rectangle((16, 2, 29, 21), fill=(20, 160, 210, 255))
                source.save(path)
            for scale in (.125, .5, 1.75):
                for width, height, x, y, angle in ((160, 80, -20, -10, 33), (240, 8, -60, 28, 64),
                                                 (8, 220, 45, -70, 121), (120, 90, 70, 45, 270)):
                    with self.subTest(scale=scale, size=(width, height), angle=angle):
                        logo = {"path": str(path), "x": x / scale, "y": y / scale,
                                "width": width / scale, "height": height / scale,
                                "rotation": angle, "flipX": True, "opacity": 65}
                        translated = {**logo, "x": (x + 96) / scale, "y": (y + 80) / scale}
                        with Image.new("RGBA", (320, 240)) as reference, Image.new("RGBA", (96, 64)) as clipped:
                            with patch.object(court_template, "_LOGO_INTERMEDIATE_MAX_PIXELS", 0):
                                court_template._composite_logo(reference, translated, scale)
                                court_template._composite_logo(clipped, logo, scale)
                            self.assertIsNotNone(clipped.getbbox())
                            with reference.crop((96, 80, 192, 144)) as expected:
                                with ImageChops.difference(expected, clipped) as difference:
                                    self.assertLess(max(ImageStat.Stat(difference).mean), .1)

    def test_large_transform_checks_revision_before_and_after_decoding(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "pinned.png"
            with Image.new("RGBA", (32, 16), "red") as source:
                source.save(path)
            original_bytes = path.read_bytes()
            logo = {"path": str(path), "sourceRevision": hashlib.sha256(original_bytes).hexdigest(),
                    "x": -16300, "y": -16300, "width": 32768, "height": 32768}
            with Image.new("RGBA", (128, 64)) as canvas:
                path.write_bytes(b"changed before decode")
                with patch.object(Image, "open", side_effect=AssertionError("Changed image decoded")):
                    with self.assertRaisesRegex(ValueError, "changed after its preview"):
                        court_template._composite_logo(canvas, logo, 1)
                path.write_bytes(original_bytes)
                original_transform = Image.Image.transform

                def mutate(image, *args, **kwargs):
                    result = original_transform(image, *args, **kwargs)
                    path.write_bytes(b"changed during decode")
                    return result

                with patch.object(Image.Image, "transform", new=mutate):
                    with self.assertRaisesRegex(ValueError, "changed while it was being decoded"):
                        court_template._composite_logo(canvas, logo, 1)

    def test_nonfinite_transform_values_fail_before_allocation(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "logo.png"
            with Image.new("RGBA", (16, 16), "red") as source:
                source.save(path)
            canvas = Image.new("RGBA", (64, 32))
            for field in ("x", "y", "width", "height", "rotation", "opacity"):
                for value in (float("nan"), float("inf"), float("-inf")):
                    with self.subTest(field=field, value=value), patch.object(Image, "open", side_effect=AssertionError("Invalid transform decoded")):
                        with self.assertRaisesRegex(ValueError, "finite numbers"):
                            court_template._composite_logo(canvas, {"path": str(path), field: value}, 1)
            canvas.close()


if __name__ == "__main__":
    unittest.main()
