import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { load } from 'cheerio';

async function files(dir, extension) {
  const result = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) result.push(...await files(path, extension));
    else if (path.endsWith(extension)) result.push(path);
  }
  return result;
}
const articles = await files('content/docs', '.mdx');
const routes = articles.map(file => '/docs' + '/' + file.replace(/^content\/docs\//, '').replace(/\.mdx$/, '').replace(/(^|\/)index$/, '$1').replace(/\/$/, '')).map(route => route.replace(/\/$/, ''));

for (const route of routes) test(`export, metadata and internal links: ${route}`, async () => {
  const $ = load(await readFile(`out${route}/index.html`, 'utf8'));
  assert.equal($('h1').length, 1);
  assert.ok($('title').text().includes('Pngr'));
  assert.ok($('meta[name="description"]').attr('content'));
  for (const node of $('a[href^="/"], img[src^="/"]').toArray()) {
    const href = $(node).attr('href') ?? $(node).attr('src');
    const path = new URL(href, 'http://localhost').pathname;
    const target = join('out', decodeURIComponent(path));
    const info = await stat(target).catch(() => null);
    assert.ok(info, `${route}: missing ${href}`);
    if (info.isDirectory()) await stat(join(target, 'index.html'));
    const fragment = new URL(href, 'http://localhost').hash.slice(1);
    if (fragment && path.startsWith('/docs')) {
      const dest = load(await readFile(info.isDirectory() ? join(target, 'index.html') : target, 'utf8'));
      assert.ok(dest('[id]').toArray().some(el => dest(el).attr('id') === decodeURIComponent(fragment)), `${route}: missing anchor ${href}`);
    }
  }
});

test('landing page and scripts preserved in export', async () => {
  for (const name of ['index.html', 'styles.css', 'site.js']) assert.equal(await readFile(`out/${name}`, 'utf8'), await readFile(name, 'utf8'));
  assert.match(await readFile('out/index.html', 'utf8'), /href="\/docs\/">Documentation/);
});

test('sitemap contains all docs and search includes article content', async () => {
  const sitemap = await readFile('out/sitemap.xml', 'utf8');
  for (const route of routes) assert.ok(sitemap.includes(`${route}/`), `sitemap missing ${route}`);
  const index = await readFile('out/search-index.json', 'utf8');
  JSON.parse(index);
  for (const route of routes) assert.ok(index.includes(route), `search missing ${route}`);
  assert.ok(index.includes('Transmission'));
  assert.ok(index.includes('forwarding'));
});
