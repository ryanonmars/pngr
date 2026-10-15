#!/usr/bin/env python3
"""Check delivered license material and keep private analysis out of packages."""
import importlib.util
import json
from pathlib import Path, PurePosixPath
import sys

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('pngr_license_verifier', ROOT / 'scripts/licensing/verify.py')
LICENSES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LICENSES)


def reject_private_material(name):
    path = PurePosixPath(name.replace('\\', '/').lower())
    if (any(part in {'.rea', '.codex', '.agents', 'internal'} for part in path.parts)
            or path.suffix in {'.hop', '.hopper', '.hdb', '.pcap', '.pcapng'}
            or any(token in path.name for token in ('pseudocode', 'disassembly', 'rea-evidence', 'ip-compliance-review'))):
        raise ValueError('Private analysis material in distribution: ' + name)


def verify_archive_runtime(archive, material_prefix, deps_prefix, rid):
    try:
        if archive.read(material_prefix + "LICENSE") != (ROOT / "LICENSE").read_bytes():
            raise ValueError("Missing or changed Pngr MIT license")
        manifest = LICENSES.validate_bytes(
            archive.read(material_prefix + 'THIRD-PARTY-NOTICES.txt'),
            archive.read(material_prefix + 'third-party-inventory.json'),
            json.loads(archive.read(deps_prefix + 'pngr.deps.json')))
    except KeyError as error:
        raise ValueError('Missing third-party notices, inventory, or dependency manifest') from error
    if manifest['rid'] != rid:
        raise ValueError('Third-party inventory architecture does not match distribution')
    return manifest


def verify_update_archive(archive, channel):
    for name in archive.namelist():
        reject_private_material(name)
    if channel == 'win-x64':
        verify_archive_runtime(archive, 'lib/app/', 'lib/app/', channel)
    elif channel == 'osx-universal':
        prefix = 'lib/app/Contents/Resources/'
        if archive.read(prefix + 'LICENSE') != (ROOT / 'LICENSE').read_bytes():
            raise ValueError('Missing or changed Pngr MIT license')
        if archive.read(prefix + 'THIRD-PARTY-NOTICES.txt') != (ROOT / 'compliance/THIRD-PARTY-NOTICES.txt').read_bytes():
            raise ValueError('Universal application lacks the complete reviewed notice collection')
        for rid in ('osx-arm64', 'osx-x64'):
            payload = prefix + 'runtimes/' + rid + '/'
            verify_archive_runtime(archive, payload, payload, rid)
    else:
        raise ValueError('Unrecognized update channel')


def verify_universal_directory(bundle):
    resources = Path(bundle) / 'Contents/Resources'
    if (resources / 'LICENSE').read_bytes() != (ROOT / 'LICENSE').read_bytes():
        raise ValueError('Missing or changed Pngr MIT license')
    if (resources / 'THIRD-PARTY-NOTICES.txt').read_bytes() != (ROOT / 'compliance/THIRD-PARTY-NOTICES.txt').read_bytes():
        raise ValueError('Universal application lacks the complete reviewed notice collection')
    for rid in ('osx-arm64', 'osx-x64'):
        LICENSES.validate_directory(resources / 'runtimes' / rid, rid)
