import type { ReactNode } from 'react';
import { DocsProvider } from '../../components/docs-provider';
import { DocsLayout } from 'fumadocs-ui/layouts/docs';
import { source } from '../../lib/source';
import './docs.css';

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <DocsProvider>
      <DocsLayout tree={source.pageTree} themeSwitch={{ enabled: false }}
        nav={{ title: <img src="/assets/branding/wordmark.png" alt="Pngr" width={94} height={32} />, url: '/' }}
        links={[{ text: 'Capabilities', url: '/#capabilities' }, { text: 'Download', url: '/#download' }]}
        sidebar={{ collapsible: false }}>
        {children}
      </DocsLayout>
    </DocsProvider>
  );
}
