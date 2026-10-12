import { cpSync, existsSync, readdirSync, readFileSync, statSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';

const here = dirname(fileURLToPath(import.meta.url));

// Where `dotnet publish src/DotNetro.WebPlayground -c Release` puts the Blazor/WASM payload.
// Override with DOTNET_PUBLISH_DIR (the directory that contains `_framework`).
const publishDir = resolve(here, process.env.DOTNET_PUBLISH_DIR ?? '../bin/Release/net10.0/publish/wwwroot');
const frameworkDir = join(publishDir, '_framework');

const mimeTypes: Record<string, string> = {
  '.js': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json',
  '.dll': 'application/octet-stream', '.map': 'application/json',
};

// Raw (uncompressed) sizes of the files the runtime loads, used by the startup progress bar.
function frameworkManifest(): string {
  const files: Record<string, number> = {};
  if (existsSync(frameworkDir)) {
    for (const name of readdirSync(frameworkDir)) {
      if (/\.(dll|wasm|js)$/.test(name)) files[name] = statSync(join(frameworkDir, name)).size;
    }
  }
  return JSON.stringify({ files });
}

// Serves (dev) or copies (build) the published .NET payload next to the app, and emits the
// framework manifest. The payload is produced by `dotnet publish`, not by Vite.
function dotnetFramework(): Plugin {
  return {
    name: 'dotnet-framework',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = (req.url ?? '').split('?')[0];
        if (url.endsWith('/framework-manifest.json')) {
          res.setHeader('Content-Type', 'application/json');
          res.end(frameworkManifest());
          return;
        }
        const index = url.indexOf('/_framework/');
        if (index < 0) return next();
        const file = join(frameworkDir, url.slice(index + '/_framework/'.length));
        if (!file.startsWith(frameworkDir) || !existsSync(file) || !statSync(file).isFile()) return next();
        res.setHeader('Content-Type', mimeTypes[extname(file)] ?? 'application/octet-stream');
        res.end(readFileSync(file));
      });
    },
    closeBundle() {
      if (!existsSync(frameworkDir)) {
        console.warn(`dotnet-framework: ${frameworkDir} not found; run \`dotnet publish\` first. The site will not boot.`);
        return;
      }
      const out = resolve(here, 'dist');
      cpSync(frameworkDir, join(out, '_framework'), { recursive: true });
      mkdirSync(out, { recursive: true });
      writeFileSync(join(out, 'framework-manifest.json'), frameworkManifest());
    },
  };
}

export default defineConfig({
  // Relative so the same build works at / (dev) and /dotnetro/ (Pages).
  base: './',
  resolve: {
    alias: { 'monaco-vs': resolve(here, 'node_modules/monaco-editor/esm/vs') },
  },
  worker: { format: 'es' },
  build: { target: 'es2022', chunkSizeWarningLimit: 4000 },
  plugins: [dotnetFramework()],
});
