#!/usr/bin/env python3
"""Package an existing self-contained Windows publish using the pinned Velopack tool."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def package(version: str, publish: Path, output: Path, dotnet: str):
    publish = publish.resolve()
    deps = json.loads((publish / 'pngr.deps.json').read_text())
    if f'pngr/{version}' not in deps['libraries']:
        raise ValueError('Published application version does not match installer version.')
    if not deps['runtimeTarget']['name'].endswith('/win-x64'):
        raise ValueError('Installer requires a win-x64 publish.')
    output.mkdir(parents=True, exist_ok=True)
    # Build in isolation; leave an existing installer intact if packaging fails.
    temporary = output.with_name(output.name + '.pending')
    if temporary.exists():
        shutil.rmtree(temporary)
    environment = os.environ.copy()
    environment.setdefault('DOTNET_ROOT', str(Path(dotnet).resolve().parent))
    try:
        subprocess.run([dotnet, 'tool', 'restore'], cwd=ROOT, env=environment, check=True)
        subprocess.run([dotnet, 'tool', 'run', 'vpk', '--', '[win]', 'pack',
                        '--packId', 'pngr', '--packTitle', 'pngr',
                        '--packVersion', version, '--packDir', str(publish),
                        '--mainExe', 'pngr.exe', '--runtime', 'win-x64',
                        '--channel', 'win-x64', '--delta', 'None',
                        '--icon', str(ROOT / 'src/TraceScope.App/Assets/Icons/pngr.ico'),
                        '--splashImage', str(ROOT / 'src/TraceScope.App/Assets/Icons/pngr-windows.png'),
                        '--outputDir', str(temporary), '--skip-updates'],
                       cwd=ROOT, env=environment, check=True)
        setup = list(temporary.glob('*Setup.exe'))
        if len(setup) != 1 or not (temporary / 'releases.win-x64.json').is_file():
            raise ValueError('Velopack did not generate the expected installer/feed.')
        setup[0].rename(temporary / 'pngr-Setup.exe')
        # Portable ZIP is retained by the old internal packaging flow, not a public release asset.
        for portable in temporary.glob('*Portable.zip'):
            portable.unlink()
        # CLI upload metadata references generated installer/portable names; our release
        # workflow publishes the renamed installer directly and uses releases.*.json.
        for upload_metadata in temporary.glob('assets.*.json'):
            upload_metadata.unlink()
        for path in output.iterdir():
            if path.is_file():
                path.unlink()
        for path in temporary.iterdir():
            shutil.move(str(path), output / path.name)
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('version')
    parser.add_argument('--publish', type=Path, default=ROOT / 'artifacts/publish/win-x64')
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/installers/win-x64')
    parser.add_argument('--dotnet', default=shutil.which('dotnet') or str(ROOT / '.tools/dotnet/dotnet'))
    args = parser.parse_args()
    package(args.version.removeprefix('v'), args.publish, args.output, args.dotnet)
