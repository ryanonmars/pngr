"""Release verification is mocked: no private keys, Apple services or credentials."""
import importlib.util
from pathlib import Path
import plistlib
import stat
import subprocess
import tempfile
import unittest
from unittest import mock
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/macos/verify-release.py"
SPEC = importlib.util.spec_from_file_location("release_verification", SCRIPT)
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


class ReleaseVerificationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bundle = self.root / "pngr.app"

    def details(self, team=VERIFY.TEAM, timestamp=True, runtime=True):
        return subprocess.CompletedProcess([], 0, "", "\n".join([
            f"Authority={VERIFY.IDENTITY}", f"TeamIdentifier={team}",
            f"Identifier={VERIFY.BUNDLE_ID}",
            "CodeDirectory v=20500 flags=" + ("0x10000(runtime)" if runtime else "0x0(none)"),
            *( ["Timestamp=Mar 5, 2026 at 12:00:00 PM"] if timestamp else []), ""]))

    def test_signature_requires_identity_team_timestamp_and_runtime(self):
        for details in (self.details(team="OTHER"), self.details(timestamp=False), self.details(runtime=False)):
            with self.subTest(details=details.stderr), mock.patch.object(VERIFY.subprocess, "run", return_value=details):
                with self.assertRaises(ValueError):
                    VERIFY.inspect_signature(self.bundle, "arm64", {}, VERIFY.BUNDLE_ID)

    def test_entitlements_are_exact_and_debugging_is_rejected(self):
        for value in ({"com.apple.security.get-task-allow": True}, {**VERIFY.JIT, "extra": True},
                      {"com.apple.security.cs.allow-jit": 1}):
            with mock.patch.object(VERIFY.subprocess, "run", return_value=self.details()), \
                    mock.patch.object(VERIFY, "run", return_value=plistlib.dumps(value)):
                with self.assertRaises(ValueError):
                    VERIFY.inspect_signature(self.bundle, "arm64", VERIFY.JIT, VERIFY.BUNDLE_ID)
        with mock.patch.object(VERIFY.subprocess, "run", return_value=self.details()), \
                mock.patch.object(VERIFY, "run", return_value=plistlib.dumps(VERIFY.JIT)):
            VERIFY.inspect_signature(self.bundle, "arm64", VERIFY.JIT, VERIFY.BUNDLE_ID)

    def make_app(self):
        contents = self.bundle / "Contents"
        contents.mkdir(parents=True)
        (contents / "Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": VERIFY.BUNDLE_ID,
                                                            "CFBundleExecutable": "pngr"}))
        for path in ("MacOS/pngr", "MacOS/UpdateMac", "Resources/runtimes/osx-arm64/pngr",
                     "Resources/runtimes/osx-x64/pngr", "Resources/runtimes/osx-arm64/native.dylib"):
            file = contents / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(bytes.fromhex("cffaedfe") + bytes(28))

    def test_all_native_slices_and_expected_entitlements_are_verified(self):
        self.make_app()
        with mock.patch.object(VERIFY, "run", return_value=b"arm64 x86_64"), \
                mock.patch.object(VERIFY, "inspect_signature") as inspect:
            VERIFY.verify_app(self.bundle)
        calls = inspect.call_args_list
        self.assertEqual(len(calls), 12)
        for call in calls:
            path, arch, entitlements, *identifier = call.args
            self.assertIn(arch, ("arm64", "x86_64"))
            if "/runtimes/" in str(path) and path.name == "pngr":
                self.assertEqual(entitlements, VERIFY.JIT)
                self.assertEqual(identifier, [VERIFY.BUNDLE_ID])
            elif path.name == "UpdateMac":
                self.assertEqual(entitlements, VERIFY.UPDATER)
            else:
                self.assertEqual(entitlements, {})

    def test_stapler_or_gatekeeper_failure_is_fatal(self):
        self.make_app()
        for tool in ("stapler", "spctl"):
            def invoke(*args):
                if tool in args:
                    raise subprocess.CalledProcessError(1, list(args))
                return b"arm64 x86_64"
            with mock.patch.object(VERIFY, "run", side_effect=invoke), mock.patch.object(VERIFY, "inspect_signature"):
                with self.assertRaises(subprocess.CalledProcessError):
                    VERIFY.verify_app(self.bundle)

    def test_dmg_requires_signature_staple_and_gatekeeper(self):
        with mock.patch.object(VERIFY, "run") as run, mock.patch.object(VERIFY, "inspect_signature") as inspect:
            VERIFY.verify_dmg(self.root / "test.dmg")
        self.assertEqual(run.call_args_list[0].args[:3], ("codesign", "--verify", "--strict"))
        self.assertEqual(run.call_args_list[1].args[:3], ("xcrun", "stapler", "validate"))
        self.assertIn("context:primary-signature", run.call_args_list[2].args)
        inspect.assert_called_once()

    def package(self, entries):
        package = self.root / "pngr-0.8.0-osx-universal-full.nupkg"
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("lib/app/Contents/Info.plist", b"metadata")
            for name, data in entries.items():
                archive.writestr(name, data)
        return package

    def test_update_extraction_decodes_velopack_links_and_normalizes_permissions(self):
        package = self.package({"lib/app/Contents/Resources/sq.version": b"version",
                                "lib/app/Contents/MacOS/sq.version.__symlink": b"../Resources/sq.version"})
        VERIFY.extract_update(package, self.bundle)
        link = self.bundle / "Contents/MacOS/sq.version"
        self.assertTrue(link.is_symlink())
        self.assertEqual(link.read_bytes(), b"version")
        self.assertEqual(stat.S_IMODE((self.bundle / "Contents/Info.plist").stat().st_mode), 0o755)

    def test_unsafe_update_paths_and_links_fail_before_extraction(self):
        for name, value in (("../escape", b"bad"), ("/escape", b"bad"), ("lib\\app\\escape", b"bad"),
                            ("lib/app/escape.__symlink", b"../../../escape"),
                            ("lib/app/escape.__symlink", b"/tmp/escape"),
                            ("lib/app/Contents/Info.plist/file", b"bad")):
            with self.subTest(name=name, value=value):
                package = self.package({name: value})
                with self.assertRaises(ValueError):
                    VERIFY.extract_update(package, self.bundle)
                self.assertFalse(self.bundle.exists())

    def test_decoded_link_collision_is_rejected(self):
        package = self.package({"lib/app/file": b"x", "lib/app/file.__symlink": b"Contents/Info.plist"})
        with self.assertRaises(ValueError):
            VERIFY.extract_update(package, self.bundle)

    def test_update_manifest_detects_changed_bytes_and_symlinks(self):
        self.package({"lib/app/Contents/file": b"original"})
        reference = self.root / "reference.app"
        (reference / "Contents").mkdir(parents=True)
        (reference / "Contents/Info.plist").write_bytes(b"metadata")
        file = reference / "Contents/file"
        file.write_bytes(b"different")
        with mock.patch.object(VERIFY, "verify_app") as verify:
            with self.assertRaises(ValueError):
                VERIFY.verify_updates(self.root, "0.8.0", reference)
            verify.assert_not_called()
            file.write_bytes(b"original")
            with mock.patch.object(VERIFY, "run") as run:
                VERIFY.verify_updates(self.root, "0.8.0", reference)
            self.assertIn("--verify-update-permissions", run.call_args.args)
        file.unlink()
        file.symlink_to("Info.plist")
        with self.assertRaises(ValueError):
            VERIFY.verify_updates(self.root, "0.8.0", reference)

    def test_wrong_version_or_multiple_full_packages_are_rejected(self):
        package = self.package({})
        with self.assertRaises(ValueError):
            VERIFY.verify_updates(self.root, "0.8.1", self.bundle)
        package.with_name("pngr-0.8.1-osx-universal-full.nupkg").write_bytes(package.read_bytes())
        with self.assertRaises(ValueError):
            VERIFY.verify_updates(self.root, "0.8.0", self.bundle)


if __name__ == "__main__":
    unittest.main()
