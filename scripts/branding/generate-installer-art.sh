#!/usr/bin/env bash
# Regenerate checked-in NSIS artwork from the canonical transparent wordmark.
# ImageMagick is an artwork-authoring dependency, not a packaging/runtime dependency.
set -euo pipefail
cd "$(dirname "$0")/../.."
wordmark=src/TraceScope.App/Assets/Branding/Pngr-wordmark.png
output=src/TraceScope.App/Assets/Branding/Installer
mkdir -p "$output"
magick -size 150x57 canvas:'#171a1c' \
  \( "$wordmark" -resize 120x42 \) -gravity center -composite \
  -type TrueColor "BMP3:$output/windows-header.bmp"
magick -size 164x314 canvas:'#171a1c' \
  \( "$wordmark" -resize 134x46 \) -gravity northwest -geometry +15+36 -composite \
  -type TrueColor "BMP3:$output/windows-welcome.bmp"
