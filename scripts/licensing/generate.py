#!/usr/bin/env python3
"""Build offline notices from reviewed, version-pinned dependency records.

No license text is synthesized. New dependency/runtime versions fail until the
inventory and original upstream texts have been reviewed and checked in.
"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMPLIANCE = ROOT / 'compliance'
RIDS = ('osx-arm64', 'osx-x64', 'win-x64')


def load_inventory():
    desktop = json.loads((COMPLIANCE / 'desktop-dependencies.json').read_text())
    rust = json.loads((COMPLIANCE / 'velopack-rust-dependencies.json').read_text())
    tool_version = json.loads((ROOT / '.config/dotnet-tools.json').read_text())['tools']['vpk']['version']
    if rust['velopackVersion'] != tool_version:
        raise ValueError('Unreviewed Velopack packaging tool version: ' + tool_version)
    return desktop, rust


def select_packages(deps, rid):
    desktop, rust = load_inventory()
    expected = desktop['reviewedDistributionProfiles'][rid]
    actual = sorted(key for key, value in deps['libraries'].items() if value['type'] != 'project')
    if actual != expected:
        raise ValueError('Published dependency graph differs from reviewed ' + rid + ' profile: '
                         + ', '.join(sorted(set(actual) ^ set(expected))))
    reviewed = {(x['name'].lower(), x['version']): x for x in desktop['packages']}
    selected = []
    for key, library in sorted(deps['libraries'].items()):
        if library['type'] == 'project':
            continue
        name, version = key.rsplit('/', 1)
        name = name.removeprefix('runtimepack.')
        entry = reviewed.get((name.lower(), version))
        if not entry:
            raise ValueError('Unreviewed distributed dependency: ' + key)
        if entry.get('rid', rid) != rid:
            raise ValueError('Runtime dependency architecture does not match ' + rid)
        if entry['kind'] == 'build-only':
            raise ValueError('Build-only package unexpectedly redistributed: ' + key)
        selected.append(dict(entry, distribution='published-dependency-graph'))
    # Updater/setup code is added by Velopack after dotnet publish, not in .deps.
    selected.extend(dict(x, kind='native-updater-dependency', distribution=rust['scope'])
                    for x in rust['packages'] if rid in x['rids'])
    selected.extend(x for x in desktop.get('additionalComponents', []) if rid in x['rids'])
    return selected


def render(packages):
    groups = {}
    for package in packages:
        if package.get('issue') or not package['notices']:
            raise ValueError('Unresolved notice coverage: ' + package['name'])
        for notice in package['notices']:
            data = (ROOT / notice['path']).read_bytes()
            digest = hashlib.sha256(data).hexdigest()
            if digest != notice['sha256']:
                raise ValueError('Reviewed license text changed: ' + notice['path'])
            group = groups.setdefault(digest, {'data': data, 'components': set(), 'sources': set()})
            group['components'].add(package['name'] + ' ' + package['version'])
            group['sources'].add(notice['source'])
    output = bytearray(b'PNGR THIRD-PARTY NOTICES\n\nOriginal license and attribution texts follow unchanged. Some upstream notices\ncover optional or platform-specific components not present in every package.\nThe companion inventory describes versions, scope, and authoritative sources.\n\n')
    for package in packages:
        if package.get('copyrightMetadata'):
            output.extend((package['name'] + ' ' + package['version'] + '\nPackage copyright metadata: ' + package['copyrightMetadata'] + '\n\n').encode())
        if package.get('sourceAvailability'):
            output.extend((package['name'] + ' ' + package['version'] + '\n' + package['sourceAvailability'] + '\n\n').encode())
    for digest, group in sorted(groups.items()):
        header = ('=' * 78 + '\nComponents: ' + ', '.join(sorted(group['components']))
                  + '\nSources:\n' + '\n'.join(sorted(group['sources']))
                  + '\nOriginal-text SHA-256: ' + digest + '\n' + '-' * 78 + '\n')
        output.extend(header.encode())
        output.extend(group['data'])
        output.extend(b'\n\n')
    return bytes(output)


def generate(publish_dir, rid):
    if rid not in RIDS:
        raise ValueError('Unsupported distribution RID: ' + rid)
    deps = json.loads((publish_dir / 'pngr.deps.json').read_text())
    packages = select_packages(deps, rid)
    notices = render(packages)
    inventory = {'schemaVersion': 1, 'rid': rid, 'noticesSha256': hashlib.sha256(notices).hexdigest(), 'packages': packages}
    (publish_dir / 'THIRD-PARTY-NOTICES.txt').write_bytes(notices)
    (publish_dir / 'third-party-inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')
    return inventory


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish-dir', type=Path)
    parser.add_argument('--rid', choices=RIDS)
    parser.add_argument('--all', action='store_true', help='Regenerate public desktop notice superset')
    args = parser.parse_args()
    if args.all:
        desktop, rust = load_inventory()
        packages = [x for x in desktop['packages'] if x['kind'] != 'build-only'] + desktop.get('additionalComponents', []) + rust['packages']
        (COMPLIANCE / 'THIRD-PARTY-NOTICES.txt').write_bytes(render(packages))
    elif args.publish_dir and args.rid:
        generate(args.publish_dir, args.rid)
    else:
        parser.error('Provide --publish-dir and --rid, or --all')


if __name__ == '__main__':
    main()
