"""Local signing command construction; never accesses a real signing key."""
import importlib.util
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/macos/sign-local.py"
SPEC = importlib.util.spec_from_file_location("local_signer", SCRIPT)
SIGNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNER)
IDENTITY = "Developer ID Application: Ryan Poelker (VRNY8KM56D)"
TEAM = "VRNY8KM56D"


class LocalSigningTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.bundle = Path(temporary.name) / "pngr.app"
        self.payload = self.bundle / "Contents/MacOS"
        self.payload.mkdir(parents=True)
        (self.bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": "dev.tracescope.desktop", "CFBundleExecutable": "pngr"}))
        for name in ("pngr", "helper", "native.dylib"):
            (self.payload / name).write_bytes(bytes.fromhex("cffaedfe") + bytes(28))
        (self.payload / "managed.dll").write_bytes(b"MZ managed image")

    def details(self, team=TEAM, flags="0x10000(runtime)"):
        return subprocess.CompletedProcess([], 0, "", f"CodeDirectory v=20500 flags={flags}\nAuthority={IDENTITY}\nTeamIdentifier={team}\n")

    def test_only_native_files_are_signed_before_outer_bundle(self):
        (self.payload / "alias.dylib").symlink_to("native.dylib")
        with mock.patch.object(SIGNER.subprocess, "run", return_value=self.details()) as run:
            SIGNER.sign(self.bundle, IDENTITY, TEAM)
        calls = [call.args[0] for call in run.call_args_list]
        nested, managed, outer, verify = calls[:4]
        self.assertEqual(nested[-2:], [str(self.payload / "helper"), str(self.payload / "native.dylib")])
        self.assertNotIn("--deep", nested)
        self.assertNotIn("--entitlements", nested)
        self.assertIn("--timestamp", nested)
        self.assertEqual(nested[nested.index("--options") + 1], "runtime")
        self.assertEqual(managed[-1], str(self.payload / "managed.dll"))
        self.assertIn(IDENTITY, managed)
        self.assertNotIn("--entitlements", managed)
        self.assertNotIn("--options", managed)
        self.assertIn("--entitlements", outer)
        self.assertEqual(outer[-1], str(self.bundle))
        self.assertEqual(verify, ["codesign", "--verify", "--deep", "--strict", "--all-architectures", str(self.bundle)])
        self.assertEqual(len(calls[4:]), 4)  # Verify identity on native files, managed assembly, and app.

    def test_unexpected_identity_or_team_cannot_start_signing(self):
        with mock.patch.object(SIGNER.subprocess, "run") as run:
            for identity, team in (("-", TEAM), (IDENTITY, "OTHER"), (IDENTITY, "")):
                with self.assertRaises(ValueError):
                    SIGNER.sign(self.bundle, identity, team)
            run.assert_not_called()

    def test_nested_signing_failure_stops_before_signing_bundle(self):
        with mock.patch.object(SIGNER.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "codesign")) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                SIGNER.sign(self.bundle, IDENTITY, TEAM)
            self.assertEqual(run.call_count, 1)

    def test_wrong_team_or_missing_runtime_is_rejected(self):
        for details in (self.details(team="OTHER"), self.details(flags="0x0(none)")):
            with self.subTest(details=details.stderr), mock.patch.object(SIGNER.subprocess, "run", return_value=details):
                with self.assertRaises(ValueError):
                    SIGNER.sign(self.bundle, IDENTITY, TEAM)

    def test_unexpected_bundle_identifier_is_preserved_by_refusing_to_sign(self):
        metadata = self.bundle / "Contents/Info.plist"
        metadata.write_bytes(plistlib.dumps({"CFBundleIdentifier": "unexpected", "CFBundleExecutable": "pngr"}))
        with mock.patch.object(SIGNER.subprocess, "run") as run:
            with self.assertRaises(ValueError):
                SIGNER.sign(self.bundle, IDENTITY, TEAM)
            run.assert_not_called()
