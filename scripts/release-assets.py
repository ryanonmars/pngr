#!/usr/bin/env python3
"""Validate installer/update assets and create the public SHA-256 manifest."""
import argparse
import importlib.util
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

_NOTICE_SPEC = importlib.util.spec_from_file_location(
    'pngr_distribution_notices', Path(__file__).resolve().parent / 'verify-distribution-notices.py')
NOTICE_CHECKS = importlib.util.module_from_spec(_NOTICE_SPEC)
_NOTICE_SPEC.loader.exec_module(NOTICE_CHECKS)


def validate_feed(directory: Path, channel: str, version: str):
    feed = json.loads((directory / f'releases.{channel}.json').read_text())
    assets = feed['Assets']
    matching = [a for a in assets if a['Version'] == version and a['Type'] == 'Full']
    if len(matching) != 1:
        raise ValueError(f'{channel}: expected exactly one full update for {version}.')
    asset = matching[0]
    name = asset['FileName']
    if Path(name).name != name:
        raise ValueError('Unsafe update filename.')
    package = directory / name
    raw = package.read_bytes()
    if len(raw) != asset['Size'] or hashlib.sha256(raw).hexdigest().lower() != asset['SHA256'].lower():
        raise ValueError(f'{name}: feed integrity mismatch.')
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise ValueError('Corrupt update archive.')
        files = [name.replace('\\', '/') for name in archive.namelist() if not name.endswith('/')]
        names = set(files)
        if len(names) != len(files):
            raise ValueError('Duplicate update archive entry.')
        for name in files:
            if any(str(parent) in names for parent in PurePosixPath(name).parents):
                raise ValueError('Update archive file/directory collision.')
        for item in archive.namelist():
            components = item.replace('\\', '/').lower().split('/')
            filename = components[-1]
            if ('..' in components or filename in ('workspace.json', 'profiles.json', '.env')
                    or filename.endswith(('.db', '.db-wal', '.db-shm', '.sqlite', '.pdb'))):
                raise ValueError(f'Private or development file in update: {item}')
        NOTICE_CHECKS.verify_update_archive(archive, channel)
    return package


def prepare(directory: Path, version: str):
    for name in ('Pngr-Setup.exe', 'Pngr-macos-universal.dmg'):
        if not (directory / name).is_file():
            raise ValueError(f'Missing installer {name}.')
    for channel in ('win-x64', 'osx-universal'):
        validate_feed(directory, channel, version)
    paths = sorted(path for path in directory.iterdir() if path.is_file() and path.name != 'SHA256SUMS')
    for path in paths:
        if path.suffix not in ('.exe', '.dmg', '.nupkg', '.json') and not path.name.startswith('RELEASES'):
            raise ValueError(f'Unexpected public asset: {path.name}')
    manifest = ''.join(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n' for path in paths)
    (directory / 'SHA256SUMS').write_text(manifest, encoding='ascii')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('version')
    args = parser.parse_args()
    prepare(args.directory, args.version.removeprefix('v'))
