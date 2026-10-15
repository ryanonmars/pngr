"""Universal 2 packaging checks, including architecture-dependent .NET payloads."""
import importlib.util
import json
import os
import pathlib
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
from notice_fixtures import materials

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
        (contents / "Resources/THIRD-PARTY-NOTICES.txt").write_bytes((SCRIPT.parents[1] / "compliance/THIRD-PARTY-NOTICES.txt").read_bytes())
        for rid, cpu in (("osx-arm64", VERIFIER.ARM64), ("osx-x64", VERIFIER.X64)):
            payload = contents / "Resources" / "runtimes" / rid
            payload.mkdir(parents=True)
            for name in ("pngr", "libhostfxr.dylib", "libcoreclr.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libe_sqlite3.dylib", "tracescope-tcp-hop", "libTraceScopeNotifications.dylib"):
                (payload / name).write_bytes(thin(cpu))
            notices, inventory, libraries = materials(rid)
            (payload / "THIRD-PARTY-NOTICES.txt").write_bytes(notices)
            (payload / "third-party-inventory.json").write_bytes(inventory)
            (payload / "pngr.dll").write_bytes(b"MZ managed image")
            (payload / "pngr.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"includedFrameworks": []}}))
            (payload / "pngr.deps.json").write_text(json.dumps({"runtimeTarget": {"name": "net10/" + rid}, "libraries": dict(libraries, **{"pngr/0.7.1": {"type": "project"}})}))
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
        plist = bundle / "Contents/Info.plist"
        metadata = plistlib.loads(plist.read_bytes())
        metadata["pngrReleaseVersion"] = "0.7.5"
        plist.write_bytes(plistlib.dumps(metadata))
        assembly = bundle / "Contents/Resources/runtimes/osx-arm64/pngr.dll"
        assembly.chmod(0o644)

        def check_signature(arguments, check):
            self.assertTrue(check)
            checked = pathlib.Path(arguments[-1])
            if checked.name == "pngr.app":
                self.assertEqual(arguments[:4], ["codesign", "--verify", "--deep", "--strict"])
                copied_bundle = checked
            else:
                self.assertEqual(arguments[:3], ["codesign", "--verify", "--strict"])
                self.assertEqual(checked.name, "pngr")
                copied_bundle = checked.parents[4]
            copied_assembly = copied_bundle / "Contents/Resources/runtimes/osx-arm64/pngr.dll"
            self.assertEqual(copied_assembly.stat().st_mode & 0o777, 0o755)
            self.assertNotEqual(copied_bundle, bundle)

        with mock.patch.object(VERIFIER.subprocess, "run", side_effect=check_signature) as run:
            VERIFIER.verify_update_permissions(bundle)
            self.assertEqual(run.call_count, 3)
        self.assertEqual(assembly.stat().st_mode & 0o777, 0o644)

    def test_managed_assemblies_are_rejected_in_macos_code_directory(self):
        bundle = self.bundle()
        (bundle / "Contents/MacOS/System.Xml.Linq.dll").write_bytes(b"MZ managed image")
        with self.assertRaisesRegex(AssertionError, "Managed assemblies must remain Resources"):
            VERIFIER.verify(bundle, "0.7.1", False)

    def test_repaired_releases_verify_each_apphost_independently(self):
        bundle = self.bundle()
        with mock.patch.object(VERIFIER.subprocess, "run") as run:
            VERIFIER.verify_runtime_signatures(bundle, "0.7.4")
            run.assert_not_called()
            VERIFIER.verify_runtime_signatures(bundle, "0.7.5")
            self.assertEqual(run.call_count, 2)
            for call, rid in zip(run.call_args_list, ("osx-arm64", "osx-x64")):
                self.assertEqual(call.args[0], ["codesign", "--verify", "--strict", str(bundle / "Contents/Resources/runtimes" / rid / "pngr")])
                self.assertEqual(call.kwargs, {"check": True})

    @unittest.skipUnless(sys.platform == "darwin" and shutil.which("clang"), "Requires macOS signing and compiler")
    def test_relocated_bundle_executable_requires_standalone_resigning(self):
        # Exercise real Security.framework signatures rather than mocked commands.
        with tempfile.TemporaryDirectory(prefix="pngr-apphost-signature-") as directory:
            root = pathlib.Path(directory)
            original = root / "original/pngr.app"
            (original / "Contents/MacOS").mkdir(parents=True)
            metadata = {"CFBundleIdentifier": "dev.tracescope.desktop", "CFBundleExecutable": "pngr", "CFBundleVersion": "0.7.4"}
            (original / "Contents/Info.plist").write_bytes(plistlib.dumps(metadata))
            source = root / "main.c"
            source.write_text("int main(void) { return 0; }\n")
            host = original / "Contents/MacOS/pngr"
            subprocess.run(["clang", str(source), "-o", str(host)], check=True, capture_output=True)
            subprocess.run(["codesign", "--force", "--sign", "-", str(original)], check=True, capture_output=True)
            subprocess.run(["codesign", "--verify", "--strict", str(original)], check=True, capture_output=True)
            moved = root / "relocated/pngr.app/Contents/Resources/runtimes/osx-arm64/pngr"
            moved.parent.mkdir(parents=True)
            shutil.copy2(host, moved)
            stale = subprocess.run(["codesign", "--verify", "--strict", str(moved)], capture_output=True, text=True)
            self.assertNotEqual(stale.returncode, 0)
            self.assertIn("invalid Info.plist", stale.stderr)
            subprocess.run(["codesign", "--force", "--sign", "-", "--identifier", metadata["CFBundleIdentifier"], str(moved)], check=True, capture_output=True)
            subprocess.run(["codesign", "--verify", "--strict", str(moved)], check=True, capture_output=True)
            # The updater's permission normalization must preserve the repair.
            moved.chmod(0o755)
            subprocess.run(["codesign", "--verify", "--strict", str(moved)], check=True, capture_output=True)
