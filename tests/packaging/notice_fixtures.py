"""Real reviewed notice material for synthetic packaging fixtures."""
from functools import lru_cache
import hashlib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('pngr_notice_generator', ROOT / 'scripts/licensing/generate.py')
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


@lru_cache
def materials(rid):
    inventory, _ = GENERATOR.load_inventory()
    libraries = {key: {'type': 'package'} for key in inventory['reviewedDistributionProfiles'][rid]}
    packages = GENERATOR.select_packages({'libraries': libraries}, rid)
    notices = GENERATOR.render(packages)
    manifest = {'schemaVersion': 1, 'rid': rid,
                'noticesSha256': hashlib.sha256(notices).hexdigest(), 'packages': packages}
    return notices, json.dumps(manifest).encode(), libraries
