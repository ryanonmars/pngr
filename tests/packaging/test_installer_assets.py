"""Installer release manifest verification without platform execution."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('release_assets', ROOT / 'scripts/release-assets.py')
ASSETS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ASSETS)
WINDOWS_SPEC = importlib.util.spec_from_file_location('windows_installer', ROOT / 'scripts/package-windows-installer.py')
WINDOWS = importlib.util.module_from_spec(WINDOWS_SPEC)
WINDOWS_SPEC.loader.exec_module(WINDOWS)


class InstallerAssetsTests(unittest.TestCase):
    def fixture(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        for name in ('Pngr-Setup.exe', 'Pngr-macos-universal.dmg'):
            (root / name).write_bytes(b'installer')
        for channel in ('win-x64', 'osx-universal'):
            package = root / f'pngr-0.7.1-{channel}-full.nupkg'
            with zipfile.ZipFile(package, 'w') as archive:
                archive.writestr('lib/app/pngr.dll', b'application')
            payload = package.read_bytes()
            (root / f'releases.{channel}.json').write_text(json.dumps({'Assets': [{
                'Version': '0.7.1', 'Type': 'Full', 'FileName': package.name,
                'Size': len(payload), 'SHA256': hashlib.sha256(payload).hexdigest()}]}))
        return root

    def test_checksums_cover_all_public_assets(self):
        root = self.fixture()
        ASSETS.prepare(root, '0.7.1')
        manifest = (root / 'SHA256SUMS').read_text().splitlines()
        self.assertEqual(6, len(manifest))
        for line in manifest:
            checksum, name = line.split('  ')
            self.assertEqual(checksum, hashlib.sha256((root / name).read_bytes()).hexdigest())

    def test_wrong_version_is_rejected(self):
        with self.assertRaises(ValueError):
            ASSETS.prepare(self.fixture(), '0.7.2')

    def test_interrupted_or_tampered_update_is_rejected(self):
        root = self.fixture()
        next(root.glob('*win-x64-full.nupkg')).write_bytes(b'truncated')
        with self.assertRaisesRegex(ValueError, 'integrity mismatch'):
            ASSETS.prepare(root, '0.7.1')

    def test_missing_installer_is_rejected(self):
        root = self.fixture()
        (root / 'Pngr-Setup.exe').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing installer'):
            ASSETS.prepare(root, '0.7.1')

    def test_extract_failure_is_rejected_before_publication(self):
        root = self.fixture()
        package = next(root.glob('*osx-universal-full.nupkg'))
        with zipfile.ZipFile(package, 'a') as archive:
            archive.writestr('lib/app', b'file where a directory is required')
        feed = root / 'releases.osx-universal.json'
        metadata = json.loads(feed.read_text())
        metadata['Assets'][0]['Size'] = package.stat().st_size
        metadata['Assets'][0]['SHA256'] = hashlib.sha256(package.read_bytes()).hexdigest()
        feed.write_text(json.dumps(metadata))
        with self.assertRaisesRegex(ValueError, 'file/directory collision'):
            ASSETS.prepare(root, '0.7.1')

    def test_portable_archives_are_not_public_assets(self):
        root = self.fixture()
        (root / 'pngr-win-x64.zip').write_bytes(b'old download')
        with self.assertRaisesRegex(ValueError, 'Unexpected public asset'):
            ASSETS.prepare(root, '0.7.1')

    def test_failed_packaging_preserves_previous_installer(self):
        root = self.fixture()
        publish = root / 'publish'
        publish.mkdir()
        (publish / 'pngr.deps.json').write_text(json.dumps({
            'libraries': {'pngr/0.7.1': {}},
            'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0/win-x64'}}))
        output = root / 'installed-assets'
        output.mkdir()
        (output / 'Pngr-Setup.exe').write_bytes(b'previous-valid-installer')
        with mock.patch.object(WINDOWS.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, ['vpk'])):
            with self.assertRaises(subprocess.CalledProcessError):
                WINDOWS.package('0.7.1', publish, output, '/sdk/dotnet')
        self.assertEqual(b'previous-valid-installer', (output / 'Pngr-Setup.exe').read_bytes())
        self.assertFalse(output.with_name(output.name + '.pending').exists())

    def test_installer_requires_matching_version_and_windows_runtime(self):
        root = self.fixture()
        publish = root / 'publish'
        publish.mkdir()
        (publish / 'pngr.deps.json').write_text(json.dumps({
            'libraries': {'pngr/0.7.1': {}},
            'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0/osx-arm64'}}))
        with self.assertRaisesRegex(ValueError, 'win-x64'):
            WINDOWS.package('0.7.1', publish, root / 'output', '/sdk/dotnet')
        with self.assertRaisesRegex(ValueError, 'version'):
            WINDOWS.package('0.7.2', publish, root / 'output', '/sdk/dotnet')

    def test_framework_tool_version_is_pinned(self):
        manifest = json.loads((ROOT / '.config/dotnet-tools.json').read_text())
        self.assertEqual('1.2.161', manifest['tools']['vpk']['version'])


if __name__ == '__main__':
    unittest.main()
