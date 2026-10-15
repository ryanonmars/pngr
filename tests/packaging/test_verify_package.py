"""Deterministic release-archive validation, without application binaries."""
import contextlib
import importlib.util
import io
import json
import pathlib
import plistlib
import tempfile
import unittest
import zipfile
from notice_fixtures import materials

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "verify-package.py"
SPEC = importlib.util.spec_from_file_location("verify_package", SCRIPT)
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


class ReleaseArchiveTests(unittest.TestCase):
    def create_archive(self, runtime="win-x64", version="0.7.0", extra=None, framework=True):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = pathlib.Path(temporary.name) / "release.zip"
        mac = runtime.startswith("osx-")
        prefix = "pngr.app/Contents/MacOS/" if mac else ""
        notices, inventory, libraries = materials(runtime)
        files = {
            prefix + ("pngr" if mac else "pngr.exe"): b"synthetic executable",
            prefix + "pngr.dll": b"synthetic assembly",
            prefix + "pngr.runtimeconfig.json": json.dumps({"runtimeOptions": {"includedFrameworks": []} if framework else {}}),
            prefix + "pngr.deps.json": json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/" + runtime}, "libraries": dict(libraries, **{"pngr/" + version: {"type": "project"}})}),
        }
        if mac:
            files.update({
                "pngr.app/Contents/Info.plist": plistlib.dumps({"CFBundleExecutable": "pngr", "CFBundleIdentifier": "dev.tracescope.desktop", "CFBundleShortVersionString": version.split("-")[0], "pngrReleaseVersion": version}),
                "pngr.app/Contents/Resources/pngr.icns": b"synthetic icon",
                prefix + "tracescope-tcp-hop": b"synthetic helper",
                prefix + "libTraceScopeNotifications.dylib": b"synthetic bridge",
                "__MACOSX/pngr.app/._Contents": b"resource metadata",
            })
        material_prefix = "pngr.app/Contents/Resources/" if mac else ""
        files[material_prefix + "THIRD-PARTY-NOTICES.txt"] = notices
        files[material_prefix + "third-party-inventory.json"] = inventory
        files.update(extra or {})
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in files.items():
                archive.writestr(name, data)
        return path

    def verify(self, path, runtime="win-x64", version="0.7.0"):
        with contextlib.redirect_stdout(io.StringIO()):
            VERIFIER.verify(path, runtime, version)

    def test_all_release_targets_accept_self_contained_archives(self):
        for runtime in ("win-x64", "osx-arm64", "osx-x64"):
            with self.subTest(runtime=runtime):
                self.verify(self.create_archive(runtime), runtime)

    def test_environment_variables_runtime_library_is_not_an_env_file(self):
        self.verify(self.create_archive(extra={"Microsoft.Extensions.Configuration.EnvironmentVariables.dll": b"runtime library"}))

    def test_databases_and_journals_are_rejected(self):
        for name in ("history.db", "history.sqlite3", "history.db-wal", "history.sqlite-shm"):
            with self.subTest(name=name), self.assertRaises(AssertionError):
                self.verify(self.create_archive(extra={name: b"private data"}))

    def test_user_config_and_development_files_are_rejected(self):
        for name in ("workspace.json", "profiles.json", ".env", "pngr.pdb", "data/sample.txt", ".git/config", "src/Program.cs"):
            with self.subTest(name=name), self.assertRaises(AssertionError):
                self.verify(self.create_archive(extra={name: b"excluded"}))

    def test_wrong_version_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "version"):
            self.verify(self.create_archive(), version="0.8.0")

    def test_wrong_runtime_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "runtime"):
            self.verify(self.create_archive("osx-arm64"), "osx-x64")

    def test_framework_dependent_archive_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "self-contained"):
            self.verify(self.create_archive(framework=False))

    def test_missing_executable_is_rejected(self):
        path = self.create_archive()
        with zipfile.ZipFile(path) as archive:
            files = {name: archive.read(name) for name in archive.namelist() if name != "pngr.exe"}
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in files.items():
                archive.writestr(name, data)
        with self.assertRaisesRegex(AssertionError, "Missing pngr.exe"):
            self.verify(path)


if __name__ == "__main__":
    unittest.main()
