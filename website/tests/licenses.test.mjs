import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdir, mkdtemp, cp, symlink, writeFile, rm } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

test('website project license matches the owner-approved repository license', async () => {
  assert.equal(await readFile('compliance/PROJECT-LICENSE.txt', 'utf8'), await readFile('../LICENSE', 'utf8'));
});

test('actual static distribution contains reviewed notices and inventory', async () => {
  assert.equal(await readFile('out/LICENSE', 'utf8'), await readFile('compliance/PROJECT-LICENSE.txt', 'utf8'));
  const manifest = JSON.parse(await readFile('compliance/browser-dependencies.json', 'utf8'));
  const notice = await readFile('out/THIRD-PARTY-NOTICES.txt', 'utf8');
  assert.equal(notice, await readFile('compliance/THIRD-PARTY-NOTICES.txt', 'utf8'));
  assert.equal(createHash('sha256').update(notice).digest('hex'), manifest.noticesSha256);
  assert.deepEqual(JSON.parse(await readFile('out/dependency-inventory.json', 'utf8')),
    JSON.parse(await readFile('compliance/dependency-inventory.json', 'utf8')));
  for (const name of ['fumadocs-core', 'fumadocs-ui', 'react', 'next', 'lucide-react', 'zbsearch', 'tailwindcss']) {
    assert.ok(manifest.packages.some(pkg => pkg.name === name), `missing ${name}`);
  }
  assert.match(notice, /Copyright/);
  assert.match(await readFile('out/index.html', 'utf8'), /href="\/THIRD-PARTY-NOTICES.txt"/);
  assert.match(await readFile('out/docs/index.html', 'utf8'), /href="\/THIRD-PARTY-NOTICES.txt"/);
});

test('all emitted browser module owners have reviewed versions and upstream texts', () => {
  execFileSync(process.execPath, ['scripts/licenses.mjs', '--browser'], { stdio: 'pipe' });
});

for (const change of ['lock', 'notices', 'new-browser-module']) test(`license guard rejects ${change} drift`, async () => {
  const dir = await mkdtemp(join(tmpdir(), 'pngr-web-license-'));
  try {
    await cp('compliance', join(dir, 'compliance'), { recursive: true });
    await cp('package-lock.json', join(dir, 'package-lock.json'));
    await mkdir(join(dir, '.next'));
    await cp('.next/browser-license-inputs.json', join(dir, '.next/browser-license-inputs.json'));
    await symlink(resolve('node_modules'), join(dir, 'node_modules'), 'dir');
    if (change === 'lock') await writeFile(join(dir, 'package-lock.json'), await readFile('package-lock.json', 'utf8') + '\n');
    if (change === 'notices') await writeFile(join(dir, 'compliance/THIRD-PARTY-NOTICES.txt'), 'incomplete');
    if (change === 'new-browser-module') {
      const inputs = JSON.parse(await readFile('.next/browser-license-inputs.json', 'utf8'));
      inputs.push('node_modules/fumadocs-mdx/dist/index.js');
      await writeFile(join(dir, '.next/browser-license-inputs.json'), JSON.stringify(inputs));
    }
    assert.throws(() => execFileSync(process.execPath, [resolve('scripts/licenses.mjs'), '--browser'], { cwd: dir, stdio: 'pipe' }),
      error => /review|match reviewed|Unreviewed/.test(error.stderr.toString()));
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
