"""Conventional installer presentation must not change Velopack install identity."""
import importlib.util
import json
from pathlib import Path
import subprocess
import struct
import tempfile
import unittest
from unittest import mock
from notice_fixtures import materials

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('windows_wizard', ROOT / 'scripts/package-windows-installer.py')
WINDOWS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WINDOWS)


class WindowsWizardTests(unittest.TestCase):
    def test_wizard_uses_existing_per_user_install_and_velopack_uninstall(self):
        script = (ROOT / 'scripts/windows/installer.nsi').read_text()
        self.assertIn('RequestExecutionLevel user', script)
        self.assertIn('InstallDir "$LOCALAPPDATA\\pngr"', script)
        self.assertIn('MUI_PAGE_WELCOME', script)
        self.assertIn('MUI_PAGE_INSTFILES', script)
        self.assertIn('MUI_PAGE_FINISH', script)
        self.assertIn('VelopackSetup.exe" --silent', script)
        self.assertIn('Update.exe" start pngr.exe', script)
        self.assertIn('SetErrorLevel $0', script)
        self.assertNotIn('WriteUninstaller', script)
        self.assertNotIn('WriteReg', script)
        self.assertNotIn('Delete ', script)
        self.assertNotIn('RMDir ', script)

    def test_wordmark_artwork_is_nsis_compatible_and_icon_is_unchanged(self):
        script = (ROOT / 'scripts/windows/installer.nsi').read_text()
        self.assertIn('MUI_HEADERIMAGE_BITMAP "${HEADER_BITMAP}"', script)
        self.assertIn('MUI_WELCOMEFINISHPAGE_BITMAP "${WELCOME_BITMAP}"', script)
        self.assertIn('MUI_ICON "${ICON}"', script)
        for name, size in [('windows-header.bmp', (150, 57)),
                           ('windows-welcome.bmp', (164, 314))]:
            bitmap = (ROOT / 'src/TraceScope.App/Assets/Branding/Installer' / name).read_bytes()
            self.assertEqual(b'BM', bitmap[:2])
            self.assertEqual(size, struct.unpack_from('<ii', bitmap, 18))
            self.assertEqual(24, struct.unpack_from('<H', bitmap, 28)[0])
            self.assertEqual(0, struct.unpack_from('<I', bitmap, 30)[0])

    def test_compiler_receives_paths_without_shell_interpolation(self):
        with tempfile.TemporaryDirectory(prefix='Pngr build ') as directory:
            output = Path(directory) / 'Pngr-Setup.exe'
            payload = Path(directory) / 'VelopackSetup.exe'
            def compile(arguments, **kwargs):
                self.assertIn(str(ROOT / 'scripts/windows/installer.nsi'), arguments)
                prefix = '/' if WINDOWS.os.name == 'nt' else '-'
                self.assertIn(f'{prefix}DPAYLOAD={payload}', arguments)
                for define, name in [('HEADER_BITMAP', 'windows-header.bmp'),
                                     ('WELCOME_BITMAP', 'windows-welcome.bmp')]:
                    artwork = ROOT / 'src/TraceScope.App/Assets/Branding/Installer' / name
                    self.assertIn(f'{prefix}D{define}={artwork}', arguments)
                self.assertNotIn('shell', kwargs)
                output.write_bytes(b'MZ' + b'0' * 2048)
            with mock.patch.object(WINDOWS, 'nsis_compiler', return_value='/tools/makensis'):
                with mock.patch.object(WINDOWS.subprocess, 'run', side_effect=compile), mock.patch.object(WINDOWS.subprocess, 'check_output', return_value='v3.12'):
                    WINDOWS.build_wizard('0.7.2', payload, output)

    def test_wrapper_failure_preserves_previous_installer_and_feed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = root / 'publish'
            publish.mkdir()
            (publish / 'pngr.deps.json').write_text(json.dumps({
                'libraries': {'pngr/0.7.2': {}},
                'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0/win-x64'}}))
            notices, inventory, libraries = materials('win-x64')
            (publish / 'LICENSE').write_bytes((ROOT / 'LICENSE').read_bytes())
            (publish / 'THIRD-PARTY-NOTICES.txt').write_bytes(notices)
            (publish / 'third-party-inventory.json').write_bytes(inventory)
            (publish / 'pngr.deps.json').write_text(json.dumps({
                'libraries': dict(libraries, **{'pngr/0.7.2': {'type': 'project'}}),
                'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0/win-x64'}}))
            output = root / 'output'
            output.mkdir()
            (output / 'Pngr-Setup.exe').write_bytes(b'previous installer')
            (output / 'releases.win-x64.json').write_text('previous feed')
            commands = []
            def run(arguments, **kwargs):
                commands.append(arguments)
                if 'pack' in arguments:
                    pending = output.with_name('output.pending')
                    pending.mkdir(exist_ok=True)
                    (pending / 'pngr-win-x64-Setup.exe').write_bytes(b'underlying setup')
                    (pending / 'releases.win-x64.json').write_text('{}')
            with mock.patch.object(WINDOWS.subprocess, 'run', side_effect=run):
                with mock.patch.object(WINDOWS, 'build_wizard', side_effect=subprocess.CalledProcessError(1, ['makensis'])):
                    with self.assertRaises(subprocess.CalledProcessError):
                        WINDOWS.package('0.7.2', publish, output, '/sdk/dotnet')
            self.assertEqual(b'previous installer', (output / 'Pngr-Setup.exe').read_bytes())
            self.assertEqual('previous feed', (output / 'releases.win-x64.json').read_text())
            arguments = commands[-1]
            self.assertEqual('pngr', arguments[arguments.index('--packId') + 1])
            self.assertEqual('Pngr', arguments[arguments.index('--packTitle') + 1])
            self.assertEqual('pngr.exe', arguments[arguments.index('--mainExe') + 1])
            self.assertEqual('win-x64', arguments[arguments.index('--channel') + 1])
            self.assertEqual(str(ROOT / 'src/TraceScope.App/Assets/Branding/Pngr-wordmark.png'),
                             arguments[arguments.index('--splashImage') + 1])
            self.assertFalse(output.with_name('output.pending').exists())


if __name__ == '__main__':
    unittest.main()
