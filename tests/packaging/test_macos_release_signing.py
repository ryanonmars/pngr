"""Release credential isolation and native signing; no real private keys used."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('release_signing', ROOT / 'scripts/macos/release-signing.py')
SIGNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNER)


class ReleaseSigningTests(unittest.TestCase):
    def test_prepare_rejects_nonrelease_events_before_any_access(self):
        for event, ref, version in [('pull_request', 'refs/tags/v0.8.0', 'v0.8.0'),
                                     ('workflow_dispatch', 'refs/tags/v0.8.0', 'v0.8.0'),
                                     ('push', 'refs/heads/main', 'v0.8.0'),
                                     ('push', 'refs/tags/v0.9.0', 'v0.8.0')]:
            with self.subTest(event=event, ref=ref), mock.patch.dict(os.environ, {
                'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': event, 'GITHUB_REF': ref, 'PNGR_VERSION': version
            }, clear=True), mock.patch.object(SIGNER, 'run') as run:
                with self.assertRaisesRegex(RuntimeError, 'tag push'):
                    SIGNER.prepare()
                run.assert_not_called()

    def test_prepare_requires_all_secrets_before_creating_keychain(self):
        with mock.patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'push',
                                         'GITHUB_REF': 'refs/tags/v0.8.0', 'PNGR_VERSION': 'v0.8.0'}, clear=True), mock.patch.object(SIGNER, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'secret is missing'):
                SIGNER.prepare()
            run.assert_not_called()

    def test_prepare_is_isolated_and_exports_only_nonsecret_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            envfile = Path(directory) / 'env'
            environment = {
                'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'push',
                'GITHUB_REF': 'refs/tags/v0.8.0', 'PNGR_VERSION': 'v0.8.0',
                'RUNNER_TEMP': directory, 'GITHUB_ENV': str(envfile),
                'PNGR_MACOS_CERTIFICATE_P12_BASE64': base64.b64encode(b'disposable-p12').decode() + '\n',
                'PNGR_MACOS_CERTIFICATE_PASSWORD': 'disposable-password',
                'PNGR_APPLE_NOTARY_KEY_P8_BASE64': base64.b64encode(b'disposable-p8').decode(),
                'PNGR_APPLE_NOTARY_KEY_ID': 'key-id', 'PNGR_APPLE_NOTARY_ISSUER_ID': 'issuer-id'}
            def invoke(args):
                if 'find-identity' in args:
                    return f'1) {SIGNER.FINGERPRINT} "{SIGNER.IDENTITY}"\n'
                if 'list-keychains' in args and '-s' not in args:
                    return '"/original/login.keychain-db"'
                return ''
            previous_umask = os.umask(0o022)
            self.addCleanup(os.umask, previous_umask)
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(SIGNER, 'run', side_effect=invoke) as run, mock.patch('builtins.print'):
                SIGNER.prepare()
            calls = [call.args[0] for call in run.call_args_list]
            imported = next(args for args in calls if 'import' in args)
            self.assertNotIn('-A', imported)
            self.assertEqual(imported[-2:], ['-T', '/usr/bin/codesign'])
            partitions = next(args for args in calls if 'set-key-partition-list' in args)
            state = Path(directory) / 'pngr-release-signing'
            self.assertEqual(partitions[-1], str(state / 'signing.keychain-db'))
            self.assertFalse((state / 'identity.p12').exists())
            self.assertFalse((state / 'notary.p8').exists())
            exported = envfile.read_text()
            self.assertIn('PNGR_MACOS_RELEASE_SIGNING=1', exported)
            for secret in ('disposable-p12', 'disposable-p8', 'disposable-password', 'key-id', 'issuer-id'):
                self.assertNotIn(secret, exported)
            self.assertEqual(json.loads((state / 'search-list.json').read_text()), ['/original/login.keychain-db'])

    def test_native_errors_do_not_include_secret_arguments_or_output(self):
        result = subprocess.CompletedProcess([], 1, b'secret-output', b'secret-error')
        with mock.patch.object(SIGNER.subprocess, 'run', return_value=result):
            with self.assertRaises(RuntimeError) as error:
                SIGNER.run(['/usr/bin/security', 'import', 'secret-password'])
        self.assertNotIn('secret', str(error.exception))

    def test_velopack_entitlements_use_required_extension(self):
        script = (ROOT / 'scripts/package-universal.sh').read_text()
        import re
        match = re.search(r'--signEntitlements "\$project_root/([^"\n]+)"', script)
        self.assertIsNotNone(match)
        path = ROOT / match.group(1)
        self.assertEqual(path.suffix, '.entitlements')  # vpk 1.2.161 rejects .plist.
        self.assertEqual(plistlib.loads(path.read_bytes()), {})

    def test_release_never_silently_falls_back_without_configuration(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(RuntimeError):
                SIGNER.configuration()

    def test_universal_hosts_have_stable_identifiers_and_jit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / 'pngr.app'
            (bundle / 'Contents/MacOS').mkdir(parents=True)
            (bundle / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': SIGNER.BUNDLE_ID}))
            natives = [bundle / 'Contents/MacOS/pngr']
            hosts = []
            for rid in ('osx-arm64', 'osx-x64'):
                payload = bundle / 'Contents/Resources/runtimes' / rid
                payload.mkdir(parents=True)
                hosts.append(payload / 'pngr')
                natives += [hosts[-1], payload / 'native.dylib']
                (payload / 'managed.dll').write_bytes(b'MZ assembly')
            for native in natives:
                native.write_bytes(bytes.fromhex('cffaedfe') + bytes(28))
            with mock.patch.object(SIGNER, 'configuration', return_value=('temporary.keychain', 'profile')), mock.patch.object(SIGNER, 'run') as run:
                SIGNER.sign(bundle)
            calls = [call.args[0] for call in run.call_args_list]
            sign_calls = [args for args in calls if '--sign' in args]
            self.assertEqual(len(sign_calls), 5)
            for args in sign_calls:
                self.assertIn('--timestamp', args)
                self.assertIn('runtime', args)
                self.assertNotIn('--deep', args)
                self.assertNotIn('-', args)
                if Path(args[-1]) in hosts:
                    self.assertIn(SIGNER.BUNDLE_ID, args)
                    self.assertIn('--entitlements', args)
                else:
                    self.assertNotIn('--entitlements', args)
            self.assertEqual(sign_calls[-1][-1], str(bundle))

    def test_notarization_must_be_accepted_before_stapling(self):
        with mock.patch.object(SIGNER, 'configuration', return_value=('keychain', 'profile')), mock.patch.object(SIGNER, 'run', side_effect=['', json.dumps({'status': 'Invalid'})]) as run:
            with self.assertRaisesRegex(RuntimeError, 'did not accept'):
                SIGNER.notarize_dmg(Path('Pngr.dmg'))
            self.assertEqual(run.call_count, 2)

    def test_cleanup_restores_search_list_and_deletes_only_temporary_keychain(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / 'pngr-release-signing'
            state.mkdir()
            (state / 'search-list.json').write_text(json.dumps(['/original/login.keychain-db']))
            (state / 'signing.keychain-db').touch()
            with mock.patch.dict(os.environ, {'RUNNER_TEMP': directory}), mock.patch.object(SIGNER, 'run') as run:
                SIGNER.cleanup()
            calls = [call.args[0] for call in run.call_args_list]
            self.assertEqual(calls[0], ['/usr/bin/security', 'list-keychains', '-d', 'user', '-s', '/original/login.keychain-db'])
            self.assertEqual(calls[1][-1], str(state / 'signing.keychain-db'))
            self.assertFalse(state.exists())
