#!/usr/bin/env python3
"""Validate packaged license material against the reviewed inventory."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate import RIDS, load_inventory, render, select_packages


def validate_bytes(notices: bytes, inventory: bytes, deps: dict | None = None):
    manifest = json.loads(inventory)
    if manifest.get('schemaVersion') != 1 or manifest.get('rid') not in RIDS:
        raise ValueError('Unsupported third-party inventory format or architecture')
    if hashlib.sha256(notices).hexdigest() != manifest.get('noticesSha256'):
        raise ValueError('Packaged third-party notices do not match their inventory')
    if deps is None:
        desktop, _ = load_inventory()
        deps = {'libraries': {key: {'type': 'package'}
                             for key in desktop['reviewedDistributionProfiles'][manifest['rid']]}}
    packages = select_packages(deps, manifest['rid'])
    if packages != manifest['packages']:
        raise ValueError('Packaged dependency inventory does not match published dependencies')
    if render(packages) != notices:
        raise ValueError('Packaged notices differ from reviewed authoritative license texts')
    return manifest


def validate_directory(path: Path, rid: str | None = None):
    path = Path(path)
    if (path / 'LICENSE').read_bytes() != (Path(__file__).resolve().parents[2] / 'LICENSE').read_bytes():
        raise ValueError('Missing or changed Pngr MIT license')
    deps_path = path / 'pngr.deps.json'
    result = validate_bytes((path / 'THIRD-PARTY-NOTICES.txt').read_bytes(),
                            (path / 'third-party-inventory.json').read_bytes(),
                            json.loads(deps_path.read_text()) if deps_path.exists() else None)
    if rid is not None and result['rid'] != rid:
        raise ValueError('Packaged third-party inventory has incorrect architecture')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--rid', choices=RIDS)
    args = parser.parse_args()
    validate_directory(args.directory, args.rid)
    print('Third-party notices and dependency inventory verified.')
