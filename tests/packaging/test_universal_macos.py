"""Universal 2 packaging checks, including architecture-dependent .NET payloads."""
import importlib.util
import json
import os
import pathlib
import plistlib
import struct
import tempfile
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/verify-macos-universal.py"
SPEC = importlib.util.spec_from_file_location("universal_verifier", SCRIPT)
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


def thin(cpu):
    return b"\xcf\xfa\xed\xfe" + struct.pack("<I", cpu) + bytes(24)


def fat():
    return b"\xca\xfe\xba\xbe" + struct.pack(">I", 2) + b"".join(struct.pack(">IIIII", cpu, 0, 0, 0, 0) for cpu in (VERIFIER.ARM64, VERIFIER.X64))


class UniversalPackageTests(unittest.TestCase):
    def bundle(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        bundle = pathlib.Path(temporary.name) / "pngr.app"
        contents = bundle / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        (contents / "Resources").mkdir()
        (contents / "MacOS/pngr").write_bytes(fat())
        (contents / "Resources/pngr.icns").write_bytes(b"icon")
        (contents / "Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "dev.tracescope.desktop", "CFBundleExecutable": "pngr", "CFBundleShortVersionString": "0.7.1", "pngrReleaseVersion": "0.7.1"}))
        for rid, cpu in (("osx-arm64", VERIFIER.ARM64), ("osx-x64", VERIFIER.X64)):
            payload = contents / "Resources" / "runtimes" / rid
            payload.mkdir(parents=True)
            for name in ("pngr", "libhostfxr.dylib", "libcoreclr.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libe_sqlite3.dylib", "tracescope-tcp-hop", "libTraceScopeNotifications.dylib"):
                (payload / name).write_bytes(thin(cpu))
            (payload / "pngr.dll").write_bytes(b"MZ managed image")
            (payload / "pngr.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"includedFrameworks": []}}))
            (payload / "pngr.deps.json").write_text(json.dumps({"runtimeTarget": {"name": "net10/" + rid}, "libraries": {"pngr/0.7.1": {}}}))
        return bundle

    def test_native_and_managed_images_are_distinguished(self):
        self.assertEqual(VERIFIER.architectures(thin(VERIFIER.ARM64)), {VERIFIER.ARM64})
        self.assertEqual(VERIFIER.architectures(fat()), {VERIFIER.ARM64, VERIFIER.X64})
        self.assertEqual(VERIFIER.architectures(b"MZ managed image"), set())

    def test_both_native_runtime_payloads_pass(self):
        VERIFIER.verify(self.bundle(), "0.7.1", False)

    def test_arm_only_launcher_is_rejected(self):
        bundle = self.bundle()
        (bundle / "Contents/MacOS/pngr").write_bytes(thin(VERIFIER.ARM64))
        with self.assertRaisesRegex(AssertionError, "Universal 2"):
            VERIFIER.verify(bundle, "0.7.1", False)

    def test_wrong_arch_dependency_is_rejected(self):
        bundle = self.bundle()
        (bundle / "Contents/Resources/runtimes/osx-x64/libSkiaSharp.dylib").write_bytes(thin(VERIFIER.ARM64))
        with self.assertRaisesRegex(AssertionError, "native osx-x64"):
            VERIFIER.verify(bundle, "0.7.1", False)

    def test_legacy_identifier_and_version_are_enforced(self):
        with self.assertRaises(AssertionError):
            VERIFIER.verify(self.bundle(), "0.7.2", False)

    def test_072_requires_pngr_display_name_with_legacy_identity(self):
        bundle = self.bundle()
        path = bundle / "Contents/Info.plist"
        metadata = plistlib.loads(path.read_bytes())
        metadata["CFBundleShortVersionString"] = "0.7.2"
        metadata["pngrReleaseVersion"] = "0.7.2"
        path.write_bytes(plistlib.dumps(metadata))
        with self.assertRaisesRegex(AssertionError, "display name must be Pngr"):
            VERIFIER.verify(bundle, "0.7.2", False)

    def test_history_is_never_packaged(self):
        bundle = self.bundle()
        (bundle / "Contents/Resources/tracescope.db").write_bytes(b"user data")
        with self.assertRaisesRegex(AssertionError, "User/development"):
            VERIFIER.verify(bundle, "0.7.1", False)

    def test_malformed_fat_header_is_rejected(self):
        with self.assertRaises(ValueError):
            VERIFIER.architectures(b"\xca\xfe\xba\xbe" + struct.pack(">I", 100))

    def test_update_capability_requires_universal_helper_and_matching_feed(self):
        bundle = self.bundle()
        (bundle / "Contents/MacOS/UpdateMac").write_bytes(fat())
        (bundle / "Contents/Resources/sq.version").write_text("<version>0.7.1</version><channel>osx-universal</channel>")
        VERIFIER.verify(bundle, "0.7.1", False, True)
        (bundle / "Contents/MacOS/UpdateMac").write_bytes(thin(VERIFIER.ARM64))
        with self.assertRaisesRegex(AssertionError, "Updater must be Universal 2"):
            VERIFIER.verify(bundle, "0.7.1", False, True)

    @unittest.skipUnless(os.name == "posix", "Unix execution bits are not represented by Windows chmod")
    def test_update_permission_validation_checks_a_copy_and_preserves_original(self):
        bundle = self.bundle()
        assembly = bundle / "Contents/Resources/runtimes/osx-arm64/pngr.dll"
        assembly.chmod(0o644)

        def check_signature(arguments, check):
            self.assertTrue(check)
            self.assertEqual(arguments[:4], ["codesign", "--verify", "--deep", "--strict"])
            copied_assembly = pathlib.Path(arguments[-1]) / "Contents/Resources/runtimes/osx-arm64/pngr.dll"
            self.assertEqual(copied_assembly.stat().st_mode & 0o777, 0o755)
            self.assertNotEqual(pathlib.Path(arguments[-1]), bundle)

        with mock.patch.object(VERIFIER.subprocess, "run", side_effect=check_signature):
            VERIFIER.verify_update_permissions(bundle)
        self.assertEqual(assembly.stat().st_mode & 0o777, 0o644)

    def test_managed_assemblies_are_rejected_in_macos_code_directory(self):
        bundle = self.bundle()
        (bundle / "Contents/MacOS/System.Xml.Linq.dll").write_bytes(b"MZ managed image")
        with self.assertRaisesRegex(AssertionError, "Managed assemblies must remain Resources"):
            VERIFIER.verify(bundle, "0.7.1", False)
