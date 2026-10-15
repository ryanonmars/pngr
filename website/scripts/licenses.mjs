import { createHash } from 'node:crypto';
import { readFile, writeFile, readdir, access, mkdir, copyFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';

const hash = value => createHash('sha256').update(value).digest('hex');
const json = async path => JSON.parse(await readFile(path, 'utf8'));
const exists = async path => { try { await access(path); return true; } catch { return false; } };
const manifestPath = 'compliance/browser-dependencies.json';
const noticesPath = 'compliance/THIRD-PARTY-NOTICES.txt';
const lockBytes = await readFile('package-lock.json');
const lock = JSON.parse(lockBytes);

async function owner(resource) {
  // Next vendors packages whose package.json deliberately has no version.
  const vendored = resource.match(/^(node_modules\/next\/dist\/compiled\/(?:@[^/]+\/)?[^/]+)\//);
  if (vendored && await exists(`${vendored[1]}/LICENSE`)) return vendored[1];
  let dir = dirname(resource);
  while (dir.includes('node_modules')) {
    if (await exists(`${dir}/package.json`) && (await json(`${dir}/package.json`)).name) return dir;
    dir = dirname(dir);
  }
  throw new Error(`Cannot identify browser dependency: ${resource}`);
}

async function browserOwners() {
  const resources = await json('.next/browser-license-inputs.json');
  const owners = new Set(await Promise.all(resources.map(owner)));
  // Tailwind emits the stylesheet, rather than an imported JavaScript module.
  owners.add('node_modules/tailwindcss');
  return [...owners].sort();
}

async function collect(root) {
  const metadata = await json(`${root}/package.json`).catch(() => ({}));
  const vendored = root.startsWith('node_modules/next/dist/compiled/');
  let version = metadata.version ?? lock.packages[root]?.version;
  if (!version && vendored) {
    version = metadata.peerDependencies?.react;
    if (root.endsWith('/react')) {
      const source = await readFile(`${root}/cjs/react.production.js`, 'utf8');
      version = source.match(/exports.version = "([^"]+)"/)?.[1];
    }
    version ??= `vendored in next@${lock.packages['node_modules/next'].version}`;
  }
  const upstreamFallback = root === 'node_modules/react-remove-scroll-bar' && version === '2.3.8'
    ? 'compliance/upstream/react-remove-scroll-bar-2.3.8' : null;
  const licenseRoot = upstreamFallback ?? root;
  const files = (await readdir(licenseRoot)).filter(name => /^(licen[cs]e|copying|notice)([.-]|$)/i.test(name)).sort();
  if (!files.length) throw new Error(`Missing authoritative license text for ${root}; review before distributing`);
  const texts = await Promise.all(files.map(async file => {
    const path = `${licenseRoot}/${file}`;
    const text = await readFile(path, 'utf8');
    return { path, sha256: hash(text), text };
  }));
  let license = metadata.license ?? lock.packages[root]?.license;
  if (!license && texts.some(file => file.text.startsWith('MIT License'))) license = 'MIT';
  if (root === 'node_modules/lucide-react') license = 'ISC AND MIT (bundled Feather icons; see upstream LICENSE)';
  // Some Apache-2.0 packages ship only the short source-file notice. Include
  // the actual license, not just its URL; preserve the package's own attribution.
  if (license === 'Apache-2.0' && !texts.some(file => file.text.includes('TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION'))) {
    const path = 'compliance/upstream/Apache-2.0.txt';
    const text = await readFile(path, 'utf8');
    texts.push({ path, sha256: hash(text), text });
  }
  const obligations = license?.includes('Apache-2.0')
    ? 'Include Apache-2.0 text and applicable upstream NOTICE/attributions; identify modified upstream files if any. No upstream modifications made by this compliance pass.'
    : license?.startsWith('ISC') || license === 'MIT'
      ? 'Retain upstream copyright and permission notice with copies/substantial portions; complete upstream text is included.'
      : license === '0BSD' ? 'No attribution condition; upstream license text retained voluntarily.'
        : 'Review the complete included upstream license text; do not infer obligations from missing metadata.';
  return {
    path: root, name: metadata.name ?? root.split('/').at(-1), version,
    license: license ?? 'See included upstream license text', obligations,
    scope: root === 'node_modules/tailwindcss' ? 'generated stylesheet' : 'emitted browser code',
    source: upstreamFallback ? 'https://github.com/theKashey/react-remove-scroll-bar/blob/7301c160fda44cb8cf2b9fdfde61efad35736196/LICENSE (upstream canonical MIT text; npm 2.3.8 omits LICENSE and its recorded gitHead is unavailable; historical text remains unverified)'
      : vendored ? `npm next@${lock.packages['node_modules/next'].version}, ${root.replace('node_modules/next/', '')}` : `npm ${metadata.name}@${version}`,
    files: texts,
  };
}

if (process.argv.includes('--refresh')) {
  const packages = await Promise.all((await browserOwners()).map(collect));
  const notices = ['Pngr website — third-party notices', '',
    'The following upstream notices accompany browser JavaScript and generated stylesheets.',
    'License texts are reproduced verbatim from installed npm artifacts and the',
    'explicitly referenced upstream fallback sources recorded below.',
    'This inventory is generated from emitted client chunks, not all build dependencies.',
    'Next vendored components are identified separately; an upstream version is recorded',
    'when supplied, otherwise the exact containing Next package identifies the artifact.', '',
    ...packages.flatMap(pkg => [`${'='.repeat(78)}`, `${pkg.name} — ${pkg.version}`, `Source: ${pkg.source}`, `License metadata: ${pkg.license}`, '',
      ...pkg.files.flatMap(file => [`--- ${file.path} ---`, file.text, ''])]),
  ].join('\n');
  const manifest = { schemaVersion: 1, packageLockSha256: hash(lockBytes), noticesSha256: hash(notices),
    packages: packages.map(pkg => ({ ...pkg, files: pkg.files.map(({ text, ...file }) => file) })),
  };
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  await writeFile(noticesPath, notices);
  const browserPaths = new Set(packages.map(pkg => pkg.path));
  const inventory = {
    description: 'Exact lockfile inventory. Only browser/generated-style entries are asserted to ship; remaining entries are build/server/test tooling, including transitive packages of build-time fumadocs-mdx. Development classification follows npm lock metadata, not an inference about legal obligations.',
    assets: 'Website uses system Arial/Helvetica/sans-serif, with no downloaded font files. Pngr branding and application screenshots require the separate artwork provenance review; no ownership is inferred here.',
    packages: Object.entries(lock.packages).filter(([path]) => path).map(([path, pkg]) => ({ path, version: pkg.version, license: pkg.license ?? 'Not provided in lock metadata; not evaluated unless shipped',
      scope: browserPaths.has(path) ? 'browser/generated styles' : pkg.dev ? 'development/test/build; not observed in browser chunks' : 'server/build or tree-shaken; not observed in browser chunks',
      integrity: pkg.integrity,
    })),
  };
  await writeFile('compliance/dependency-inventory.json', JSON.stringify(inventory, null, 2) + '\n');
  console.log(`Reviewed notice inputs: ${packages.length}; lockfile entries: ${inventory.packages.length}`);
} else {
  const manifest = await json(manifestPath);
  if (manifest.packageLockSha256 !== hash(lockBytes)) throw new Error('Website dependency lock changed: review licenses and explicitly refresh compliance records after a build.');
  if (manifest.noticesSha256 !== hash(await readFile(noticesPath))) throw new Error('Third-party notices do not match reviewed inventory.');
  for (const pkg of manifest.packages) {
    const current = await collect(pkg.path);
    if (JSON.stringify(current.files.map(({ text, ...file }) => file)) !== JSON.stringify(pkg.files) || current.version !== pkg.version) {
      throw new Error(`License/version changed for ${pkg.path}; review required.`);
    }
  }
  if (process.argv.includes('--browser')) {
    const observed = await browserOwners();
    const covered = new Set(manifest.packages.map(pkg => pkg.path));
    for (const path of observed) if (!covered.has(path)) throw new Error(`Unreviewed emitted browser dependency: ${path}`);
  }
  await mkdir('public', { recursive: true });
  await copyFile(noticesPath, 'public/THIRD-PARTY-NOTICES.txt');
  await copyFile('compliance/dependency-inventory.json', 'public/dependency-inventory.json');
  if (process.argv.includes('--browser')) {
    await copyFile(noticesPath, 'out/THIRD-PARTY-NOTICES.txt');
    await copyFile('compliance/dependency-inventory.json', 'out/dependency-inventory.json');
  }
  console.log(`Website license coverage verified (${manifest.packages.length} notice entries).`);
}
