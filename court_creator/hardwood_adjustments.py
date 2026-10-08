"""Non-destructive hardwood settings shared by court previews and exports."""
import math
import numpy as np
from PIL import Image

LIMITS = {'brightness': (-100, 100, 0), 'contrast': (-100, 100, 0),
          'saturation': (-100, 100, 0), 'scale': (50, 200, 100), 'rotation': (-180, 180, 0)}


def texture_settings(floor):
    raw = floor.get('textureSettings')
    if raw is None: raw = {}
    if not isinstance(raw, dict): raise ValueError('Invalid hardwood texture settings.')
    result = {}
    for key, (low, high, default) in LIMITS.items():
        value = raw.get(key, default)
        if type(value) is not int or not low <= value <= high:
            raise ValueError('Invalid hardwood ' + key + '.')
        result[key] = value
    return result


def adjust_hardwood(image, settings):
    """Match Canvas ColorAdjustmentService's RGB math; retain original alpha."""
    if not any(settings[key] for key in ('brightness', 'contrast', 'saturation')):
        return image
    pixels = np.array(image)
    contrast = settings['contrast'] * 2.55
    factor = 259 * (contrast + 255) / (255 * (259 - contrast))
    # Process bounded strips to avoid full-size floating-point copies on 8K exports.
    for top in range(0, image.height, 128):
        rgb = pixels[top:top + 128, :, :3].astype(np.float64)
        rgb = np.clip(factor * (rgb - 128) + 128 + settings['brightness'] * 2.55, 0, 255)
        maximum, minimum = rgb.max(axis=2), rgb.min(axis=2)
        delta = maximum - minimum
        saturation = np.divide(delta, maximum, out=np.zeros_like(delta), where=maximum > 0)
        adjusted = np.clip(saturation * (1 + settings['saturation'] / 100), 0, 1)
        ratio = np.divide(adjusted, saturation, out=np.ones_like(delta), where=saturation > 0)
        rgb = maximum[:, :, None] - (maximum[:, :, None] - rgb) * ratio[:, :, None]
        pixels[top:top + 128, :, :3] = np.rint(np.clip(rgb, 0, 255)).astype(np.uint8)
    result = Image.fromarray(pixels)
    image.close()
    return result


def place_hardwood(image, settings):
    """Scale/rotate a repeating full-court texture around its center, before clipping."""
    if settings['scale'] == 100 and settings['rotation'] == 0: return image
    source = np.asarray(image)
    result = Image.new('RGBA', image.size)
    angle = math.radians(settings['rotation'])
    cosine, sine, zoom = math.cos(angle), math.sin(angle), settings['scale'] / 100
    center_x, center_y = image.width / 2, image.height / 2
    x = np.arange(image.width, dtype=np.float64)[None, :] + .5 - center_x
    for top in range(0, image.height, 64):
        y = np.arange(top, min(top + 64, image.height), dtype=np.float64)[:, None] + .5 - center_y
        sx = (cosine * x + sine * y) / zoom + center_x - .5
        sy = (-sine * x + cosine * y) / zoom + center_y - .5
        x0, y0 = np.floor(sx).astype(np.int64), np.floor(sy).astype(np.int64)
        fx, fy = (sx - x0)[:, :, None], (sy - y0)[:, :, None]
        a = source[y0 % image.height, x0 % image.width].astype(np.float64)
        b = source[y0 % image.height, (x0 + 1) % image.width].astype(np.float64)
        c = source[(y0 + 1) % image.height, x0 % image.width].astype(np.float64)
        d = source[(y0 + 1) % image.height, (x0 + 1) % image.width].astype(np.float64)
        pixels = np.rint((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy).astype(np.uint8)
        strip = Image.fromarray(pixels); result.paste(strip, (0, top)); strip.close()
    image.close()
    return result
