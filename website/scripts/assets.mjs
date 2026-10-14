import { cp, mkdir, copyFile } from 'node:fs/promises';
await mkdir('public', { recursive: true });
await cp('assets', 'public/assets', { recursive: true });
for (const file of ['styles.css', 'site.js']) await copyFile(file, `public/${file}`);
