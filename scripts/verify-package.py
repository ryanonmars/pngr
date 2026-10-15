#!/usr/bin/env python3
"""Check release ZIP contents without launching a cross-platform executable."""
import argparse
import importlib.util
import json
import pathlib
import plistlib
import zipfile

# Reuse the same content/hash checks for staging, portable archives and updates.
_NOTICE_SPEC = importlib.util.spec_from_file_location(
    'pngr_distribution_notices', pathlib.Path(__file__).resolve().parent / 'verify-distribution-notices.py')
NOTICE_CHECKS = importlib.util.module_from_spec(_NOTICE_SPEC)
_NOTICE_SPEC.loader.exec_module(NOTICE_CHECKS)


def verify(path, runtime, version):
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None, "Archive integrity check failed"
        names = {item.filename for item in archive.infolist() if not item.is_dir()}
        prefix = "pngr.app/Contents/MacOS/" if runtime.startswith("osx-") else ""
        executable = "pngr" if runtime.startswith("osx-") else "pngr.exe"
        for required in (executable, "pngr.dll", "pngr.runtimeconfig.json", "pngr.deps.json"):
            assert prefix + required in names, f"Missing {required}"
        config = json.loads(archive.read(prefix + "pngr.runtimeconfig.json"))
        assert "includedFrameworks" in config["runtimeOptions"], "Release must be self-contained"
        dependencies = json.loads(archive.read(prefix + "pngr.deps.json"))
        assert f"pngr/{version}" in dependencies["libraries"], "Application version does not match release"
        assert dependencies["runtimeTarget"]["name"].endswith("/" + runtime), "Archive runtime does not match release target"
        material_prefix = "pngr.app/Contents/Resources/" if runtime.startswith("osx-") else ""
        NOTICE_CHECKS.verify_archive_runtime(archive, material_prefix, prefix, runtime)
        for name in names:
            NOTICE_CHECKS.reject_private_material(name)
            # ditto includes harmless AppleDouble resource metadata in macOS ZIPs.
            parts = pathlib.PurePosixPath(name).parts
            if parts and parts[0] == "__MACOSX":
                continue
            lower = name.lower()
            assert not any(part.lower() in {"bin", "obj", ".git", "testresults", "data", "test-data"} for part in parts), f"Development/user directory: {name}"
            assert not lower.endswith((".pdb", ".db", ".sqlite", ".sqlite3", ".cs", ".csproj", ".sln", ".slnx", ".trx", ".log")), f"Development/user file: {name}"
            filename = pathlib.PurePosixPath(lower).name
            assert filename != ".env" and not filename.startswith(".env."), f"Environment configuration: {name}"
            assert not any(token in filename for token in ("workspace.json", "settings.json", "profiles.json", ".db-wal", ".db-shm", ".sqlite-wal", ".sqlite-shm", ".sqlite3-wal", ".sqlite3-shm")), f"User configuration: {name}"
        if runtime.startswith("osx-"):
            plist = plistlib.loads(archive.read("pngr.app/Contents/Info.plist"))
            assert plist["CFBundleExecutable"] == "pngr"
            assert plist["CFBundleIdentifier"] == "dev.tracescope.desktop", "Legacy identifier must be preserved"
            assert plist["CFBundleShortVersionString"] == version.split("-")[0]
            assert plist["pngrReleaseVersion"] == version
            assert "pngr.app/Contents/Resources/pngr.icns" in names
            for native_component in ("tracescope-tcp-hop", "libTraceScopeNotifications.dylib"):
                assert prefix + native_component in names, f"Missing native macOS component: {native_component}"
        print(f"Verified {path}: {runtime}, version {version}, self-contained and no user data")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=pathlib.Path)
    parser.add_argument("runtime", choices=("osx-arm64", "osx-x64", "win-x64"))
    parser.add_argument("version")
    args = parser.parse_args()
    verify(args.archive, args.runtime, args.version.removeprefix("v"))
