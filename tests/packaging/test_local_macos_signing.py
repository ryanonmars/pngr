"""Local signing command construction; never accesses a real signing key."""
import importlib.util
from pathlib import Path
import plistlib
import subprocess
import shutil
import sys
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
        (self.payload / "pngr.runtimeconfig.json").write_text('{}')
        (self.payload / "pngr.deps.json").write_text('{}')

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
        self.assertEqual(managed[-3:], [str(self.payload / name) for name in
                                      ("managed.dll", "pngr.deps.json", "pngr.runtimeconfig.json")])
        self.assertIn(IDENTITY, managed)
        self.assertNotIn("--entitlements", managed)
        self.assertNotIn("--options", managed)
        self.assertIn("--entitlements", outer)
        self.assertEqual(outer[-1], str(self.bundle))
        self.assertEqual(verify, ["codesign", "--verify", "--deep", "--strict", "--all-architectures", str(self.bundle)])
        self.assertEqual(len(calls[4:]), 6)  # Verify identity on native files, managed assembly, and app.

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

    @unittest.skipUnless(sys.platform == "darwin" and shutil.which("clang"), "Requires macOS compiler and codesign")
    def test_runtime_json_requires_generic_signature_in_a_clean_bundle(self):
        # Disposable ad-hoc probe: exercises Apple's layout rules without a private key.
        with tempfile.TemporaryDirectory(prefix="pngr-local-signing-layout-") as directory:
            root = Path(directory)
            bundle = root / "probe.app"
            payload = bundle / "Contents/MacOS"
            payload.mkdir(parents=True)
            (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({
                "CFBundleIdentifier": "dev.tracescope.signing-layout-probe", "CFBundleExecutable": "probe"}))
            source = root / "main.c"
            source.write_text("int main(void) { return 0; }\n")
            subprocess.run(["clang", str(source), "-o", str(payload / "probe")], check=True, capture_output=True)
            data = payload / "probe.runtimeconfig.json"
            data.write_text('{}')
            unsigned = subprocess.run(["codesign", "--force", "--sign", "-", str(bundle)], capture_output=True)
            self.assertNotEqual(unsigned.returncode, 0)
            self.assertIn(b"probe.runtimeconfig.json", unsigned.stderr)
            subprocess.run(["codesign", "--force", "--sign", "-", str(data)], check=True, capture_output=True)
            subprocess.run(["codesign", "--force", "--sign", "-", str(bundle)], check=True, capture_output=True)
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True, capture_output=True)
