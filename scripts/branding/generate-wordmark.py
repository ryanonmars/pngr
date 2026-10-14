#!/usr/bin/env python3
"""Generate native Avalonia vectors and transparent installer art from the supplied SVG.

Raster regeneration requires rsvg-convert and ImageMagick on the developer's
machine. Generated assets are checked in; builds need neither tool.
"""
import argparse
from html import escape
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'src/TraceScope.App/Assets/Branding'


def generate(source: Path, destination: Path, raster=True):
    document = ET.parse(source)
    namespace = {'svg': 'http://www.w3.org/2000/svg'}
    styles = ''.join(element.text or '' for element in document.findall('.//svg:style', namespace))
    colors = dict(re.findall(r'\.([\w-]+)\s*\{\s*fill:\s*(#[0-9a-fA-F]{6})\s*\}', styles))
    paths = document.findall('.//svg:path', namespace)
    if not paths or set(colors.values()) != {'#1b95ff', '#ffffff'}:
        raise ValueError('Expected the supplied cyan/white path-based wordmark.')
    if document.findall('.//svg:rect', namespace) or any('transform' in element.attrib for element in document.iter()):
        raise ValueError('Unexpected background or transform: review the SVG before regenerating.')
    geometry = '\n'.join(f'        <GeometryDrawing Brush="{colors[path.attrib["class"]]}" Geometry="{escape(path.attrib["d"], quote=True)}"/>'
                         for path in paths)
    drawing = f'''<!-- Generated from Pngr-Font.svg by scripts/branding/generate-wordmark.py. -->
<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <DrawingImage x:Key="PngrWordmark">
    <DrawingImage.Drawing>
      <DrawingGroup>
{geometry}
      </DrawingGroup>
    </DrawingImage.Drawing>
  </DrawingImage>
</ResourceDictionary>
'''
    destination.mkdir(parents=True, exist_ok=True)
    (destination / 'Wordmark.axaml').write_text(drawing)
    if raster:
        with tempfile.TemporaryDirectory(prefix='Pngr-wordmark-') as temporary:
            rendered = Path(temporary) / 'source.png'
            subprocess.run(['rsvg-convert', '-w', '3240', '-h', '3240', str(source), '-o', str(rendered)], check=True)
            subprocess.run(['magick', str(rendered), '-trim', '+repage', '-strip',
                            str(destination / 'Pngr-wordmark.png')], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vector-only', action='store_true')
    arguments = parser.parse_args()
    generate(ASSETS / 'Pngr-Font.svg', ASSETS, raster=not arguments.vector_only)
