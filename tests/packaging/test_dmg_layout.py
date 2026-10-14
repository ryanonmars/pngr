"""Finder layout uses deterministic positions and refuses incomplete staging."""
import importlib.util
import pathlib
import sys
import tempfile
import types
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / 'scripts/macos/configure-dmg.py'
SPEC = importlib.util.spec_from_file_location('dmg_layout', SCRIPT)
LAYOUT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LAYOUT)


class DmgLayoutTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.volume = pathlib.Path(self.temporary.name)
        self.entries = {}
        owner = self

        class Store:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def __getitem__(self, key): return owner.entries.setdefault(key, {})

        store = types.SimpleNamespace(DSStore=types.SimpleNamespace(open=lambda *args: Store()))
        alias = types.SimpleNamespace(Alias=types.SimpleNamespace(for_file=lambda path: types.SimpleNamespace(to_bytes=lambda: b'volume-background-alias')), Bookmark=types.SimpleNamespace(for_file=lambda path: 'volume-background-bookmark'))
        self.modules = mock.patch.dict(sys.modules, {'ds_store': store, 'mac_alias': alias})
        self.modules.start()
        self.addCleanup(self.modules.stop)

    def stage(self):
        (self.volume / 'pngr.app').mkdir()
        (self.volume / '.background').mkdir()
        (self.volume / '.background/install.png').write_bytes(b'background')
        (self.volume / 'Applications').symlink_to('/Applications')

    @unittest.skipIf(sys.platform == 'win32', 'Windows non-admin test users cannot create symlinks')
    def test_finder_positions_and_dimensions_fit_background(self):
        self.stage()
        LAYOUT.configure(self.volume)
        self.assertEqual(self.entries['pngr.app']['Iloc'], (190, 230))
        self.assertEqual(self.entries['Applications']['Iloc'], (530, 230))
        self.assertFalse(self.entries['.']['bwsp']['ShowSidebar'])
        self.assertFalse(self.entries['.']['bwsp']['ShowToolbar'])
        self.assertEqual(self.entries['.']['icvp']['iconSize'], 112.0)
        self.assertEqual(self.entries['.']['icvp']['backgroundImageAlias'], b'volume-background-alias')

    def test_case_only_public_name_cleanup_precedes_replacement(self):
        # On default APFS both spellings resolve to the same file. Removing the
        # old spelling after the move would silently delete the new installer.
        script = (SCRIPT.parents[1] / 'package-universal.sh').read_text()
        cleanup = script.index('rm -f artifacts/pngr-macos-universal.dmg')
        publication = script.index('mv "$stage/Pngr-macos-universal.dmg" artifacts/Pngr-macos-universal.dmg')
        self.assertLess(cleanup, publication)
        self.assertNotIn('rm -f artifacts/pngr-macos-universal.dmg', script[publication:])

    def test_missing_app_or_artwork_prevents_packaging(self):
        with self.assertRaisesRegex(ValueError, 'application and installation background'):
            LAYOUT.configure(self.volume)

    def test_real_directory_cannot_replace_applications_shortcut(self):
        (self.volume / 'pngr.app').mkdir()
        (self.volume / '.background').mkdir()
        (self.volume / '.background/install.png').write_bytes(b'background')
        (self.volume / 'Applications').mkdir()
        with self.assertRaisesRegex(ValueError, 'Applications must be a shortcut'):
            LAYOUT.configure(self.volume)


if __name__ == '__main__':
    unittest.main()
