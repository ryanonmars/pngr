"""Controlled updater harness tests never access native Keychain or user data."""
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import plistlib
import subprocess
import sqlite3
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('macos_install_update', Path(__file__).resolve().parents[2] / 'scripts/macos/verify-install-update.py')
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


class MacInstallUpdateTests(unittest.TestCase):
    def test_apply_uses_real_updater_and_isolated_root_cache_without_restart(self):
        root = Path('/isolated/pngr.app')
        args = VERIFY.apply_arguments(root, Path('/evidence/test.nupkg'), Path('/evidence'))
        self.assertEqual(args[:2], [root / 'Contents/MacOS/UpdateMac', 'apply'])
        self.assertIn('--norestart', args)
        self.assertIn('--silent', args)
        self.assertEqual(args[args.index('--rootDir') + 1], root)
        self.assertEqual(args[args.index('--packageDir') + 1], Path('/evidence/update-cache'))
        self.assertEqual(args[args.index('--package') + 1], Path('/evidence/test.nupkg'))

    def test_probe_uses_only_explicit_disposable_noninteractive_diagnostic(self):
        for phase, message in [('create', 'created.'), ('read', 'read verified.'), ('cleanup', 'removed.')]:
            with self.subTest(phase=phase), mock.patch.object(VERIFY, 'run', return_value='authorization UI disabled\n' + message) as run:
                VERIFY.probe(Path('/app'), phase, Path('/state'))
                self.assertEqual(run.call_args.args[1:], ('--verify-keychain-signature', phase, Path('/state')))
                self.assertEqual(run.call_args.kwargs['timeout'], 30)
        with mock.patch.object(VERIFY, 'run', return_value='created.'):
            with self.assertRaises(ValueError):
                VERIFY.probe(Path('/app'), 'create', Path('/state'))

    def test_read_exercises_production_authenticated_child(self):
        with mock.patch.object(VERIFY, 'run', return_value='Disposable signature-probe credential read verified.') as run:
            VERIFY.probe(Path('/app'), 'read', Path('/state'), isolated=True)
            self.assertEqual(run.call_args.args[1], '--verify-keychain-signature-isolated')
        with mock.patch.object(VERIFY, 'run') as run:
            with self.assertRaises(ValueError):
                VERIFY.probe(Path('/app'), 'create', Path('/state'), isolated=True)
            run.assert_not_called()

    def test_inventory_preserves_symlink_targets(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'file').write_text('payload')
            (root / 'symlink').symlink_to('file')
            result = VERIFY.inventory(root)
            self.assertEqual(result['symlink'], 'link:file')
            self.assertEqual(len(result['file']), 64)

    def test_signature_uses_full_prerelease_version_and_separate_bundle_hash(self):
        with tempfile.TemporaryDirectory() as folder:
            app = Path(folder) / 'pngr.app'
            (app / 'Contents').mkdir(parents=True)
            metadata = {'CFBundleIdentifier': VERIFY.RELEASE.BUNDLE_ID,
                        'CFBundleShortVersionString': '0.8.0',
                        'pngrReleaseVersion': '0.8.0-updatecheck.123.1'}
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps(metadata))
            results = [subprocess.CompletedProcess([], 0, '', 'CDHash=aaaa\n'),
                       subprocess.CompletedProcess([], 0, '', 'designated => identifier "dev.tracescope.desktop" and certificate leaf[subject.OU] = "VRNY8KM56D"\n'),
                       subprocess.CompletedProcess([], 0, '', 'CDHash=bbbb\n')]
            with mock.patch.object(VERIFY, 'run'), mock.patch.object(VERIFY.RELEASE, 'native', return_value=False), \
                    mock.patch.object(VERIFY.subprocess, 'run', side_effect=results):
                result = VERIFY.signature(app)
            self.assertEqual(result['version'], metadata['pngrReleaseVersion'])
            self.assertEqual(result['bundle_version'], '0.8.0')
            self.assertEqual(result['app_cdhash'], 'bbbb')
            self.assertEqual(result['host_cdhash'], 'aaaa')
            metadata['pngrReleaseVersion'] = '0.9.0-other'
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps(metadata))
            with mock.patch.object(VERIFY, 'run'), mock.patch.object(VERIFY.RELEASE, 'native', return_value=False), \
                    mock.patch.object(VERIFY.subprocess, 'run', side_effect=results):
                with self.assertRaises(ValueError):
                    VERIFY.signature(app)

    def test_requires_native_macos_before_creating_directories(self):
        with mock.patch.object(VERIFY.sys, 'platform', 'linux'), mock.patch.object(VERIFY.Path, 'mkdir') as mkdir:
            with self.assertRaises(ValueError):
                VERIFY.verify(Path('/a'), Path('/b'), Path('/evidence'))
            mkdir.assert_not_called()

    def test_requires_fresh_evidence_before_any_installation_or_credential_access(self):
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(VERIFY.sys, 'platform', 'darwin'), mock.patch.object(VERIFY, 'probe') as probe:
            with self.assertRaises(FileExistsError):
                VERIFY.verify(Path('/a'), Path('/b'), Path(folder))
            probe.assert_not_called()

    def test_seed_has_no_notification_credentials_or_external_targets(self):
        with tempfile.TemporaryDirectory() as folder:
            data = Path(folder) / 'data'
            VERIFY.seed(data)
            raw = (data / 'workspace.json').read_text()
            workspace = json.loads(raw)
            self.assertFalse(workspace['WebEnabled'])
            self.assertEqual(workspace['Targets'][0]['Target'], '127.0.0.1')
            self.assertNotIn('Webhook', raw)
            self.assertNotIn('Telegram', raw)

    def test_preservation_checks_history_and_profiles(self):
        with tempfile.TemporaryDirectory() as folder:
            data = Path(folder) / 'data'
            VERIFY.seed(data)
            with closing(sqlite3.connect(data / 'tracescope.db')) as db, db:
                db.execute('CREATE TABLE sessions (id TEXT, ended TEXT)')
                db.execute('INSERT INTO sessions VALUES ("session", NULL)')
                db.execute('CREATE TABLE samples (id TEXT, value INTEGER)')
                db.execute('INSERT INTO samples VALUES ("sample", 21)')
            baseline = VERIFY.snapshot(data)
            workspace = json.loads((data / 'workspace.json').read_text())
            VERIFY.assert_data(data, baseline, workspace)
            with closing(sqlite3.connect(data / 'tracescope.db')) as db, db:
                db.execute('UPDATE samples SET value = 999')
            with self.assertRaises(ValueError):
                VERIFY.assert_data(data, baseline, workspace)


if __name__ == '__main__':
    unittest.main()
