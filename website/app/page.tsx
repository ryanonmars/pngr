import { readFile } from 'node:fs/promises';
import Script from 'next/script';

export const metadata = {
  title: { absolute: 'Pngr — See your network clearly.' },
  description: 'Find latency, jitter, and response loss with Pngr. Live network diagnostics and local session history for macOS and Windows.',
};

export default async function Home() {
  const html = await readFile('index.html', 'utf8');
  const body = html.match(/<body>([\s\S]*)<\/body>/)?.[1] ?? '';
  return <><link rel="stylesheet" href="/styles.css" /><div dangerouslySetInnerHTML={{ __html: body }} /><Script src="/site.js" strategy="afterInteractive" /></>;
}
