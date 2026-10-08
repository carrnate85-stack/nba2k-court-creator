import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image
from court_creator import backend, experimental_lines as renderer
from tests.test_geometry_cache import fixture


class TwoPointHardwoodTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.main = self.root / 'main.png'
        self.second = self.root / 'second.png'
        Image.new('RGB', (800, 400), 'red').save(self.main)
        Image.new('RGB', (800, 400), 'blue').save(self.second)
        self.geometry = fixture()
        for layer in self.geometry['paints'] + self.geometry['layers']:
            layer['visible'] = False
        def region(identity, polygon, color):
            layer = next(item for item in self.geometry['paints'] + self.geometry['layers'] if item['id'] == identity)
            layer.update(polygons=[polygon], gameUvPolygons=[polygon], visible=True, color=color)
        region('two-point-left', [[100, 100], [300, 100], [300, 180], [140, 180], [140, 420], [300, 420], [300, 500], [100, 500]], '#FF00FF')
        region('two-point-right', [[900, 100], [700, 100], [700, 180], [860, 180], [860, 420], [700, 420], [700, 500], [900, 500]], '#FF00FF')
        region('paint-left', [[140, 180], [300, 180], [300, 420], [140, 420]], '#008000')
        region('paint-right', [[700, 180], [860, 180], [860, 420], [700, 420]], '#008000')
        region(self.geometry['layers'][0]['id'], [[180, 120], [220, 120], [220, 140], [180, 140]], '#FFFF00')
        self.request = dict(floor={'path': str(self.main)}, twoPointFloor={'path': str(self.second)}, outsideColor='#000000')

    def render(self, request=None, *, preview=True):
        output = self.root / 'export.png'
        renderer.render_experimental(self.root, request or self.request, output, preview=preview, geometry=self.geometry)
        return Image.open(output)

    def test_second_wood_is_clipped_to_both_regions_with_keys_center_and_lines_preserved(self):
        for mapping in ('game-uv', 'template'):
            request = dict(self.request, mappingMode=mapping)
            request['floor'] = dict(request['floor'], bbox=[100, 100, 800, 400])
            with self.subTest(mapping=mapping), self.render(request) as image:
                def pixel(x, y): return image.getpixel((round(x / 4), round(y / 4)))[:3]
                self.assertEqual(pixel(120, 300), (0, 0, 255))
                self.assertEqual(pixel(880, 300), (0, 0, 255))
                self.assertEqual(pixel(200, 300), (0, 128, 0))
                self.assertEqual(pixel(800, 300), (0, 128, 0))
                self.assertEqual(pixel(500, 300), (255, 0, 0))
                self.assertEqual(pixel(200, 128), (255, 255, 0))
                self.assertEqual(pixel(40, 300), (0, 0, 0))

    def test_full_resolution_and_disabled_second_floor_restore_two_point_color(self):
        with self.render(preview=False) as image:
            self.assertEqual(image.size, (8192, 4096))
            self.assertEqual(image.getpixel((120, 300))[:3], (0, 0, 255))
            self.assertEqual(image.getpixel((880, 300))[:3], (0, 0, 255))
        with self.render(dict(self.request, twoPointFloor=None)) as image:
            self.assertEqual(image.getpixel((30, 75))[:3], (255, 0, 255))

    def test_pattern_keeps_full_court_alignment(self):
        pixels = Image.new('RGB', (800, 400), 'blue')
        pixels.paste('cyan', (400, 0, 800, 400)); pixels.save(self.second); pixels.close()
        with self.render() as image:
            self.assertEqual(image.getpixel((30, 75))[:3], (0, 0, 255))
            self.assertEqual(image.getpixel((220, 75))[:3], (0, 255, 255))

    def test_missing_stale_or_protected_second_floor_preserves_output(self):
        output = self.root / 'export.png'; output.write_bytes(b'previous export')
        self.request['twoPointFloor']['sourceRevision'] = hashlib.sha256(self.second.read_bytes()).hexdigest()
        Image.new('RGB', (800, 400), 'cyan').save(self.second)
        with self.assertRaisesRegex(ValueError, 'changed'):
            renderer.render_experimental(self.root, self.request, output, preview=True, geometry=self.geometry)
        self.assertEqual(output.read_bytes(), b'previous export')
        self.request['twoPointFloor'].pop('sourceRevision')
        original = self.second.read_bytes()
        with self.assertRaisesRegex(ValueError, 'new file'):
            renderer.render_experimental(self.root, self.request, self.second, preview=True, geometry=self.geometry)
        self.assertEqual(self.second.read_bytes(), original)
        self.second.unlink()
        with self.assertRaisesRegex(ValueError, 'missing'):
            renderer.render_experimental(self.root, self.request, output, preview=True, geometry=self.geometry)
        self.assertEqual(output.read_bytes(), b'previous export')

    def test_backend_resolves_and_protects_second_floor(self):
        request = dict(self.request, buildMode='game-uv', outputPath=str(self.root / 'backend.png'))
        request['twoPointFloor'] = {'path': 'second.png'}
        with patch.object(backend, 'PROJECT_ROOT', self.root):
            backend.render_preview(request, geometry=self.geometry)
            self.assertIn(self.second, backend._export_sources(request))
            with Image.open(request['outputPath']) as image:
                self.assertEqual(image.getpixel((30, 75))[:3], (0, 0, 255))

    def test_adjustments_are_independent_preserve_alpha_and_do_not_modify_originals(self):
        from court_creator.hardwood_adjustments import texture_settings, adjust_hardwood
        originals = (self.main.read_bytes(), self.second.read_bytes())
        request = dict(self.request)
        request['floor'] = dict(self.request['floor'], textureSettings={'brightness': -20})
        request['twoPointFloor'] = dict(self.request['twoPointFloor'], textureSettings={'saturation': -100})
        with self.render(request) as image:
            self.assertEqual(image.getpixel((125, 75))[:3], (204, 0, 0))
            self.assertEqual(image.getpixel((30, 75))[:3], (255, 255, 255))
            self.assertEqual(image.getpixel((50, 75))[:3], (0, 128, 0))
        self.assertEqual(originals, (self.main.read_bytes(), self.second.read_bytes()))
        sample = Image.new('RGBA', (1, 1), (100, 50, 20, 37))
        with adjust_hardwood(sample, texture_settings({'textureSettings': {'brightness': 20, 'contrast': 10, 'saturation': 15}})) as changed:
            self.assertEqual(changed.getpixel((0, 0))[3], 37)

    def test_rotation_and_grain_scale_repeat_without_escaping_the_masks(self):
        request = dict(self.request, twoPointFloor=dict(self.request['twoPointFloor'], textureSettings={'rotation': 45, 'scale': 50}))
        with self.render(request) as image:
            self.assertEqual(image.getpixel((30, 75))[:3], (0, 0, 255))
            self.assertEqual(image.getpixel((220, 75))[:3], (0, 0, 255))
            self.assertEqual(image.getpixel((125, 75))[:3], (255, 0, 0))
            self.assertEqual(image.getpixel((50, 75))[:3], (0, 128, 0))

    def test_invalid_texture_settings_are_rejected_before_replacing_output(self):
        output = self.root / 'export.png'; output.write_bytes(b'previous export')
        for settings in ([], 'bad', {'scale': 0}, {'rotation': 181}, {'brightness': True}, {'contrast': 101}, {'saturation': float('nan')}):
            with self.subTest(settings=settings), self.assertRaisesRegex(ValueError, 'hardwood'):
                request = dict(self.request, twoPointFloor=dict(self.request['twoPointFloor'], textureSettings=settings))
                renderer.render_experimental(self.root, request, output, preview=True, geometry=self.geometry)
            self.assertEqual(output.read_bytes(), b'previous export')


if __name__ == '__main__': unittest.main()
