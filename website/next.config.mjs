import { createMDX } from 'fumadocs-mdx/next';
import { BrowserLicensePlugin } from './scripts/browser-license-plugin.mjs';

export default createMDX()({
  output: 'export',
  trailingSlash: true,
  images: { unoptimized: true },
  webpack(config, { isServer }) {
    if (!isServer) config.plugins.push(new BrowserLicensePlugin());
    return config;
  },
});
