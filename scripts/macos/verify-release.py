#!/usr/bin/env python3
"""Fail-closed verification of Developer ID macOS release and update artifacts."""
import argparse
import hashlib
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import stat
import subprocess
import tempfile
import time
import sys
import zipfile

TEAM = "VRNY8KM56D"
IDENTITY = "Developer ID Application: Ryan Poelker (VRNY8KM56D)"
BUNDLE_ID = "dev.tracescope.desktop"
JIT = {"com.apple.security.cs.allow-jit": True}
UPDATER = {**JIT, **{key: True for key in (
    "com.apple.security.automation.apple-events",
    "com.apple.security.cs.allow-unsigned-executable-memory",
    "com.apple.security.cs.allow-dyld-environment-variables",
    "com.apple.security.cs.disable-library-validation")}}
MACHO = {bytes.fromhex(value) for value in (
    "feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}


def diagnostic(error):
    # Verification commands receive no credentials. Still emit only known error
    # categories/codes, never raw tool output, URLs, tickets or host paths.
    text = b"\n".join(value if isinstance(value, bytes) else str(value).encode()
                       for value in (error.stdout, error.stderr) if value).decode("utf-8", errors="replace")
    lowered = text.lower()
    categories = [label for needle, label in (
        ("timed out", "network timeout"), ("could not resolve", "DNS failure"),
        ("could not establish secure connection", "TLS connection failure"),
        ("certificate authority pinning mismatch", "Apple certificate pinning mismatch"),
        ("record not found", "ticket record not found"),
        ("could not validate ticket", "ticket validation failed")) if needle in lowered]
    codes = re.findall(r"(?:NSURLErrorDomain|NSOSStatusErrorDomain)\s+Code=(-?\d+)", text)
    return "; ".join([*categories, *("native error " + code for code in codes)]) or "no classified native diagnostic"


def run(*args):
    # EX_NOHOST (68) from stapler is a ticket-service/network failure. Retry only
    # that code, with a hard bound; invalid signatures/tickets still fail closed.
    attempts = 3 if args[:3] == ("xcrun", "stapler", "validate") else 1
    for attempt in range(attempts):
        try:
            return subprocess.run(list(map(str, args)), check=True, capture_output=True).stdout
        except subprocess.CalledProcessError as error:
            print(f"Verification {Path(args[0]).name} failed (exit {error.returncode}; {diagnostic(error)}).", file=sys.stderr)
            if error.returncode != 68 or attempt + 1 == attempts:
                raise
            print(f"Retrying Apple ticket validation ({attempt + 2}/{attempts}).", file=sys.stderr)
            time.sleep(10 * (attempt + 1))


def native(path):
    if path.is_symlink() or not path.is_file():
        return False
    with path.open("rb") as stream:
        return stream.read(4) in MACHO


def inspect_signature(path, arch=None, entitlements=None, identifier=None):
    selection = ["--arch", arch] if arch else []
    result = subprocess.run(["codesign", "-d", "--verbose=4", *selection, str(path)],
                            check=True, capture_output=True, text=True)
    details = result.stderr
    if (f"Authority={IDENTITY}\n" not in details or f"TeamIdentifier={TEAM}\n" not in details
            or not re.search(r"^Timestamp=.+", details, re.MULTILINE)):
        raise ValueError(f"Expected timestamped Developer ID identity: {path.name}")
    if entitlements is None:  # A disk image is signed data, not executable code.
        return
    if not re.search(r"^CodeDirectory.*flags=0x[0-9a-fA-F]+\([^)]*\bruntime\b", details, re.MULTILINE):
        raise ValueError(f"Hardened runtime missing: {path.name}")
    if identifier and f"Identifier={identifier}\n" not in details:
        raise ValueError(f"Unexpected executable identifier: {path.name}")
    raw = run("codesign", "-d", *selection, "--entitlements", ":-", path)
    actual = plistlib.loads(raw) if raw.strip() else {}
    if actual != entitlements or any(type(value) is not bool for value in actual.values()):
        raise ValueError(f"Unexpected entitlements: {path.name}")


def verify_app(bundle):
    metadata = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    if metadata.get("CFBundleIdentifier") != BUNDLE_ID or metadata.get("CFBundleExecutable") != "pngr":
        raise ValueError("Unexpected release bundle identifier or executable")
    run("codesign", "--verify", "--deep", "--strict", "--all-architectures", bundle)
    hosts = {bundle / "Contents/Resources/runtimes" / rid / "pngr" for rid in ("osx-arm64", "osx-x64")}
    updater = bundle / "Contents/MacOS/UpdateMac"
    launcher = bundle / "Contents/MacOS/pngr"
    if not all(native(path) for path in [*hosts, updater, launcher]):
        raise ValueError("Missing required native release component")
    for path in sorted(bundle.rglob("*")):
        if not native(path):
            continue
        run("codesign", "--verify", "--strict", "--all-architectures", path)
        arches = run("lipo", "-archs", path).decode().split()
        if not arches or not set(arches) <= {"arm64", "x86_64"}:
            raise ValueError(f"Unexpected native architectures: {path.name}")
        for arch in arches:
            inspect_signature(path, arch, JIT if path in hosts else UPDATER if path == updater else {},
                              BUNDLE_ID if path in hosts or path == launcher else None)
    # The bundle signature is represented by its universal launcher; inspect both slices explicitly.
    for arch in ("arm64", "x86_64"):
        inspect_signature(bundle, arch, {}, BUNDLE_ID)
    run("xcrun", "stapler", "validate", bundle)
    run("spctl", "--assess", "--type", "execute", "--verbose=4", bundle)


def verify_dmg(path):
    run("codesign", "--verify", "--strict", path)
    inspect_signature(path)
    run("xcrun", "stapler", "validate", path)
    run("spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose=4", path)


def extract_update(package, destination):
    """Mirror Velopack's lib/app payload, .__symlink decoding and 755 modes safely."""
    entries = {}
    links = {}
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise ValueError("Corrupt update ZIP")
        seen = set()
        for item in archive.infolist():
            name = item.filename
            parts = PurePosixPath(name).parts
            if ("\\" in name or name.startswith("/") or ".." in parts or name in seen
                    or not parts or ":" in parts[0] or PurePosixPath(name).as_posix() != name.rstrip("/")):
                raise ValueError("Unsafe or duplicate update entry")
            seen.add(name)
            if not name.startswith("lib/app/") or item.is_dir():
                continue
            relative = name.removeprefix("lib/app/")
            encoded_link = relative.endswith(".__symlink")
            if encoded_link:
                relative = relative.removesuffix(".__symlink")
            path = PurePosixPath(relative)
            mode = item.external_attr >> 16
            if not relative or relative.startswith("/") or ".." in path.parts or relative in entries:
                raise ValueError("Unsafe or duplicate decoded update entry")
            if stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFLNK):
                raise ValueError("Unsupported update entry type")
            entries[relative] = archive.read(item)
            if encoded_link or stat.S_ISLNK(mode):
                links[relative] = entries[relative].decode("utf-8").rstrip("/")
        if not entries or "Contents/Info.plist" not in entries:
            raise ValueError("Missing canonical lib/app update payload")
        for name in entries:
            if any(parent.as_posix() in entries for parent in PurePosixPath(name).parents if parent.as_posix() != "."):
                raise ValueError("Update file/directory collision")
        # Validate before writing any file or creating any symlink.
        root = destination.resolve()
        for name, target in links.items():
            if not target or PurePosixPath(target).is_absolute() or "\\" in target:
                raise ValueError("Unsafe update symlink")
            resolved = (root / name).parent.joinpath(target).resolve()
            if not resolved.is_relative_to(root):
                raise ValueError("Escaping update symlink")
        for name, data in entries.items():
            if name in links:
                continue
            path = destination / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            path.chmod(0o755)
        for name, target in links.items():
            path = destination / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to(target)
        for name in links:
            target = destination / name
            try:
                resolved = target.resolve(strict=True)
            except (OSError, RuntimeError) as error:
                raise ValueError("Broken or cyclic update symlink") from error
            if not resolved.is_relative_to(root):
                raise ValueError("Escaping update symlink chain")


def manifest(bundle):
    return {path.relative_to(bundle).as_posix():
            ("link", os.readlink(path)) if path.is_symlink() else ("file", hashlib.sha256(path.read_bytes()).hexdigest())
            for path in bundle.rglob("*") if path.is_symlink() or path.is_file()}


def verify_updates(directory, version, reference):
    version = version.removeprefix("v")
    packages = list(directory.glob("*osx-universal-full.nupkg"))
    if len(packages) != 1 or packages[0].name != f"pngr-{version}-osx-universal-full.nupkg":
        raise ValueError("Expected exactly one full universal update for the release")
    with tempfile.TemporaryDirectory(prefix="pngr-release-update-") as temporary:
        bundle = Path(temporary) / "pngr.app"
        extract_update(packages[0], bundle)
        if manifest(bundle) != manifest(reference):
            raise ValueError("Update payload differs from the signed portable application")
        verify_app(bundle)
        run("python3", Path(__file__).resolve().parents[1] / "verify-macos-universal.py",
            bundle, version, "--require-updater", "--verify-update-permissions")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("app", "dmg"):
        commands.add_parser(name).add_argument("path", type=Path)
    update = commands.add_parser("updates")
    update.add_argument("directory", type=Path)
    update.add_argument("version")
    update.add_argument("reference", type=Path)
    args = parser.parse_args()
    if args.command == "app":
        verify_app(args.path)
    elif args.command == "dmg":
        verify_dmg(args.path)
    else:
        verify_updates(args.directory, args.version, args.reference)
    print(f"Verified Developer ID release {args.command}")


if __name__ == "__main__":
    main()
