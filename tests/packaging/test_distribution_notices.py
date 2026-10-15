"""Delivered notices must survive every packaging stage, not just generation."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

import test_verify_package as PORTABLE
import test_installer_assets as INSTALLERS
import test_universal_macos as UNIVERSAL

ASSETS = INSTALLERS.ASSETS
WINDOWS = INSTALLERS.WINDOWS

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('distribution_notices_test', ROOT / 'scripts/verify-distribution-notices.py')
NOTICES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NOTICES)


def rewrite_without(path, ending):
    with zipfile.ZipFile(path) as archive:
        entries = {name: archive.read(name) for name in archive.namelist() if not name.endswith(ending)}
    with zipfile.ZipFile(path, 'w') as archive:
        for name, data in entries.items():
            archive.writestr(name, data)


class DistributionNoticeTests(unittest.TestCase):
    def test_each_portable_runtime_rejects_missing_notice_or_inventory(self):
        for rid in ('osx-arm64', 'osx-x64', 'win-x64'):
            for name in ('THIRD-PARTY-NOTICES.txt', 'third-party-inventory.json'):
                with self.subTest(rid=rid, name=name):
                    path = PORTABLE.ReleaseArchiveTests.create_archive(self, rid)
                    rewrite_without(path, name)
                    with self.assertRaisesRegex(ValueError, 'Missing third-party'):
                        PORTABLE.ReleaseArchiveTests.verify(self, path, rid)

    def test_update_packages_reject_stripped_notice_after_feed_integrity_is_updated(self):
        for channel in ('win-x64', 'osx-universal'):
            with self.subTest(channel=channel):
                root = INSTALLERS.InstallerAssetsTests.fixture(self)
                package = next(root.glob('*' + channel + '-full.nupkg'))
                rewrite_without(package, 'THIRD-PARTY-NOTICES.txt')
                feed = root / ('releases.' + channel + '.json')
                data = json.loads(feed.read_text())
                data['Assets'][0]['Size'] = package.stat().st_size
                data['Assets'][0]['SHA256'] = hashlib.sha256(package.read_bytes()).hexdigest()
                feed.write_text(json.dumps(data))
                with self.assertRaises((ValueError, KeyError)):
                    ASSETS.validate_feed(root, channel, '0.7.1')

    def test_universal_bundle_requires_both_exact_runtime_inventories(self):
        bundle = UNIVERSAL.UniversalPackageTests.bundle(self)
        (bundle / 'Contents/Resources/runtimes/osx-x64/third-party-inventory.json').unlink()
        with self.assertRaises(FileNotFoundError):
            NOTICES.verify_universal_directory(bundle)

    def test_private_reverse_engineering_material_is_rejected(self):
        for name in ('.rea/evidence.json', 'docs/internal/ip-compliance-review.md',
                     'helper.hop', 'native-pseudocode.txt', 'capture.pcapng'):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'Private analysis'):
                NOTICES.reject_private_material(name)

    def test_reviewed_nsis_version_is_enforced_before_compilation(self):
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(WINDOWS, 'nsis_compiler', return_value='/tools/makensis'), \
                 mock.patch.object(WINDOWS.subprocess, 'check_output', return_value='v99.0'), \
                 mock.patch.object(WINDOWS.subprocess, 'run') as compile:
                with self.assertRaisesRegex(ValueError, 'NSIS version differs'):
                    WINDOWS.build_wizard('0.7.5', Path(directory) / 'payload.exe', Path(directory) / 'setup.exe')
                compile.assert_not_called()
