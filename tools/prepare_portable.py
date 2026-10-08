"""Build a local floor cache from the installed game on the portable app's first run."""
from __future__ import annotations
import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
from io import BytesIO
import json
import os
from pathlib import Path, PurePosixPath
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'tools'))
sys.path.insert(0, str(ROOT))
import build_2k26_floor_template_library as library
from extract_2k26_court_files import extract, read_manifest
from court_creator.court_import import find_nba2k27_root, _remember_game_root, _base_file_lock
from court_creator.experimental_lines import prepare_geometry, load_geometry
from court_creator.backend import load_stock_state
from PIL import Image

CATALOG = ROOT / "assets/court_floor_templates/nba2k27"
INDEX = CATALOG / "nba2k27_floor_templates.json"

def ready():
    try:
        data = json.loads(INDEX.read_text(encoding="utf-8"))
        return data.get('preparationComplete', True) is True and bool(data["templates"]) and load_geometry(ROOT) is not None and all(
            (ROOT / "assets" / item["path"]).is_file() and (ROOT / "assets" / item["thumbnailPath"]).is_file()
            for item in data["templates"])
    except (OSError, ValueError, KeyError, TypeError):
        return False

def prepare_image(candidate, game):
    width, height, chain = library.read_tld_metadata(candidate.with_suffix('.tld'))
    fourcc, raw_size = library.choose_format(width, height, chain, 'auto')
    identity = library.template_id_for(candidate, '2k27')
    png = CATALOG / 'images' / (identity + '.png')
    thumbnail = CATALOG / 'thumbnails' / (identity + '.jpg')
    if not png.exists():
        texture = library.oodle_decompress(game, candidate.read_bytes(), raw_size)
        size = library.top_mip_bytes(width, height // 2, 8 if fourcc == 'DXT1' else 16)
        with Image.open(BytesIO(library.make_dds(width, height // 2, fourcc, texture[:size]))) as decoded:
            image = library.clean_decode_speckles(decoded.convert('RGBA'))
        image.putalpha(255)
        temporary = png.with_suffix('.tmp')
        image.save(temporary, format='PNG'); temporary.replace(png)
        image.close()
    if not thumbnail.exists():
        with Image.open(png) as source:
            image = source.convert('RGB'); image.thumbnail((360, 180), Image.Resampling.LANCZOS)
            temporary = thumbnail.with_suffix('.tmp')
            image.save(temporary, format='JPEG', quality=82, optimize=True); temporary.replace(thumbnail)
            image.close()
    name = library.friendly_name(candidate)
    return dict(id=identity, name=name, path=library.relative_to_asset_root(png),
                thumbnailPath=library.relative_to_asset_root(thumbnail), texturePath=None,
                width=width, height=height // 2, format=fourcc, cleaned=True,
                category=library.category_for_name(name))

def publish_catalog(templates, complete):
    temporary = INDEX.with_suffix('.tmp')
    temporary.write_text(json.dumps(dict(name='NBA 2K27 Floor Templates', gameVersion='2k27',
                                        preparationComplete=complete, templates=templates), indent=2), encoding='utf-8')
    temporary.replace(INDEX)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-root', type=Path)
    parser.add_argument('--check-only', action='store_true')
    args = parser.parse_args()
    if args.check_only:
        print('Floor cache ready.' if ready() else 'Floor cache needs first-run preparation.', flush=True)
        return 0 if ready() else 3
    # Avoid consulting game discovery after a complete cache has been generated.
    if ready():
        print('Court library is ready.', flush=True); return 0
    try:
        game = find_nba2k27_root(args.game_root)
    except FileNotFoundError as error:
        print(str(error), file=sys.stderr); return 2
    _remember_game_root(game, required=True)
    os.environ['NBA2K27_ROOT'] = str(game)
    CATALOG.mkdir(parents=True, exist_ok=True)
    with _base_file_lock(CATALOG / 'prepare'):
        if ready(): return 0
        print('Preparing court geometry from NBA 2K27…', flush=True)
        prepare_geometry(ROOT)
        (CATALOG / 'images').mkdir(exist_ok=True)
        (CATALOG / 'thumbnails').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='court-floor-source-', dir=CATALOG) as folder:
            scratch = Path(folder)
            print('Reading floor textures from the installed game…', flush=True)
            entries = {}
            archives = {}
            for entry in read_manifest(game / 'manifest'):
                path = PurePosixPath(entry['path'])
                if path.parts[:1] == ('shared',) and path.suffix in ('.mip0', '.tld'):
                    texture_name = path.with_suffix('.mip0').name
                    if not (library.WOOD_FLOOR.match(texture_name) or library.surface_key(Path(texture_name)) in library.ADDITIONAL_FLOOR_SURFACES):
                        continue
                    if '..' in path.parts or ':' in str(path) or '\\' in str(path): raise ValueError('Invalid game texture path')
                    if entry['offset'] < 0 or not 0 < entry['size'] <= 64 * 1024 * 1024: raise ValueError('Invalid texture range')
                    if entry['chunk'] not in archives:
                        archive = (game / entry['chunk']).resolve()
                        if not archive.is_relative_to(game.resolve()): raise ValueError('Archive path leaves the game folder')
                        archives[entry['chunk']] = archive
                    entries[str(path)] = entry
            wanted = []
            for name, entry in entries.items():
                path = Path(name)
                if path.suffix != '.mip0': continue
                if not (library.WOOD_FLOOR.match(path.name) or library.surface_key(path) in library.ADDITIONAL_FLOOR_SURFACES): continue
                partner = entries.get(str(PurePosixPath(name).with_suffix('.tld')))
                if partner: wanted.extend((entry, partner))
            if not wanted: raise ValueError('The installed game has no supported floor textures.')
            extract(game, scratch, wanted)
            candidates = [path for path in library.find_candidates(scratch)
                          if library.surface_key(path) not in library.BROKEN_SOURCE_TEXTURES]
            if not candidates: raise ValueError('No supported floors were found.')
            print('Preparing the first court floor…', flush=True)
            result = [prepare_image(candidates[0], game)]
            publish_catalog(result, False)
            print('FIRST_FLOOR_READY', flush=True)
            workers = min(4, max(1, os.cpu_count() or 1))
            with ProcessPoolExecutor(max_workers=workers) as pool:
                futures = [pool.submit(prepare_image, path, game) for path in candidates[1:]]
                for future in as_completed(futures):
                    result.append(future.result())
                    print(f'Preparing floors: {len(result)}/{len(candidates)}', flush=True)
            if not result: raise ValueError('No supported floors were prepared.')
            result.sort(key=lambda item: item['id'])
            publish_catalog(result, True)
        state = load_stock_state()
        if state['floorLibraryCount'] != len(result): raise ValueError('The new floor catalog failed its load check.')
        print(f'Court library ready: {len(result)} floors.', flush=True)
    return 0

if __name__ == '__main__':
    try: raise SystemExit(main())
    except Exception as error:
        print('Floor preparation failed: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
