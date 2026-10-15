import copy
import json
import subprocess
from pathlib import Path
import tempfile
import unittest
import generate
import verify


class DistributionNoticesTests(unittest.TestCase):
    def deps(self, rid='osx-arm64'):
        desktop, _ = generate.load_inventory()
        return {'libraries': {key: {'type': 'package'} for key in desktop['reviewedDistributionProfiles'][rid]}}

    def test_all_authoritative_notice_inputs_are_tracked(self):
        tracked = set(subprocess.check_output(
            ['git', 'ls-files', '-z'], cwd=generate.ROOT).decode().split('\0'))
        desktop, rust = generate.load_inventory()
        packages = desktop['packages'] + desktop.get('additionalComponents', []) + rust['packages']
        for package in packages:
            for notice in package['notices']:
                self.assertIn(notice['path'], tracked, "License input would be missing from a clean checkout")

    def test_every_reviewed_rid_round_trips_offline(self):
        for rid in generate.RIDS:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as td:
                root = Path(td)
                (root / 'pngr.deps.json').write_text(json.dumps(self.deps(rid)))
                generated = generate.generate(root, rid)
                self.assertEqual(verify.validate_directory(root, rid), generated)
                self.assertGreater((root / 'THIRD-PARTY-NOTICES.txt').stat().st_size, 10000)

    def test_public_desktop_superset_is_current(self):
        desktop, rust = generate.load_inventory()
        packages = [x for x in desktop['packages'] if x['kind'] != 'build-only'] + desktop.get('additionalComponents', []) + rust['packages']
        self.assertEqual((generate.COMPLIANCE / 'THIRD-PARTY-NOTICES.txt').read_bytes(), generate.render(packages))

    def test_windows_installer_source_availability_is_distributed(self):
        packages = generate.select_packages(self.deps('win-x64'), 'win-x64')
        notices = generate.render(packages)
        self.assertIn(b'https://github.com/kichik/nsis/archive/refs/tags/v312.tar.gz', notices)
        self.assertIn(b'COMMON PUBLIC LICENSE VERSION 1.0', notices)

    def test_unreviewed_version_is_rejected(self):
        deps = self.deps()
        key = next(k for k in deps['libraries'] if k.startswith('Avalonia/'))
        deps['libraries'][key.replace('11.3.20', '99.0.0')] = deps['libraries'].pop(key)
        with self.assertRaisesRegex(ValueError, 'differs from reviewed'):
            generate.select_packages(deps, 'osx-arm64')

    def test_missing_dependency_is_rejected(self):
        deps = self.deps()
        deps['libraries'].pop('Velopack/1.2.161')
        with self.assertRaises(ValueError):
            generate.select_packages(deps, 'osx-arm64')

    def test_corrupted_notice_rejected(self):
        packages = generate.select_packages(self.deps(), 'osx-arm64')
        notices = generate.render(packages)
        manifest = {'schemaVersion': 1, 'rid': 'osx-arm64', 'noticesSha256': 'bad', 'packages': packages}
        with self.assertRaisesRegex(ValueError, 'do not match'):
            verify.validate_bytes(notices, json.dumps(manifest).encode(), self.deps())

    def test_empty_inventory_cannot_validate_without_deps(self):
        notices = generate.render([])
        manifest = {'schemaVersion': 1, 'rid': 'osx-arm64', 'noticesSha256': generate.hashlib.sha256(notices).hexdigest(), 'packages': []}
        with self.assertRaisesRegex(ValueError, 'does not match'):
            verify.validate_bytes(notices, json.dumps(manifest).encode())

    def test_original_upstream_license_bytes_are_preserved(self):
        packages = generate.select_packages(self.deps(), 'osx-arm64')
        output = generate.render(packages)
        for package in packages:
            for notice in package['notices']:
                self.assertIn((generate.ROOT / notice['path']).read_bytes(), output)

    def test_modified_reviewed_license_file_fails(self):
        packages = copy.deepcopy(generate.select_packages(self.deps(), 'osx-arm64'))
        packages[0]['notices'][0]['sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'license text changed'):
            generate.render(packages)

    def test_missing_reviewed_license_file_fails(self):
        packages = copy.deepcopy(generate.select_packages(self.deps(), 'osx-arm64'))
        packages[0]['notices'] = []
        with self.assertRaisesRegex(ValueError, 'Unresolved notice coverage'):
            generate.render(packages)


if __name__ == '__main__':
    unittest.main()
