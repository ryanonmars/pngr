#!/bin/sh
# Explicit local Developer ID build; release/CI signing is configured separately.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
export PNGR_MACOS_SIGN_IDENTITY='Developer ID Application: Ryan Poelker (VRNY8KM56D)'
export PNGR_MACOS_SIGN_TEAM=VRNY8KM56D
exec sh "$project_root/scripts/package.sh" "${1:-osx-arm64}" "${2:-0.8.0}"
