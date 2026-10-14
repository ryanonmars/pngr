import type { ReactNode } from 'react';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: { default: 'Pngr Documentation', template: '%s | Pngr Docs' },
  description: 'Install Pngr, monitor your network, and understand local diagnostic history.',
  icons: { icon: '/assets/branding/icon.png' },
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return <html lang="en" className="dark" suppressHydrationWarning><body>{children}</body></html>;
}
