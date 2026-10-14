"""The checked-in native vector artwork must exactly match the supplied SVG."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'src/TraceScope.App/Assets/Branding'
SPEC = importlib.util.spec_from_file_location('wordmark', ROOT / 'scripts/branding/generate-wordmark.py')
WORDMARK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WORDMARK)


class WordmarkTests(unittest.TestCase):
    def test_checked_in_vectors_are_regenerated_from_exact_source_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            WORDMARK.generate(ASSETS / 'Pngr-Font.svg', Path(directory), raster=False)
            self.assertEqual((ASSETS / 'Wordmark.axaml').read_bytes(), (Path(directory) / 'Wordmark.axaml').read_bytes())
        svg = ET.parse(ASSETS / 'Pngr-Font.svg')
        drawing = ET.parse(ASSETS / 'Wordmark.axaml')
        original = svg.findall('.//{http://www.w3.org/2000/svg}path')
        generated = drawing.findall('.//{https://github.com/avaloniaui}GeometryDrawing')
        self.assertEqual([path.attrib['d'] for path in original], [path.attrib['Geometry'] for path in generated])
        self.assertEqual(['#1b95ff', '#ffffff', '#ffffff', '#ffffff'], [path.attrib['Brush'] for path in generated])

    def test_installer_wordmark_has_transparent_alpha_and_no_square_padding(self):
        png = (ASSETS / 'Pngr-wordmark.png').read_bytes()
        self.assertEqual(b'\x89PNG\r\n\x1a\n', png[:8])
        width, height, depth, color = struct.unpack('>IIBB', png[16:26])
        self.assertEqual(8, depth)
        self.assertEqual(6, color, 'Installer wordmark must retain RGBA transparency.')
        self.assertGreater(width, 2000)
        self.assertTrue(2.8 < width / height < 3.0)


if __name__ == '__main__':
    unittest.main()
