'use client';

import Link from 'next/link';
import { RootProvider } from 'fumadocs-ui/provider/next';
import type { ComponentProps, ReactNode } from 'react';

function SiteLink({ href, prefetch, ...props }: ComponentProps<'a'> & { prefetch?: boolean }) {
  // Leave the docs with a full navigation so the original landing-page CSS and
  // JavaScript load independently of the documentation application.
  if (href?.startsWith('/docs')) return <Link href={href} prefetch={prefetch} {...props} />;
  return <a href={href} {...props} />;
}

export function DocsProvider({ children }: { children: ReactNode }) {
  return <RootProvider components={{ Link: SiteLink }} theme={{ forcedTheme: 'dark', hotKey: false }}
    search={{ options: { type: 'static', api: '/search-index.json' } }}>{children}</RootProvider>;
}
