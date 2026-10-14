#!/usr/bin/env python3
"""Native Windows installer/update smoke, including official 0.7.1 compatibility.

This tests silent installation and real application restarts, not interactive
wizard usability. Run only in a disposable Windows CI account with no Pngr
installation. Application data is isolated from the normal user data path.
"""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.request

LEGACY_RELEASE = 'https://github.com/ryanonmars/pngr/releases/download/v0.7.1/'


def download_verified_baseline(directory: Path):
    manifest = download(LEGACY_RELEASE + 'SHA256SUMS', 128 * 1024)
    expected = {}
    for line in manifest.decode('ascii').splitlines():
        checksum, name = line.split('  ', 1)
        expected[name] = checksum
    name = 'pngr-Setup.exe'
    payload = download(LEGACY_RELEASE + name, 150 * 1024 * 1024)
    if hashlib.sha256(payload).hexdigest() != expected[name]:
        raise ValueError('Official v0.7.1 setup failed published checksum verification.')
    path = directory / name
    path.write_bytes(payload)
    return path


def download(url: str, limit: int):
    if not url.startswith('https://'):
        raise ValueError('Installer downloads require HTTPS.')
    request = urllib.request.Request(url, headers={'User-Agent': 'Pngr-installer-validation'})
    with urllib.request.urlopen(request, timeout=90) as response:
        if not response.url.startswith('https://'):
            raise ValueError('Installer redirect requires HTTPS.')
        raw = response.read(limit + 1)
        if len(raw) > limit:
            raise ValueError('Installer download exceeded its safety limit.')
        return raw


def run(arguments, success=True):
    result = subprocess.run([str(value) for value in arguments], timeout=180)
    if success and result.returncode != 0:
        raise ValueError(f'Command failed ({result.returncode}): {arguments[0]}')
    if not success and result.returncode == 0:
        raise ValueError('Corrupt update unexpectedly succeeded.')
    return result


def version(installation: Path):
    deps = json.loads((installation / 'current/pngr.deps.json').read_text())
    versions = [key.removeprefix('pngr/') for key in deps['libraries'] if key.startswith('pngr/')]
    if len(versions) != 1:
        raise ValueError('Installed application version is ambiguous.')
    return versions[0]


def assert_installation(installation: Path, expected_version: str):
    for name in ('Update.exe', 'current/pngr.exe', 'current/sq.version'):
        if not (installation / name).is_file():
            raise ValueError(f'Missing installed file: {name}')
    if version(installation) != expected_version:
        raise ValueError(f'Installed application does not report {expected_version}.')
    # Velopack owns Add/Remove Programs and a stable Start menu shortcut.
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Microsoft\Windows\CurrentVersion\Uninstall\pngr') as key:
        uninstall, _ = winreg.QueryValueEx(key, 'UninstallString')
        display_name, _ = winreg.QueryValueEx(key, 'DisplayName')
        if expected_version != '0.7.1' and display_name != 'Pngr':
            raise ValueError('Installed application did not update its Windows branding.')
        if 'Update.exe' not in uninstall:
            raise ValueError('Uninstall entry does not use Velopack.')
    start_menu = Path(os.environ['APPDATA']) / 'Microsoft/Windows/Start Menu/Programs'
    if not any(path.name.lower() == 'pngr.lnk' for path in start_menu.rglob('*.lnk')):
        raise ValueError('Missing Pngr Start menu shortcut.')


def capture(installation: Path, data: Path, output: Path):
    run([installation / 'current/pngr.exe', '--data-dir', data,
         '--capture', output, '--capture-after', '4', '--exit-after-capture'])
    if not output.is_file() or output.stat().st_size < 1024:
        raise ValueError('Installed application did not produce a native UI capture.')
    details = json.loads(output.with_suffix('.json').read_text())
    if not details['Targets'] or details['Targets'][0]['Target'] != '127.0.0.1':
        raise ValueError('Installed application did not restore its loopback target.')
    return details


def database_snapshot(data: Path):
    with closing(sqlite3.connect(data / 'tracescope.db')) as database:
        return {table: database.execute(f'SELECT * FROM {table}').fetchall()
                for table in ('sessions', 'samples')}


