#!/bin/sh
# Universal 2 launcher plus separate self-contained .NET payloads, installed as one app.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"
version=${1:-0.7.3}
version=${version#v}
archive_directory=${PNGR_UNIVERSAL_ARCHIVE_DIR:-artifacts}
# CI provides validated architecture ZIPs; local builds can publish both payloads here.
if [ "${PNGR_UNIVERSAL_USE_ARCHIVES:-0}" != 1 ]; then
    scripts/package.sh osx-arm64 "$version"
    scripts/package.sh osx-x64 "$version"
fi
mkdir -p artifacts/universal
stage=$(mktemp -d "$project_root/artifacts/universal/build.XXXXXX")
mounted_volume=
cleanup() {
    if [ -n "$mounted_volume" ]; then hdiutil detach -quiet "$mounted_volume" || true; fi
    rm -rf "$stage"
}
trap cleanup EXIT HUP INT TERM
bundle="$stage/pngr.app"
mkdir -p "$bundle/Contents/MacOS"
mkdir -p "$bundle/Contents/Resources/runtimes"
for rid in osx-arm64 osx-x64; do
    mkdir "$stage/$rid"
    ditto -x -k "$archive_directory/pngr-$rid.zip" "$stage/$rid"
    mv "$stage/$rid/pngr.app/Contents/MacOS" "$bundle/Contents/Resources/runtimes/$rid"
done
cp "$stage/osx-arm64/pngr.app/Contents/Info.plist" "$bundle/Contents/Info.plist"
cp -R "$stage/osx-arm64/pngr.app/Contents/Resources/." "$bundle/Contents/Resources/"
xcrun clang -O2 -Wall -Wextra -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 \
    scripts/macos/universal-launcher.c -o "$bundle/Contents/MacOS/pngr"
# Ad-hoc signatures satisfy runtime integrity; this is NOT Developer ID signing or notarization.
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
python3 scripts/verify-macos-universal.py "$bundle" "$version"
output="$stage/releases"
mkdir -p "$output"
sdk=dotnet
if [ -x .tools/dotnet/dotnet ]; then
    sdk=.tools/dotnet/dotnet
    export DOTNET_ROOT="$project_root/.tools/dotnet"
fi
# vpk accepts prebuilt bundles and preserves the legacy identifier and universal launcher.
# '-' is ad-hoc integrity signing, not a trusted identity. Gatekeeper remains unchanged.
"$sdk" tool restore
"$sdk" tool run vpk -- pack --packId pngr --packVersion "$version" --packTitle Pngr \
    --packDir "$bundle" --mainExe pngr --runtime osx-arm64 --channel osx-universal \
    --bundleId dev.tracescope.desktop --noInst --signAppIdentity - --outputDir "$output" \
    --delta None --skip-updates
# DMG and the internal update package must contain the same updater-enabled application.
portable=$(find "$output" -maxdepth 1 -name '*-Portable.zip' -print | head -1)
if [ -z "$portable" ]; then echo "Velopack portable bundle missing" >&2; exit 1; fi
mkdir "$stage/enriched"
ditto -x -k "$portable" "$stage/enriched"
rm -rf "$bundle"
# Velopack names its portable bundle from packTitle. Preserve the installed
# basename used by 0.7.1 while displaying Pngr via localized bundle metadata.
mv "$stage/enriched/Pngr.app" "$bundle"
python3 scripts/verify-macos-universal.py "$bundle" "$version" --require-updater --verify-update-permissions
codesign --verify --deep --strict "$bundle"
mkdir "$stage/image"
mv "$bundle" "$stage/image/pngr.app"
ln -s /Applications "$stage/image/Applications"
# Finder presentation is generated on the mounted image so its background alias
# references the image volume, not a temporary build directory.
layout_python="$project_root/.tools/dmg-layout/bin/python3"
if [ ! -x "$layout_python" ]; then python3 -m venv "$project_root/.tools/dmg-layout"; fi
"$layout_python" -m pip install --disable-pip-version-check -q -r scripts/macos/dmg-requirements.txt
mkdir -p "$stage/image/.background"
xcrun swift scripts/macos/dmg-background.swift "$stage/image/.background/install.png" \
  src/TraceScope.App/Assets/Branding/Pngr-wordmark.png
hdiutil create -quiet -volname Pngr -srcfolder "$stage/image" -format UDRW \
    -fs HFS+ "$stage/install-writable.dmg"
mkdir "$stage/mounted"
hdiutil attach -quiet -nobrowse -mountpoint "$stage/mounted" "$stage/install-writable.dmg"
mounted_volume="$stage/mounted"
"$layout_python" scripts/macos/configure-dmg.py "$mounted_volume"
hdiutil detach -quiet "$mounted_volume"
mounted_volume=
hdiutil convert -quiet "$stage/install-writable.dmg" -format UDZO -o "$stage/Pngr-macos-universal.dmg"
hdiutil verify -quiet "$stage/Pngr-macos-universal.dmg"
# Validate the final read-only image, not only staging metadata.
hdiutil attach -quiet -nobrowse -mountpoint "$stage/mounted" "$stage/Pngr-macos-universal.dmg"
mounted_volume="$stage/mounted"
"$layout_python" scripts/macos/configure-dmg.py "$mounted_volume" --verify
hdiutil detach -quiet "$mounted_volume"
mounted_volume=
rm -rf artifacts/universal/pngr.app
mv "$stage/image/pngr.app" artifacts/universal/pngr.app
# APFS often ignores filename case: remove the old spelling before publishing
# the replacement, never after (that would delete the new Pngr file too).
rm -f artifacts/pngr-macos-universal.dmg
mv "$stage/Pngr-macos-universal.dmg" artifacts/Pngr-macos-universal.dmg
cp artifacts/Pngr-macos-universal.dmg "$output/Pngr-macos-universal.dmg"
# Portable ZIP is an internal intermediate, not a public download.
rm "$portable"
# CLI upload bookkeeping lists the intentionally removed portable ZIP; SDK uses releases.*.
find "$output" -maxdepth 1 -name 'assets.*.json' -delete
# Replace the complete feed only after every package and the installer validate.
# Never leave an older-version feed pointing at new-version files, or accumulate test releases.
final_output="$project_root/artifacts/installers/osx-universal"
mkdir -p "$(dirname "$final_output")"
if [ -d "$final_output" ]; then mv "$final_output" "$stage/previous-release"; fi
if ! mv "$output" "$final_output"; then
    if [ -d "$stage/previous-release" ]; then mv "$stage/previous-release" "$final_output"; fi
    exit 1
fi
echo "Built artifacts/Pngr-macos-universal.dmg ($version)"
