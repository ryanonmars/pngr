# Distribution notice maintenance

`generate.py` creates notices offline from checked-in authoritative license files.
Run it after self-contained `dotnet publish`, before signing or Velopack packaging:

```sh
python3 scripts/licensing/generate.py --publish-dir artifacts/publish/osx-arm64 --rid osx-arm64
python3 scripts/licensing/verify.py artifacts/publish/osx-arm64 --rid osx-arm64
```

The supported RIDs are `osx-arm64`, `osx-x64`, and `win-x64`. A changed published
NuGet/framework graph or Velopack tool version fails closed. Review upstream
license/NOTICE files for the new exact version, update the inventory and original
texts, update the reviewed distribution profiles, and run `generate.py --all`.
Do not merely change version strings to bypass a failed check.

`verify.validate_bytes(notices, inventory, deps=None)` supports archive checks.
Supplying the artifact's parsed `pngr.deps.json` verifies its exact dependency
graph. Without it, the manifest is still checked against the reviewed RID profile.

`compliance/THIRD-PARTY-NOTICES.txt` is the desktop superset; per-RID output includes
only its published managed/runtime graph and the corresponding reviewed native
updater/installer notice scope. Each original text is copied byte-for-byte,
identified by SHA-256, and attributed to its authoritative source. Duplicate text
is printed once with all component references. The files contain no PingPlotter
reverse-engineering material.

The Velopack Rust inventory records normal-dependency target trees, not an
independent attestation of its compiled binaries. Native build-feature/linkage
attestation remains an upstream provenance question. See the compliance inventory
for scope and limitations. Build tools, test packages, and unused platform-native
packages are not asserted to ship merely because they were restored.
