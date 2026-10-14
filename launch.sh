#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ -x "artifacts/macos/pngr.app/Contents/MacOS/pngr" ]; then
  exec artifacts/macos/pngr.app/Contents/MacOS/pngr --data-dir "$PWD/artifacts/data" "$@"
fi
if [ -x ".tools/dotnet/dotnet" ]; then
  exec .tools/dotnet/dotnet run --project src/TraceScope.App -- --data-dir "$PWD/artifacts/data" "$@"
fi
exec dotnet run --project src/TraceScope.App -- --data-dir "$PWD/artifacts/data" "$@"
