import type { MetadataRoute } from 'next';
import { source } from '../lib/source';

export const dynamic = 'force-static';
export default function sitemap(): MetadataRoute.Sitemap {
  // Vercel supplies the current production domain; local builds use localhost.
  const origin = process.env.SITE_URL ?? (process.env.VERCEL_PROJECT_PRODUCTION_URL
    ? `https://${process.env.VERCEL_PROJECT_PRODUCTION_URL}` : 'http://localhost:3000');
  return [{ url: `${origin}/` }, ...source.getPages().map(page => ({ url: `${origin}${page.url}/` }))];
}
