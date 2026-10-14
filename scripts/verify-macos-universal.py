#!/usr/bin/env python3
"""Verify native architecture slices and self-contained dependencies of a Universal 2 bundle."""
import argparse
import json
import pathlib
import plistlib
import shutil
import struct
import subprocess
import tempfile

ARM64 = 0x0100000C
X64 = 0x01000007


def architectures(data):
    """Return Mach-O CPU types; managed PE DLLs correctly return an empty set."""
    if len(data) < 8:
        return set()
    magic = data[:4]
    if magic in (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe"):
        return {struct.unpack_from("<I", data, 4)[0]}
    if magic in (b"\xfe\xed\xfa\xcf", b"\xfe\xed\xfa\xce"):
        return {struct.unpack_from(">I", data, 4)[0]}
    if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        count = struct.unpack_from(">I", data, 4)[0]
        stride = 32 if magic[-1] == 0xBF else 20
        if count > 32 or len(data) < 8 + count * stride:
            raise ValueError("Invalid universal Mach-O header")
        return {struct.unpack_from(">I", data, 8 + i * stride)[0] for i in range(count)}
    return set()


def verify(bundle, version, check_dependencies=True, require_updater=False):
    contents = bundle / "Contents"
    plist = plistlib.loads((contents / "Info.plist").read_bytes())
    assert plist["CFBundleIdentifier"] == "dev.tracescope.desktop"
    assert plist["CFBundleExecutable"] == "pngr"
    assert plist["CFBundleShortVersionString"] == version.split("-")[0]
    assert plist["pngrReleaseVersion"] == version
    if tuple(map(int, version.split("-")[0].split("."))) >= (0, 7, 2):
        assert plist.get("CFBundleName") == "Pngr" and plist.get("CFBundleDisplayName") == "Pngr", "Installer display name must be Pngr"
        assert plist.get("LSHasLocalizedDisplayName") is True, "Finder localized display name required"
        assert 'CFBundleDisplayName = "Pngr";' in (contents / "Resources/en.lproj/InfoPlist.strings").read_text()
    assert architectures((contents / "MacOS/pngr").read_bytes()) == {ARM64, X64}, "Launcher must be Universal 2"
    assert not any((contents / "MacOS").rglob("*.dll")), "Managed assemblies must remain Resources, not MacOS code, to survive updater permission changes"
    assert (contents / "Resources/pngr.icns").is_file()
    if require_updater:
        assert architectures((contents / "MacOS/UpdateMac").read_bytes()) == {ARM64, X64}, "Updater must be Universal 2"
        metadata = (contents / "Resources/sq.version").read_text()
        assert f"<version>{version}</version>" in metadata
        assert "osx-universal" in metadata
    for rid, cpu in (("osx-arm64", ARM64), ("osx-x64", X64)):
        payload = contents / "Resources" / "runtimes" / rid
        config = json.loads((payload / "pngr.runtimeconfig.json").read_text())
        assert "includedFrameworks" in config["runtimeOptions"], "Runtime must be self-contained"
        deps = json.loads((payload / "pngr.deps.json").read_text())
        assert deps["runtimeTarget"]["name"].endswith("/" + rid)
        assert "pngr/" + version in deps["libraries"]
        for required in ("pngr", "pngr.dll", "libhostfxr.dylib", "libcoreclr.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libe_sqlite3.dylib", "tracescope-tcp-hop", "libTraceScopeNotifications.dylib"):
            assert (payload / required).is_file(), f"Missing {rid}: {required}"
        for file in payload.rglob("*"):
            if not file.is_file():
                continue
            with file.open("rb") as stream:
                slices = architectures(stream.read(4096))
            if not slices:
                continue
            assert cpu in slices, f"{file.name} lacks native {rid} slice"
            if check_dependencies:
                output = subprocess.check_output(["otool", "-arch", "arm64" if cpu == ARM64 else "x86_64", "-L", str(file)], text=True)
                # LC_ID_DYLIB is the library's own install-name, not a dependency to load.
                identity = subprocess.check_output(["otool", "-D", str(file)], text=True).splitlines()
                own_ids = {line.strip() for line in identity if line and not line.endswith(":")}
                for line in output.splitlines():
                    if not line.startswith("\t"):
                        continue
                    dependency = line.strip().split(" (", 1)[0]
                    if dependency in own_ids:
                        continue
                    if dependency.startswith("/"):
                        assert dependency.startswith(("/usr/lib/", "/System/Library/")), f"Non-system absolute dependency: {dependency}"
                    elif dependency.startswith("@loader_path/"):
                        assert (file.parent / dependency.removeprefix("@loader_path/")).is_file(), f"Missing loader-relative dependency: {dependency}"
                    elif dependency.startswith("@rpath/"):
                        # The self-contained payload uses local runtime libraries; nothing
                        # should require Homebrew or a separately installed .NET runtime.
                        assert any(payload.rglob(pathlib.PurePosixPath(dependency).name)), f"Missing rpath dependency: {dependency}"
    for file in bundle.rglob("*"):
        assert file.suffix.lower() not in (".db", ".sqlite", ".sqlite3", ".pdb", ".cs", ".csproj", ".trx"), f"User/development file: {file}"
        assert file.name not in ("workspace.json", "settings.json", "profiles.json", ".env")
    print(f"Verified Universal 2 pngr {version}: native ARM64/x64 launcher and both self-contained payloads")


def verify_update_permissions(bundle):
    """Velopack's macOS extractor sets all files 755; signatures must survive that."""
    with tempfile.TemporaryDirectory(prefix="pngr-update-permissions-") as directory:
        copy = pathlib.Path(directory) / "pngr.app"
        shutil.copytree(bundle, copy, symlinks=True)
        for file in copy.rglob("*"):
            if file.is_file() and not file.is_symlink():
                file.chmod(0o755)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(copy)], check=True)
    print("Verified bundle signatures after updater permission normalization")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=pathlib.Path)
    parser.add_argument("version")
    parser.add_argument("--require-updater", action="store_true")
    parser.add_argument("--verify-update-permissions", action="store_true")
    args = parser.parse_args()
    verify(args.bundle, args.version.removeprefix("v"), require_updater=args.require_updater)
    if args.verify_update_permissions:
        verify_update_permissions(args.bundle)
