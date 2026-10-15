import { mkdirSync, writeFileSync } from 'node:fs';
import { relative, resolve } from 'node:path';

// Observe emitted client chunks, including Webpack's concatenated submodules.
// Server/build dependencies are inventoried separately, not labelled as browser code.
export class BrowserLicensePlugin {
  apply(compiler) {
    compiler.hooks.afterEmit.tap('PngrBrowserLicenses', compilation => {
      const resources = new Set();
      function visit(module) {
        if (module.resource?.includes('/node_modules/')) {
          resources.add(relative(process.cwd(), module.resource.split('?')[0]));
        }
        if (module.modules) for (const child of module.modules) visit(child);
      }
      for (const chunk of compilation.chunks) {
        for (const module of compilation.chunkGraph.getChunkModulesIterable(chunk)) visit(module);
      }
      mkdirSync(resolve('.next'), { recursive: true });
      writeFileSync(resolve('.next/browser-license-inputs.json'), JSON.stringify([...resources].sort(), null, 2) + '\n');
    });
  }
}
