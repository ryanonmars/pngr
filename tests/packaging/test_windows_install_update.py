"""Safety and data checks used by the native Windows lifecycle smoke."""
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('windows_lifecycle', ROOT / 'scripts/windows/verify-install-update.py')
LIFECYCLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LIFECYCLE)


class WindowsLifecycleTests(unittest.TestCase):
    def test_baseline_download_requires_matching_published_checksum(self):
        payload = b'official old setup'
        manifest = f'{hashlib.sha256(payload).hexdigest()}  pngr-Setup.exe\n'.encode()
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(LIFECYCLE, 'download', side_effect=[manifest, payload]):
                self.assertEqual(payload, LIFECYCLE.download_verified_baseline(Path(directory)).read_bytes())
            with mock.patch.object(LIFECYCLE, 'download', side_effect=[manifest, b'corrupt']):
                with self.assertRaisesRegex(ValueError, 'checksum'):
                    LIFECYCLE.download_verified_baseline(Path(directory))

    def test_update_preservation_checks_exact_saved_measurements(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            config = {'Name': 'Windows installer compatibility', 'RetentionDays': 90,
                      'Profiles': [{'Name': 'Compatibility profile'}],
                      'Targets': [{'Group': 'Compatibility group'}], 'WebEnabled': False}
            (data / 'workspace.json').write_text(json.dumps(config))
            with closing(sqlite3.connect(data / 'tracescope.db')) as database, database:
                database.execute('CREATE TABLE sessions (id TEXT, ended TEXT)')
                database.execute('CREATE TABLE samples (id INTEGER, latency REAL)')
                database.execute("INSERT INTO sessions VALUES ('saved', NULL)")
                database.execute('INSERT INTO samples VALUES (1, 3.14)')
            baseline = LIFECYCLE.database_snapshot(data)
            LIFECYCLE.assert_preserved(data, baseline)
            with closing(sqlite3.connect(data / 'tracescope.db')) as database, database:
                database.execute("UPDATE sessions SET ended = 'now'")
                database.execute("INSERT INTO sessions VALUES ('new', NULL)")
                database.execute('INSERT INTO samples VALUES (2, 5.5)')
            LIFECYCLE.assert_preserved(data, baseline)
            with closing(sqlite3.connect(data / 'tracescope.db')) as database, database:
                database.execute('UPDATE samples SET latency = 100 WHERE id = 1')
            with self.assertRaisesRegex(ValueError, 'measurements'):
                LIFECYCLE.assert_preserved(data, baseline)

    def test_uninstall_waits_for_delayed_root_deletion_after_app_disappears(self):
        with tempfile.TemporaryDirectory() as directory:
            installation = Path(directory) / 'pngr'
            installation.mkdir()
            (installation / 'Update.exe').write_bytes(b'updater')
            current = installation / 'current'
            current.mkdir()
            # No current/pngr.exe remains, but detached self-deletion is still
            # pending. A fresh setup cannot safely start at this point.
            polls = []
            def finish_detached_cleanup(seconds):
                self.assertTrue(installation.exists())
                polls.append(seconds)
                if len(polls) == 3:
                    (installation / 'Update.exe').unlink()
                    current.rmdir()
                    installation.rmdir()
            with mock.patch.object(LIFECYCLE, 'run') as run:
                with mock.patch.object(LIFECYCLE.time, 'sleep', side_effect=finish_detached_cleanup):
                    LIFECYCLE.uninstall(installation)
            run.assert_called_once_with([installation / 'Update.exe', '--silent', 'uninstall'])
            self.assertEqual(3, len(polls))
            self.assertFalse(installation.exists())

    def test_uninstall_deadline_reports_remaining_files_without_deleting_them(self):
        with tempfile.TemporaryDirectory() as directory:
            installation = Path(directory) / 'pngr'
            installation.mkdir()
            residual = installation / 'Update.exe'
            residual.write_bytes(b'locked updater')
            with mock.patch.object(LIFECYCLE.time, 'monotonic', side_effect=[0, 0, 61]):
                with mock.patch.object(LIFECYCLE.time, 'sleep'):
                    with self.assertRaisesRegex(ValueError, 'installation root.*Update.exe'):
                        LIFECYCLE.wait_for_uninstall(installation)
            self.assertEqual(b'locked updater', residual.read_bytes())

    def test_uninstall_of_absent_root_returns_without_delay(self):
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(LIFECYCLE.time, 'sleep') as sleep:
                LIFECYCLE.uninstall(Path(directory) / 'absent')
            sleep.assert_not_called()

    def test_http_download_is_rejected_before_network_access(self):
        with self.assertRaisesRegex(ValueError, 'HTTPS'):
            LIFECYCLE.download('http://example.invalid/setup.exe', 100)


if __name__ == '__main__':
    unittest.main()
