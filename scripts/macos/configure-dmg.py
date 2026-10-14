#!/usr/bin/env python3
"""Configure Finder icon layout without GUI automation or installer runtime dependencies."""
import argparse
import pathlib

WIDTH, HEIGHT = 720, 460
APP_POSITION = (190, 230)
APPLICATIONS_POSITION = (530, 230)


def configure(volume):
    from ds_store import DSStore
    from mac_alias import Alias, Bookmark

    background = volume / '.background' / 'install.png'
    if not (volume / 'pngr.app').is_dir() or not background.is_file():
        raise ValueError('Pngr application and installation background are required')
    if not (volume / 'Applications').is_symlink():
        raise ValueError('Applications must be a shortcut')
    with DSStore.open(str(volume / '.DS_Store'), 'w+') as store:
        store['.']['bwsp'] = {
            'ShowStatusBar': False, 'ShowTabView': False, 'ShowToolbar': False,
            'ShowPathbar': False, 'ShowSidebar': False,
            'WindowBounds': '{{180, 120}, {%d, %d}}' % (WIDTH, HEIGHT),
            'SidebarWidth': 0, 'ContainerShowSidebar': False, 'PreviewPaneVisibility': False,
        }
        store['.']['icvp'] = {
            'viewOptionsVersion': 1, 'backgroundType': 2,
            'backgroundImageAlias': Alias.for_file(str(background)).to_bytes(),
            'iconSize': 112.0, 'textSize': 14.0, 'labelOnBottom': True,
            'showItemInfo': False, 'showIconPreview': False,
            'arrangeBy': 'none', 'gridOffsetX': 0.0, 'gridOffsetY': 0.0,
            'gridSpacing': 100.0, 'scrollPositionX': 0.0, 'scrollPositionY': 0.0,
            'backgroundColorRed': 0.09, 'backgroundColorGreen': 0.10, 'backgroundColorBlue': 0.11,
        }
        store['.']['pBBk'] = Bookmark.for_file(str(background))
        store['.']['vSrn'] = ('long', 1)
        store['.']['icvl'] = ('type', 'icnv')
        store['pngr.app']['Iloc'] = APP_POSITION
        store['Applications']['Iloc'] = APPLICATIONS_POSITION
    verify(volume)


def verify(volume):
    from ds_store import DSStore
    with DSStore.open(str(volume / '.DS_Store'), 'r') as store:
        assert store['pngr.app']['Iloc'] == APP_POSITION
        assert store['Applications']['Iloc'] == APPLICATIONS_POSITION
        assert store['.']['bwsp']['WindowBounds'].endswith('{720, 460}}')
        assert store['.']['icvp']['iconSize'] == 112.0
        assert store['.']['icvp']['backgroundType'] == 2
        assert store['.']['icvp']['backgroundImageAlias']
        assert store['.']['pBBk']
    assert (volume / '.background/install.png').is_file()
    assert (volume / 'Applications').is_symlink()
    assert (volume / 'Applications').readlink() == pathlib.Path('/Applications')
    print('Verified Pngr Finder installation layout')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('volume', type=pathlib.Path)
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    (verify if args.verify else configure)(args.volume)