def assert_preserved(data: Path, baseline):
    actual = database_snapshot(data)
    for table, records in baseline.items():
        # Sessions can acquire an ended timestamp; primary identity and samples
        # are immutable. All saved measurements must remain exact.
        if table == 'sessions':
            if not {row[0] for row in records}.issubset({row[0] for row in actual[table]}):
                raise ValueError('Saved sessions were removed during updating.')
        elif not set(records).issubset(set(actual[table])):
            raise ValueError('Saved measurements were changed or removed during updating.')
    config = json.loads((data / 'workspace.json').read_text())
    if config['Name'] != 'Windows installer compatibility' or config['RetentionDays'] != 90:
        raise ValueError('Workspace/settings were not preserved.')
    if not any(profile['Name'] == 'Compatibility profile' for profile in config['Profiles']):
        raise ValueError('Saved probing profile was not preserved.')
    if config['Targets'][0]['Group'] != 'Compatibility group' or config['WebEnabled']:
        raise ValueError('Target configuration changed or optional API was enabled.')


def uninstall(installation: Path):
    updater = installation / 'Update.exe'
    if updater.is_file():
        run([updater, '--silent', 'uninstall'])
    for _ in range(40):
        if not (installation / 'current/pngr.exe').exists():
            return
        time.sleep(.25)
    raise ValueError('Velopack uninstall did not remove the installed executable.')


def verify(setup: Path, package: Path, expected_version: str, evidence: Path):
    if os.name != 'nt':
        raise ValueError('Native Windows validation requires Windows.')
    installation = Path(os.environ['LOCALAPPDATA']) / 'pngr'
    if installation.exists():
        raise ValueError('Refusing to modify an existing installation. Use a disposable Windows CI account.')
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='Pngr-installer-validation-') as temporary:
        temporary = Path(temporary)
        data = temporary / 'isolated-user-data'
        data.mkdir()
        options = {'Protocol': 'Icmp', 'Interval': '00:00:01', 'Timeout': '00:00:00.500', 'MaxHops': 4}
        (data / 'workspace.json').write_text(json.dumps({
            'Name': 'Windows installer compatibility', 'RetentionDays': 90,
            'WebEnabled': False, 'DesktopNotifications': False,
            'Targets': [{'Target': '127.0.0.1', 'Options': options, 'Group': 'Compatibility group'}],
            'Profiles': [{'Name': 'Compatibility profile', 'Options': options}]}))
        try:
            # Fresh conventional wrapper: silent mode must not launch the app.
            run([setup, '/S'])
            assert_installation(installation, expected_version)
            capture(installation, data, evidence / 'windows-fresh-install.png')
            uninstall(installation)
            baseline_setup = download_verified_baseline(temporary)
            run([baseline_setup, '--silent'])
            assert_installation(installation, '0.7.1')
            capture(installation, data, evidence / 'windows-v071-before-update.png')
            baseline = database_snapshot(data)
            if not baseline['sessions'] or not baseline['samples']:
                raise ValueError('Loopback monitoring did not persist real historical measurements.')
            original = hashlib.sha256((installation / 'current/pngr.exe').read_bytes()).hexdigest()
            broken = temporary / package.name
            broken.write_bytes(package.read_bytes()[:4096])
            run([installation / 'Update.exe', '--silent', 'apply', '--package', broken, '--norestart'], success=False)
            if hashlib.sha256((installation / 'current/pngr.exe').read_bytes()).hexdigest() != original:
                raise ValueError('Failed update changed the installed application.')
            assert_preserved(data, baseline)
            # Use the updater from 0.7.1, establishing backwards compatibility.
            run([installation / 'Update.exe', '--silent', 'apply', '--package', package, '--norestart'])
            assert_installation(installation, expected_version)
            capture(installation, data, evidence / 'windows-after-update.png')
            assert_preserved(data, baseline)
            (evidence / 'windows-install-update-results.json').write_text(json.dumps({
                'From': '0.7.1', 'To': expected_version,
                'FreshWizardSilentInstall': True, 'NativeApplicationRestart': True,
                'CorruptUpdateRejected': True, 'HistorySettingsProfilesPreserved': True,
                'InteractiveWizardManuallyVerified': False}, indent=2))
        finally:
            uninstall(installation)
    print(f'Verified native Windows installer and 0.7.1 → {expected_version} update; interactive wizard remains manual testing.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('setup', type=Path)
    parser.add_argument('package', type=Path)
    parser.add_argument('version')
    parser.add_argument('--evidence', type=Path, default=Path('artifacts/screenshots/windows-installation'))
    args = parser.parse_args()
    verify(args.setup.resolve(), args.package.resolve(), args.version.removeprefix('v'), args.evidence)
