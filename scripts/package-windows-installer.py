#!/usr/bin/env python3
"""Package Windows updates with Velopack and a conventional per-user NSIS wizard."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def nsis_compiler():
    configured = os.environ.get('PNGR_MAKENSIS')
    candidates = [configured, shutil.which('makensis')]
    if os.name == 'nt':
        for variable in ('ProgramFiles(x86)', 'ProgramFiles'):
            base = os.environ.get(variable)
            if base:
                candidates.append(str(Path(base) / 'NSIS/makensis.exe'))
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    raise ValueError('NSIS is required: brew install makensis (macOS) or choco install nsis (Windows).')


def build_wizard(version: str, payload: Path, destination: Path):
    compiler = nsis_compiler()
    # NSIS uses /D on Windows, -D on Unix. Paths are passed as argument values,
    # never shell-interpolated; spaces in checkout/build directories are safe.
    prefix = '/' if os.name == 'nt' else '-'
    subprocess.run([compiler, f'{prefix}DVERSION={version}',
                    f'{prefix}DNUMERIC_VERSION={version.split("-", 1)[0]}.0',
                    f'{prefix}DPAYLOAD={payload}', f'{prefix}DOUTPUT={destination}',
                    f'{prefix}DICON={ROOT / "src/TraceScope.App/Assets/Icons/pngr.ico"}',
                    f'{prefix}DHEADER_BITMAP={ROOT / "src/TraceScope.App/Assets/Branding/Installer/windows-header.bmp"}',
                    f'{prefix}DWELCOME_BITMAP={ROOT / "src/TraceScope.App/Assets/Branding/Installer/windows-welcome.bmp"}',
                    str(ROOT / 'scripts/windows/installer.nsi')], check=True)
    if not destination.is_file() or destination.stat().st_size < 1024:
        raise ValueError('NSIS did not generate the expected installation wizard.')


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
                        '--packId', 'pngr', '--packTitle', 'Pngr',
                        '--packVersion', version, '--packDir', str(publish),
                        '--mainExe', 'pngr.exe', '--runtime', 'win-x64',
                        '--channel', 'win-x64', '--delta', 'None',
                        '--icon', str(ROOT / 'src/TraceScope.App/Assets/Icons/pngr.ico'),
                        '--splashImage', str(ROOT / 'src/TraceScope.App/Assets/Branding/Pngr-wordmark.png'),
                        '--outputDir', str(temporary), '--skip-updates'],
                       cwd=ROOT, env=environment, check=True)
        setup = list(temporary.glob('*Setup.exe'))
        if len(setup) != 1 or not (temporary / 'releases.win-x64.json').is_file():
            raise ValueError('Velopack did not generate the expected installer/feed.')
        payload = temporary / 'VelopackSetup.exe'
        setup[0].rename(payload)
        build_wizard(version, payload, temporary / 'Pngr-Setup.exe')
        # The embedded one-click setup is not an additional public download.
        payload.unlink()
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
