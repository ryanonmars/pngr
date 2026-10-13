#!/bin/sh
# Build the distributable bundles with their platform icons.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"
runtime=${1:-osx-arm64}
case "$runtime" in
    osx-arm64) destination=artifacts/macos ;;
    osx-x64) destination=artifacts/macos-x64 ;;
    win-x64) destination=artifacts/windows ;;
    *) echo "Usage: $0 [osx-arm64|osx-x64|win-x64]" >&2; exit 2 ;;
esac
sdk=dotnet
if [ -x .tools/dotnet/dotnet ]; then sdk=.tools/dotnet/dotnet; fi
publish_dir="artifacts/publish/$runtime"
"$sdk" publish src/TraceScope.App/TraceScope.App.csproj -c Release -r "$runtime" --self-contained true -o "$publish_dir"
if [ "$runtime" = win-x64 ]; then
    mkdir -p "$destination"
    rsync -a "$publish_dir/" "$destination/"
    # Replace the archive so removed/renamed files cannot linger in an older ZIP.
    archive_tmp=$(mktemp "$project_root/artifacts/tracescope-windows.XXXXXX")
    trap 'rm -f "$archive_tmp"' EXIT HUP INT TERM
    python3 - "$publish_dir" "$archive_tmp" <<'PY'
import pathlib, sys, zipfile
source = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(source.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(source))
PY
    mv "$archive_tmp" artifacts/TraceScope-win-x64.zip
    chmod 644 artifacts/TraceScope-win-x64.zip
else
    bundle="$destination/TraceScope.app"
    mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
    rsync -a "$publish_dir/" "$bundle/Contents/MacOS/"
    cp src/TraceScope.App/Platforms/macOS/Info.plist "$bundle/Contents/Info.plist"
    cp src/TraceScope.App/Assets/Icons/tracescope.icns "$bundle/Contents/Resources/TraceScope.icns"
    codesign --force --deep --sign - "$bundle"
    codesign --verify --deep --strict "$bundle"
    touch "$bundle"
fi
echo "Built $destination"
