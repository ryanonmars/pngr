#!/usr/bin/env python3
"""Verify and stage an allowlisted, credential-free macOS inspection artifact."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import sys


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ROOT = Path(__file__).resolve().parents[1]
VERIFY = load("pngr_verification_release", ROOT / "macos/verify-release.py")
ASSETS = load("pngr_verification_feed", ROOT / "release-assets.py")
VERSION = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?"


def prepare(source, bundle, version, output, commit, run_id):
    if not re.fullmatch(VERSION, version):
        raise ValueError("Invalid package version")
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or not re.fullmatch(r"[0-9]+", run_id):
        raise ValueError("Invalid verification provenance")
    if output.exists() or output.is_symlink():
        raise ValueError("Inspection output must not already exist")
    required = {"Pngr-macos-universal.dmg", "releases.osx-universal.json",
                f"pngr-{version}-osx-universal-full.nupkg"}
    allowed = required | {"RELEASES-osx-universal"}
    files = list(source.iterdir())
    if (not required <= {path.name for path in files}
            or any(path.name not in allowed or path.is_symlink() or not path.is_file() for path in files)):
        raise ValueError("Unexpected or missing inspection source artifact")
    # No historical or unrelated feed entries belong in this isolated inspection output.
    feed = json.loads((source / "releases.osx-universal.json").read_text())
    if len(feed.get("Assets", [])) != 1:
        raise ValueError("Expected a single inspection update")
    package = ASSETS.validate_feed(source, "osx-universal", version)
    if package.name != f"pngr-{version}-osx-universal-full.nupkg":
        raise ValueError("Unexpected inspection update filename")
    VERIFY.verify_app(bundle)
    VERIFY.verify_dmg(source / "Pngr-macos-universal.dmg")
    VERIFY.verify_updates(source, version, bundle)
    # Construct the report from a fixed schema, never native command output or environment values.
    report = {"schema_version": 1, "status": "verified", "commit": commit, "run_id": run_id,
              "version": version, "team_id": VERIFY.TEAM, "identity": VERIFY.IDENTITY,
              "checks": {name: "passed" for name in (
                  "app_signature_and_entitlements", "hardened_runtime_and_timestamps",
                  "app_stapled_ticket_and_gatekeeper", "dmg_signature_ticket_and_gatekeeper",
                  "update_payload_matches_app", "update_signatures_and_permissions", "feed_integrity")}}
    try:
        output.mkdir(parents=True)
        for path in sorted(files):
            shutil.copyfile(path, output / path.name)
        (output / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        checksums = "".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
                            for path in sorted(output.iterdir()))
        (output / "SHA256SUMS").write_text(checksums, encoding="ascii")
    except Exception:
        shutil.rmtree(output)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("version")
    parser.add_argument("output", type=Path)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", required=True)
    args = parser.parse_args()
    try:
        prepare(args.source, args.bundle, args.version, args.output, args.commit, args.run_id)
    except Exception:
        # Native diagnostics can contain host paths; the artifact and logs get no raw exception data.
        print("macOS inspection artifact verification failed; no new verified artifact was prepared.", file=sys.stderr)
        return 1
    print("Verified macOS inspection artifacts and sanitized report prepared.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
