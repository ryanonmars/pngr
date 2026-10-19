#!/usr/bin/env python3
"""Explicit, local-only Developer ID Build A -> B Velopack/Keychain probe.

Creates its own installation and application data. Never opens the user's Pngr
workspace and never accepts an existing credential reference. The app's native
probe disables authorization UI only in its own process. Failure to authorize is
a failed test, not an invitation to grant broader Keychain permissions.
"""
import argparse
from contextlib import closing
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import sqlite3
import subprocess
import sys
import uuid

SPEC = importlib.util.spec_from_file_location('pngr_update_release', Path(__file__).with_name('verify-release.py'))
RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RELEASE)


def run(*args, timeout=120):
    # Inputs contain paths and public identifiers only. Do not stream arbitrary
    # child output: the native probe has its own sanitized status vocabulary.
    result = subprocess.run(list(map(str, args)), capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise ValueError(f'{Path(args[0]).name} failed (exit {result.returncode}); inspect the local evidence.')
    return result.stdout


def inventory(root):
    return {str(path.relative_to(root)): ('link:' + os.readlink(path) if path.is_symlink()
            else hashlib.sha256(path.read_bytes()).hexdigest())
            for path in root.rglob('*') if path.is_symlink() or path.is_file()}


def signature(app):
    metadata = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    if metadata.get('CFBundleIdentifier') != RELEASE.BUNDLE_ID:
        raise ValueError('Unexpected application identifier')
    run('codesign', '--verify', '--deep', '--strict', '--all-architectures', app)
    rid = 'osx-arm64' if platform.machine() == 'arm64' else 'osx-x64'
    host = app / 'Contents/Resources/runtimes' / rid / 'pngr'
    for path in app.rglob('*'):
        if RELEASE.native(path):
            run('codesign', '--verify', '--strict', '--all-architectures', path)
            arches = run('lipo', '-archs', path).split()
            for arch in arches:
                entitlement = RELEASE.JIT if path.name == 'pngr' and 'runtimes' in path.parts else (
                    RELEASE.UPDATER if path.name == 'UpdateMac' else {})
                RELEASE.inspect_signature(path, arch, entitlement)
    details = subprocess.run(['codesign', '-d', '--verbose=4', str(host)],
                             check=True, capture_output=True, text=True).stderr
    digest = re.search(r'^CDHash=([0-9a-f]+)$', details, re.MULTILINE)
    if not digest:
        raise ValueError('Missing host CodeDirectory hash')
    requirement = subprocess.run(['codesign', '-d', '-r-', str(host)],
                                 check=True, capture_output=True, text=True).stdout
    requirement = next((line for line in requirement.splitlines() if line.startswith('designated => ')), '')
    if not requirement or RELEASE.BUNDLE_ID not in requirement or RELEASE.TEAM not in requirement:
        raise ValueError('Unexpected host designated requirement')
    bundle_details = subprocess.run(['codesign', '-d', '--verbose=4', str(app)],
                                    check=True, capture_output=True, text=True).stderr
    bundle_digest = re.search(r'^CDHash=([0-9a-f]+)$', bundle_details, re.MULTILINE)
    if not bundle_digest:
        raise ValueError('Missing application CodeDirectory hash')
    numeric = metadata['CFBundleShortVersionString']
    version = metadata.get('pngrReleaseVersion', numeric)
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', numeric) or version.split('-')[0].split('+')[0] != numeric:
        raise ValueError('Release version does not match the numeric bundle version')
    return {'version': version, 'bundle_version': numeric, 'team': RELEASE.TEAM,
            'app_cdhash': bundle_digest.group(1),
            'host_cdhash': digest.group(1), 'host_requirement': requirement}


def apply_arguments(app, package, evidence):
    # Explicit packageDir prevents use of Pngr's shared/default update cache.
    return [app / 'Contents/MacOS/UpdateMac', 'apply', '--norestart', '--silent',
            '--rootDir', app, '--packageDir', evidence / 'update-cache',
            '--package', package, '--log', evidence / 'updater.log']


def probe(app, phase, state, isolated=False):
    if isolated and phase != 'read':
        raise ValueError('Isolated child diagnostic is restricted to read verification')
    # Native create/cleanup suppress authorization in the app process. Isolated
    # reads exercise the production authenticated child, which disables native
    # authorization UI itself and bounds its read, without touching parent UI.
    flag = '--verify-keychain-signature-isolated' if isolated else '--verify-keychain-signature'
    output = run(app / 'Contents/MacOS/pngr', flag, phase, state, timeout=30)
    expected = {'create': 'created.', 'read': 'read verified.', 'cleanup': 'removed.'}[phase]
    if expected not in output or (not isolated and 'authorization UI disabled' not in output):
        raise ValueError('Native credential probe did not confirm noninteractive success')
    return output


def seed(data):
    data.mkdir()
    (data / 'workspace.json').write_text(json.dumps({
        'Name': 'Disposable macOS updater verification', 'RetentionDays': 90,
        'WebEnabled': False, 'DesktopNotifications': False,
        'Targets': [{'Target': '127.0.0.1', 'Group': 'Update preservation', 'Options': {
            'Protocol': 'Icmp', 'Interval': '00:00:01', 'Timeout': '00:00:01', 'MaxHops': 4}}],
        'Profiles': [{'Name': 'Update verification profile', 'Options': {
            'Protocol': 'Icmp', 'Interval': '00:00:01', 'Timeout': '00:00:01', 'MaxHops': 4}}]}))


def capture(app, data, evidence, phase):
    output = evidence / f'{phase}.png'
    run(app / 'Contents/MacOS/pngr', '--data-dir', data, '--capture', output,
        '--capture-after', '5', '--exit-after-capture', timeout=90)
    if not output.is_file() or output.stat().st_size < 1024:
        raise ValueError('Native application restart/capture failed')


def snapshot(data):
    with closing(sqlite3.connect(data / 'tracescope.db')) as db:
        rows = {table: db.execute(f'SELECT * FROM {table}').fetchall() for table in ('sessions', 'samples')}
    if not rows['sessions'] or not rows['samples']:
        raise ValueError('No real monitoring history was produced for preservation testing')
    return rows


def assert_data(data, baseline, workspace):
    actual = snapshot(data)
    for table, previous in baseline.items():
        if table == 'sessions':
            if not {row[0] for row in previous} <= {row[0] for row in actual[table]}:
                raise ValueError('Session identity lost during update')
        elif not set(previous) <= set(actual[table]):
            raise ValueError('Monitoring history changed during update')
    current = json.loads((data / 'workspace.json').read_text())
    for field in ('Name', 'RetentionDays', 'Profiles', 'WebEnabled', 'Targets'):
        if current[field] != workspace[field]:
            raise ValueError(f'Application data changed during update: {field}')


def verify(build_a, package_b, evidence):
    if sys.platform != 'darwin':
        raise ValueError('Real macOS updater verification requires macOS')
    # Both installation and evidence are fresh; no caller-supplied existing app
    # location, data directory, or Keychain identifier is accepted.
    evidence.mkdir(parents=True, exist_ok=False)
    evidence.chmod(0o700)
    install = Path.home() / 'Applications' / ('Pngr-update-verification-' + uuid.uuid4().hex)
    install.mkdir(parents=True, exist_ok=False)
    install.chmod(0o700)
    app = install / 'pngr.app'
    data = evidence / 'isolated-user-data'
    state = evidence / 'disposable-keychain-state.json'
    report = {'installation': str(app), 'data': str(data), 'passed': False,
              'scope': 'local signed update; Apple notarization is not exercised by this test'}
    created = False
    try:
        before = signature(build_a)
        # Validate and extract B for expected-payload comparison only. The actual
        # installed bundle is replaced exclusively by UpdateMac, never this copy.
        expected_b = evidence / 'expected-b.app'
        RELEASE.extract_update(package_b, expected_b)
        after = signature(expected_b)
        if before['version'] == after['version'] or before['app_cdhash'] == after['app_cdhash']:
            raise ValueError('Builds A and B must have distinct release versions and application hashes')
        if before['host_requirement'] != after['host_requirement']:
            raise ValueError('Builds A and B do not share a stable host designated requirement')
        report.update(build_a=before, build_b=after, package_sha256=hashlib.sha256(package_b.read_bytes()).hexdigest())
        run('ditto', build_a, app)
        seed(data)
        capture(app, data, evidence, 'before-update')
        baseline = snapshot(data)
        workspace = json.loads((data / 'workspace.json').read_text())
        report['before_data_hashes'] = inventory(data)
        report['keychain_create'] = probe(app, 'create', state)
        created = True
        report['keychain_reads_a'] = [probe(app, 'read', state) for _ in range(2)]
        cache = evidence / 'update-cache'
        cache.mkdir()
        # Protect source package even if an invalid package makes UpdateMac delete
        # its supplied input. Only a disposable copy is passed to the updater.
        disposable_package = cache / package_b.name
        run('ditto', package_b, disposable_package)
        run(*apply_arguments(app, disposable_package, evidence), timeout=180)
        if inventory(app) != inventory(expected_b):
            raise ValueError('UpdateMac result does not exactly match the signed Build B package')
        report['installed_b'] = signature(app)
        assert_data(data, baseline, workspace)
        report['keychain_reads_b'] = [probe(app, 'read', state) for _ in range(2)]
        report['keychain_read_authenticated_child_b'] = probe(app, 'read', state, isolated=True)
        capture(app, data, evidence, 'after-update')
        assert_data(data, baseline, workspace)
        report['keychain_read_after_restart'] = probe(app, 'read', state, isolated=True)
        report['after_data_hashes'] = inventory(data)
        report['preserved_sessions'] = len(baseline['sessions'])
        report['preserved_samples'] = len(baseline['samples'])
        report['passed'] = True
    except Exception as error:
        report['failure_type'] = type(error).__name__
        if isinstance(error, ValueError):
            report['failure'] = str(error)
        raise
    finally:
        if created:
            try:
                report['keychain_cleanup'] = probe(app, 'cleanup', state)
            except Exception:
                report['passed'] = False
                report['cleanup_failed'] = True
                # State retains only random ID and hash for explicit safe retry.
                print('Disposable credential cleanup failed; state retained. No other credentials touched.', file=sys.stderr)
        (evidence / 'verification.json').write_text(json.dumps(report, indent=2))
    if not report['passed']:
        raise ValueError('Update or disposable credential cleanup verification failed')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-a', type=Path, required=True)
    parser.add_argument('--package-b', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True, help='New local evidence directory; never upload this directory publicly')
    args = parser.parse_args()
    try:
        verify(args.build_a.resolve(), args.package_b.resolve(), args.evidence.resolve())
    except Exception as error:
        print(f'Controlled update verification failed ({type(error).__name__}); local evidence retained.', file=sys.stderr)
        return 1
    print('Real Velopack update, isolated application data and noninteractive disposable Keychain continuity verified.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
