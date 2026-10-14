#!/bin/sh
# Build the distributable bundles with their platform icons.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"
runtime=${1:-osx-arm64}
version=${2:-0.7.3}
version=${version#v}
if ! printf '%s\n' "$version" | LC_ALL=C grep -Eq '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$'; then
    echo "Version must be semantic X.Y.Z, optionally with a prerelease suffix." >&2
    exit 2
fi
bundle_version=${version%%-*}
case "$runtime" in
    osx-arm64) destination=artifacts/macos ;;
    osx-x64) destination=artifacts/macos-x64 ;;
    win-x64) destination=artifacts/windows ;;
    *) echo "Usage: $0 [osx-arm64|osx-x64|win-x64] [version]" >&2; exit 2 ;;
esac
sdk=dotnet
if [ -x .tools/dotnet/dotnet ]; then sdk=.tools/dotnet/dotnet; fi
publish_dir="artifacts/publish/$runtime"
# Publish into a clean generated directory so removed assemblies cannot survive.
rm -rf "$publish_dir"
"$sdk" publish src/TraceScope.App/TraceScope.App.csproj -c Release -r "$runtime" --self-contained true -p:Version="$version" -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=None -p:DebugSymbols=false -o "$publish_dir"
if [ "$runtime" = win-x64 ]; then
    mkdir -p "$destination"
    rsync -a --delete "$publish_dir/" "$destination/"
    # Replace the archive so removed/renamed files cannot linger in an older ZIP.
    archive_tmp=$(mktemp "$project_root/artifacts/pngr-windows.XXXXXX")
    trap 'rm -f "$archive_tmp"' EXIT HUP INT TERM
    python3 - "$publish_dir" "$archive_tmp" <<'PY'
import pathlib, sys, zipfile
source = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(source.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(source))
PY
    mv "$archive_tmp" artifacts/pngr-win-x64.zip
    chmod 644 artifacts/pngr-win-x64.zip
else
    bundle="$destination/pngr.app"
    mkdir -p "$bundle/Contents/MacOS"
    # Resources are generated, not user data. Remove obsolete branding/assets.
    rm -rf "$bundle/Contents/Resources"
    mkdir -p "$bundle/Contents/Resources"
    rsync -a --delete "$publish_dir/" "$bundle/Contents/MacOS/"
    cp src/TraceScope.App/Platforms/macOS/Info.plist "$bundle/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $bundle_version" "$bundle/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $bundle_version" "$bundle/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Add :pngrReleaseVersion string $version" "$bundle/Contents/Info.plist"
    cp src/TraceScope.App/Assets/Icons/pngr.icns "$bundle/Contents/Resources/pngr.icns"
    cp -R src/TraceScope.App/Platforms/macOS/en.lproj "$bundle/Contents/Resources/"
    codesign --force --deep --sign - "$bundle"
    codesign --verify --deep --strict "$bundle"
    touch "$bundle"
    # Keep a single current archive for each platform instead of stacking versions.
    archive_tmp=$(mktemp "$project_root/artifacts/pngr-macos.XXXXXX")
    trap 'rm -f "$archive_tmp"' EXIT HUP INT TERM
    ditto -c -k --sequesterRsrc --keepParent "$bundle" "$archive_tmp"
    mv "$archive_tmp" "artifacts/pngr-$runtime.zip"
    chmod 644 "artifacts/pngr-$runtime.zip"
fi
# Remove the old generated distribution only after its replacement succeeds.
rm -f "artifacts/TraceScope-$runtime.zip"
if [ "$runtime" != win-x64 ]; then rm -rf "$destination/TraceScope.app"; fi
python3 scripts/verify-package.py "artifacts/pngr-$runtime.zip" "$runtime" "$version"
echo "Built $destination ($version)"
