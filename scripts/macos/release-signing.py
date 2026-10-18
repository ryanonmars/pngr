#!/usr/bin/env python3
"""Authorized ephemeral signing credentials and universal component signing.

Preparation requires a tag push or protected manual main verification. Private
material lives outside the checkout and subprocess diagnostics are suppressed.
"""
import base64
import json
import importlib.util
import os
from pathlib import Path
import plistlib
import re
import secrets
import shlex
import shutil
import subprocess
import sys

IDENTITY = "Developer ID Application: Ryan Poelker (VRNY8KM56D)"
TEAM = "VRNY8KM56D"
FINGERPRINT = "566DA82D5A264CC736DF5E646C42E3495F924AC6"
BUNDLE_ID = "dev.tracescope.desktop"
PROFILE = "pngr-release-notary"
MACHO = {bytes.fromhex(value) for value in (
    "feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}


def run(arguments):
    # CalledProcessError includes argv (potential passwords); never propagate it.
    result = subprocess.run(arguments, capture_output=True)
    if result.returncode:
        raise RuntimeError(f"Signing operation failed: {Path(arguments[0]).name} (exit {result.returncode}).")
    return result.stdout.decode("utf-8", errors="strict")


def state_directory():
    return Path(os.environ["RUNNER_TEMP"]) / "pngr-release-signing"


def release_event():
    ref = os.environ.get("GITHUB_REF", "")
    version = os.environ.get("PNGR_VERSION", "")
    return (os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("GITHUB_EVENT_NAME") == "push"
            and ref == f"refs/tags/{version}"
            and re.fullmatch(r"v\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", version))


def prepare(verification=False):
    if verification:
        spec = importlib.util.spec_from_file_location("pngr_verification_policy", Path(__file__).with_name("verification-policy.py"))
        policy = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(policy)
        policy.verify()
    elif not release_event():
        raise RuntimeError("Signing credentials require a matching version-tag push.")
    values = {name: os.environ.get(name, "") for name in (
        "PNGR_MACOS_CERTIFICATE_P12_BASE64", "PNGR_MACOS_CERTIFICATE_PASSWORD",
        "PNGR_APPLE_NOTARY_KEY_P8_BASE64", "PNGR_APPLE_NOTARY_KEY_ID", "PNGR_APPLE_NOTARY_ISSUER_ID")}
    if not all(values.values()):
        raise RuntimeError("A required release signing or notarization secret is missing.")
    os.umask(0o077)
    directory = state_directory()
    directory.mkdir(mode=0o700)  # Refuse stale state; do not reuse credentials.
    keychain = directory / "signing.keychain-db"
    previous = shlex.split(run(["/usr/bin/security", "list-keychains", "-d", "user"]))
    (directory / "search-list.json").write_text(json.dumps(previous))
    password = secrets.token_urlsafe(48)
    print(f"::add-mask::{password}", flush=True)
    certificate = directory / "identity.p12"
    key = directory / "notary.p8"
    try:
        try:
            certificate.write_bytes(base64.b64decode("".join(values["PNGR_MACOS_CERTIFICATE_P12_BASE64"].split()), validate=True))
            key.write_bytes(base64.b64decode("".join(values["PNGR_APPLE_NOTARY_KEY_P8_BASE64"].split()), validate=True))
        except ValueError:
            raise RuntimeError("Release credential encoding is invalid.") from None
        run(["/usr/bin/security", "create-keychain", "-p", password, str(keychain)])
        run(["/usr/bin/security", "set-keychain-settings", "-lut", "21600", str(keychain)])
        run(["/usr/bin/security", "unlock-keychain", "-p", password, str(keychain)])
        run(["/usr/bin/security", "import", str(certificate), "-k", str(keychain),
             "-P", values["PNGR_MACOS_CERTIFICATE_PASSWORD"], "-T", "/usr/bin/codesign"])
        # This new isolated keychain contains only the imported release identity.
        # No login-keychain item or local access-control rule is touched.
        run(["/usr/bin/security", "set-key-partition-list", "-S", "apple-tool:,apple:,codesign:",
             "-s", "-k", password, str(keychain)])
        identities = run(["/usr/bin/security", "find-identity", "-v", "-p", "codesigning", str(keychain)])
        if f'{FINGERPRINT} "{IDENTITY}"' not in identities:
            raise RuntimeError("The temporary Keychain does not contain the expected valid Developer ID identity.")
        run(["/usr/bin/security", "list-keychains", "-d", "user", "-s", str(keychain), *previous])
        run(["/usr/bin/xcrun", "notarytool", "store-credentials", PROFILE, "--key", str(key),
             "--key-id", values["PNGR_APPLE_NOTARY_KEY_ID"], "--issuer", values["PNGR_APPLE_NOTARY_ISSUER_ID"],
             "--keychain", str(keychain)])
        with open(os.environ["GITHUB_ENV"], "a") as stream:
            stream.write(f"PNGR_MACOS_RELEASE_SIGNING=1\nPNGR_MACOS_SIGN_KEYCHAIN={keychain}\nPNGR_MACOS_NOTARY_PROFILE={PROFILE}\n")
    finally:
        certificate.unlink(missing_ok=True)
        key.unlink(missing_ok=True)
    print(f"Temporary signing identity verified: Team {TEAM}.")


def cleanup():
    directory = state_directory()
    if not directory.exists():
        return
    failed = False
    previous = directory / "search-list.json"
    if previous.exists():
        try:
            run(["/usr/bin/security", "list-keychains", "-d", "user", "-s", *json.loads(previous.read_text())])
        except RuntimeError:
            failed = True
    keychain = directory / "signing.keychain-db"
    if keychain.exists():
        try:
            run(["/usr/bin/security", "delete-keychain", str(keychain)])
        except RuntimeError:
            failed = True
    shutil.rmtree(directory)
    if failed:
        raise RuntimeError("Temporary signing Keychain cleanup failed; runner teardown is required.")


def configuration():
    keychain = os.environ.get("PNGR_MACOS_SIGN_KEYCHAIN", "")
    profile = os.environ.get("PNGR_MACOS_NOTARY_PROFILE", "")
    if os.environ.get("PNGR_MACOS_RELEASE_SIGNING") != "1" or not keychain or not profile:
        raise RuntimeError("Release signing requires a Keychain and notarization profile.")
    if not Path(keychain).is_file():
        raise RuntimeError("Release signing Keychain is missing.")
    return keychain, profile


def native(path):
    if path.is_symlink() or not path.is_file():
        return False
    with path.open("rb") as stream:
        return stream.read(4) in MACHO


def sign(bundle):
    keychain, _ = configuration()
    metadata = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    if metadata.get("CFBundleIdentifier") != BUNDLE_ID:
        raise RuntimeError("Unexpected application bundle identifier.")
    hosts = {bundle / "Contents/Resources/runtimes" / rid / "pngr" for rid in ("osx-arm64", "osx-x64")}
    if not all(native(host) for host in hosts):
        raise RuntimeError("Both universal runtime hosts are required.")
    main = bundle / "Contents/MacOS/pngr"
    common = ["/usr/bin/codesign", "--force", "--sign", IDENTITY, "--keychain", keychain,
              "--timestamp", "--options", "runtime"]
    entitlements = Path(__file__).with_name("dotnet.entitlements.plist")
    for path in sorted((p for p in bundle.rglob("*") if p != main and native(p)),
                       key=lambda p: (-len(p.parts), str(p))):
        options = ["--identifier", BUNDLE_ID, "--entitlements", str(entitlements)] if path in hosts else []
        run([*common, *options, str(path)])
    # No JIT entitlement is needed by the native universal launcher.
    run([*common, str(bundle)])
    run(["/usr/bin/codesign", "--verify", "--deep", "--strict", "--all-architectures", str(bundle)])


def notarize_dmg(path):
    keychain, profile = configuration()
    run(["/usr/bin/codesign", "--force", "--sign", IDENTITY, "--keychain", keychain, "--timestamp", str(path)])
    result = json.loads(run(["/usr/bin/xcrun", "notarytool", "submit", str(path), "--wait", "--timeout", "45m",
                            "--output-format", "json", "--keychain-profile", profile, "--keychain", keychain]))
    if result.get("status") != "Accepted":
        raise RuntimeError("Apple did not accept the DMG notarization submission.")
    run(["/usr/bin/xcrun", "stapler", "staple", str(path)])


if __name__ == "__main__":
    try:
        command = sys.argv[1]
        if command == "prepare":
            prepare()
        elif command == "prepare-verification":
            prepare(verification=True)
        elif command == "cleanup":
            cleanup()
        elif command == "sign":
            sign(Path(sys.argv[2]).resolve())
        elif command == "dmg":
            notarize_dmg(Path(sys.argv[2]).resolve())
        else:
            raise RuntimeError("Unknown release signing operation.")
    except RuntimeError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        # No traceback/argv/native output: these can include credentials.
        print("Release signing failed during " + (sys.argv[1] if len(sys.argv) > 1 else "initialization") + ". Check credential validity and Apple service availability.", file=sys.stderr)
        sys.exit(1)
