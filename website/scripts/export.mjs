import { copyFile } from 'node:fs/promises';
// Retain the existing buildless landing page and its behavior byte for byte.
for (const file of ['index.html', 'styles.css', 'site.js']) {
  await copyFile(file, `out/${file}`);
}

// Serve the exported search database with a JSON content type on static hosts.
await copyFile('out/api/search', 'out/search-index.json');
