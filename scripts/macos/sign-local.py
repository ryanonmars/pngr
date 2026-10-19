"""Sign a local architecture bundle inside out with an explicit Developer ID.

No Keychain settings, credential ACLs, or release configuration are changed.
Private-key authorization is left to macOS; never supply a password here.
"""
import os
from pathlib import Path
import plistlib
import re
import subprocess
import sys

MACHO = {bytes.fromhex(value) for value in (
    "feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}


def native(path):
    if path.is_symlink() or not path.is_file():
        return False
    with path.open("rb") as stream:
        return stream.read(4) in MACHO


def sign(bundle, identity, team):
    if not identity.startswith("Developer ID Application: ") or not team or not identity.endswith(f"({team})"):
        raise ValueError("An explicit Developer ID Application identity and matching Team ID are required.")
    metadata = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    if metadata["CFBundleIdentifier"] != "dev.tracescope.desktop":
        raise ValueError("Unexpected bundle identifier.")
    main = bundle / "Contents/MacOS" / metadata["CFBundleExecutable"]
    entitlements = Path(__file__).with_name("dotnet.entitlements.plist")
    common = ["codesign", "--force", "--sign", identity, "--timestamp", "--options", "runtime"]
    nested = sorted((path for path in bundle.rglob("*") if path != main and native(path)),
                    key=lambda path: (-len(path.parts), str(path)))
    if nested:
        # Batching does not combine Keychain authorization: macOS may request
        # permission for each private-key use, including each architecture.
        # Libraries inherit host entitlements; native helpers need no JIT.
        subprocess.run([*common, *(str(path) for path in nested)], check=True)
    # The architecture bundle keeps .NET assemblies and runtime JSON in MacOS.
    # Apple treats every ordinary file there as nested code, including JSON.
    # Explicitly sign ancillary files as generic code before sealing the app.
    # Their signatures live in xattrs, which ditto preserves for the thin ZIP;
    # universal packaging relocates these data files into Resources instead.
    ancillary = sorted(path for path in (bundle / "Contents/MacOS").rglob("*")
                       if path.is_file() and not path.is_symlink() and not native(path))
    if ancillary:
        subprocess.run(["codesign", "--force", "--sign", identity, "--timestamp",
                        *(str(path) for path in ancillary)], check=True)
    subprocess.run([*common, "--entitlements", str(entitlements), str(bundle)], check=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", "--all-architectures", str(bundle)], check=True)
    for path in [*nested, *ancillary, bundle]:
        details = subprocess.run(["codesign", "-d", "--verbose=4", str(path)],
                                 check=True, capture_output=True, text=True).stderr
        runtime = re.search(r"^CodeDirectory.*flags=0x[0-9a-f]+\([^)]*\bruntime\b", details, re.MULTILINE)
        if (f"TeamIdentifier={team}\n" not in details or f"Authority={identity}\n" not in details
                or (path not in ancillary and not runtime)):
            raise ValueError(f"Unexpected signing identity or runtime flags: {path.name}")
    print(f"Developer ID verified: Team {team}, hardened runtime, {len(nested)} nested native files, {len(ancillary)} ancillary files.")


if __name__ == "__main__":
    sign(Path(sys.argv[1]).resolve(), os.environ.get("PNGR_MACOS_SIGN_IDENTITY", ""),
         os.environ.get("PNGR_MACOS_SIGN_TEAM", ""))
