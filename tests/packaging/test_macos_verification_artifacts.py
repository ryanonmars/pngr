"""Inspection exports fail closed without credentials or Apple service calls."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/macos/verification-artifacts.py"
SPEC = importlib.util.spec_from_file_location("macos_inspection_artifacts", SCRIPT)
ARTIFACTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ARTIFACTS)


class VerificationArtifactTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "installers"
        self.source.mkdir()
        self.bundle = self.root / "pngr.app"
        self.output = self.root / "inspection"
        self.package = self.source / "pngr-0.8.0-osx-universal-full.nupkg"
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr("lib/app/Contents/Info.plist", "test metadata")
        raw = self.package.read_bytes()
        feed = {"Assets": [{"Version": "0.8.0", "Type": "Full", "FileName": self.package.name,
                            "Size": len(raw), "SHA256": hashlib.sha256(raw).hexdigest()}]}
        self.feed = self.source / "releases.osx-universal.json"
        self.feed.write_text(json.dumps(feed))
        (self.source / "Pngr-macos-universal.dmg").write_bytes(b"test image")
        self.checks = {}
        for name in ("verify_app", "verify_dmg", "verify_updates"):
            self.checks[name] = self.enterContext(mock.patch.object(ARTIFACTS.VERIFY, name))
        self.enterContext(mock.patch.object(ARTIFACTS.ASSETS.NOTICE_CHECKS, "verify_update_archive"))

    def prepare(self, **overrides):
        values = dict(source=self.source, bundle=self.bundle, version="0.8.0", output=self.output,
                      commit="a" * 40, run_id="123456")
        values.update(overrides)
        ARTIFACTS.prepare(**values)

    def test_only_finished_artifacts_and_allowlisted_report_are_staged(self):
        (self.source / "RELEASES-osx-universal").write_text("test release entry")
        self.prepare()
        self.assertEqual({path.name for path in self.output.iterdir()}, {
            self.package.name, "Pngr-macos-universal.dmg", "releases.osx-universal.json",
            "RELEASES-osx-universal", "verification.json", "SHA256SUMS"})
        report = json.loads((self.output / "verification.json").read_text())
        self.assertEqual(set(report), {"schema_version", "status", "commit", "run_id", "version",
                                       "team_id", "identity", "checks"})
        self.assertEqual(report["team_id"], "VRNY8KM56D")
        self.assertEqual(set(report["checks"].values()), {"passed"})
        self.assertNotIn(str(self.root), json.dumps(report))
        self.checks["verify_app"].assert_called_once_with(self.bundle)
        self.checks["verify_dmg"].assert_called_once_with(self.source / "Pngr-macos-universal.dmg")
        self.checks["verify_updates"].assert_called_once_with(self.source, "0.8.0", self.bundle)
        for line in (self.output / "SHA256SUMS").read_text().splitlines():
            digest, name = line.split("  ")
            self.assertEqual(digest, hashlib.sha256((self.output / name).read_bytes()).hexdigest())

    def test_native_verification_failure_copies_nothing(self):
        for name, checker in self.checks.items():
            with self.subTest(name=name):
                checker.side_effect = ValueError("untrusted diagnostic content")
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())
                checker.side_effect = None

    def test_feed_integrity_failure_copies_nothing(self):
        self.package.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "integrity mismatch"):
            self.prepare()
        self.assertFalse(self.output.exists())
        self.checks["verify_app"].assert_not_called()

    def test_unexpected_files_directories_and_symlinks_are_rejected(self):
        for name in ("certificate.p12", "private.p8", "signing.keychain-db", "logs.txt", "other.nupkg"):
            extra = self.source / name
            extra.write_bytes(b"not a real secret")
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.prepare()
            extra.unlink()
        extra = self.source / "private"
        extra.mkdir()
        with self.assertRaises(ValueError):
            self.prepare()
        extra.rmdir()
        dmg = self.source / "Pngr-macos-universal.dmg"
        dmg.unlink()
        dmg.symlink_to(self.package)
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_existing_output_is_preserved_and_not_reused(self):
        self.output.mkdir()
        existing = self.output / "old"
        existing.write_text("preserve")
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertEqual(existing.read_text(), "preserve")

    def test_unvalidated_metadata_cannot_enter_report(self):
        for values in ({"commit": "not a commit"}, {"commit": "b" * 41}, {"run_id": "secret/path"},
                       {"version": "0.8.0\nprivate"}, {"version": "../private"}, {"version": "01.8.0"}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                self.prepare(**values)
        self.assertFalse(self.output.exists())

    def test_multiple_feed_versions_are_rejected(self):
        feed = json.loads(self.feed.read_text())
        feed["Assets"].append(dict(feed["Assets"][0], Version="0.7.6"))
        self.feed.write_text(json.dumps(feed))
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_copy_failure_removes_partial_output(self):
        with mock.patch.object(ARTIFACTS.shutil, "copyfile", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.prepare()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
