"""Build one self-contained Windows EXE, excluding game assets and personal files."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile
import struct

ROOT = Path(__file__).resolve().parent.parent

def tree(directory, prefix):
    for path in sorted(directory.rglob('*')):
        relative = path.relative_to(directory)
        if any(part in ('__pycache__', 'test', 'tests') for part in relative.parts): continue
        if path.is_symlink(): raise ValueError('Linked package input: ' + str(path))
        if path.is_file() and path.suffix not in ('.pyc', '.pdb'):
            yield path, (Path(prefix) / relative).as_posix()

def build(args):
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    version = json.loads((ROOT / 'package.json').read_text(encoding='utf-8'))['version']
    dotnet = str(args.dotnet)
    with tempfile.TemporaryDirectory(prefix='portable-build-', dir=output.parent) as folder:
        stage = Path(folder)
        desktop = stage / 'desktop'
        print('Publishing the app with its own .NET runtime…', flush=True)
        subprocess.run([dotnet, 'publish', str(ROOT / 'src/NBA2KCourtCreator/NBA2KCourtCreator.csproj'),
                        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', str(desktop), '--nologo',
                        '-p:CanvasToolkitVersion=' + args.canvas_version,
                        '-p:CanvasToolkitFeed=' + str(args.canvas_feed.resolve())], check=True, cwd=ROOT)
        files = list(tree(desktop, 'desktop')) + list(tree(ROOT / 'court_creator', 'court_creator'))
        for name in ('package.json', 'studio-build.json'):
            files.append((ROOT / name, name))
        files.append((ROOT / 'data/team_palettes.json', 'data/team_palettes.json'))
        for name in ('prepare_portable.py', 'build_2k26_floor_template_library.py', 'extract_2k26_court_files.py',
                     'export_2k26_court_texture.py', 'court_logo_web.py', 'texconv.exe', 'texconv-LICENSE.txt'):
            files.append((ROOT / 'tools' / name, 'tools/' + name))
        base = args.python_base.resolve()
        # This is a full relocatable interpreter, not the installation's path-bound venv.
        for path in sorted(base.iterdir()):
            if path.is_file() and (path.suffix in ('.exe', '.dll') or path.name == 'LICENSE.txt'):
                files.append((path, 'runtime/python/' + path.name))
        for name in ('DLLs', 'tcl'):
            files.extend(tree(base / name, 'runtime/python/' + name))
        for path, destination in tree(base / 'Lib', 'runtime/python/Lib'):
            if 'site-packages' not in Path(destination).parts:
                files.append((path, destination))
        packages = args.site_packages.resolve()
        for folder in sorted(packages.iterdir()):
            name = folder.name.casefold()
            if folder.is_dir() and (name == 'pil' or name.startswith(('pillow-', 'shapely', 'numpy'))):
                files.extend(tree(folder, 'runtime/python/Lib/site-packages/' + folder.name))
        if not any(name.endswith('runtime/python/python.exe') for _, name in files): raise ValueError('Python executable missing')
        names = [name.casefold() for _, name in files]
        if len(set(names)) != len(names): raise ValueError('Duplicate payload path')
        if any(name.startswith(('assets/', 'data/generated/')) or name.endswith('pyvenv.cfg') for name in names):
            raise ValueError('The portable bundle must exclude game assets and virtual-environment paths')
        print('Bundling Python, the app and first-run preparation tools (no floors)…', flush=True)
        payload = stage / 'payload.zip'
        records = {}
        with zipfile.ZipFile(payload, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path, name in files:
                data = path.read_bytes()
                archive.writestr(name, data)
                records[name] = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
            archive.writestr('portable-manifest.json', json.dumps(dict(version=version, files=records), indent=2))
        with zipfile.ZipFile(payload) as archive:
            if archive.testzip() is not None: raise ValueError('Payload archive failed CRC validation')
        with payload.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        info = stage / 'BundleInfo.g.cs'
        info.write_text('internal static class BundleInfo { internal const string Hash = "' + digest + '"; }', encoding='utf-8')
        launcher = stage / 'launcher'
        subprocess.run([dotnet, 'publish', str(ROOT / 'tools/PortableLauncher/PortableLauncher.csproj'),
                        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', str(launcher), '--nologo',
                        '-p:PayloadPath=' + str(payload), '-p:BundleInfoPath=' + str(info)], check=True, cwd=ROOT)
        executable = launcher / 'NBA 2K Court Creator Portable.exe'
        if not executable.is_file(): raise ValueError('The single EXE was not produced')
        temporary = output.with_suffix('.exe.tmp')
        with executable.open('rb') as source, temporary.open('wb') as destination:
            shutil.copyfileobj(source, destination)
            payload_offset = destination.tell()
            with payload.open('rb') as source:
                shutil.copyfileobj(source, destination)
            destination.write(struct.pack('<qq', payload_offset, payload.stat().st_size))
            destination.write(bytes.fromhex(digest))
            destination.write(b'CourtBundleZipV1')
        temporary.replace(output)
        with output.open('rb') as stream:
            output_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
        summary = dict(version=version, bytes=output.stat().st_size,
                       sha256=output_hash,
                       payloadSha256=digest, fileCount=len(files), gameAssetsIncluded=False)
        output.with_suffix('.build.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print('Portable EXE ready: ' + str(output), flush=True)
        print(f'Size: {summary["bytes"] / 1024 / 1024:.1f} MiB; no game assets bundled.', flush=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--python-base', type=Path, required=True)
    parser.add_argument('--site-packages', type=Path, required=True)
    parser.add_argument('--canvas-feed', type=Path, required=True)
    parser.add_argument('--canvas-version', default='0.9.15')
    parser.add_argument('--dotnet', type=Path, default=Path(shutil.which('dotnet') or 'dotnet'))
    parser.add_argument('--output', type=Path, default=ROOT / 'outputs/NBA 2K Court Creator Portable.exe')
    build(parser.parse_args())
