// The C64 emulator is the VICE libretro core that EmulatorJS builds for the web
// (@emulatorjs/core-vice_x64). The npm package ships it as a 7z archive; this unpacks the
// plain (single-threaded) build once into node_modules/.cache, from where the dev server serves
// it and the production build copies it. Nothing extracted is committed. The wasm contains the
// C64 ROMs, so the notice in THIRD-PARTY-NOTICES.md covers them too.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import SevenZip from '7z-wasm';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');

/** The files of the core the page needs, served under `c64/`. */
export const c64CoreFiles = ['vice_x64_libretro.js', 'vice_x64_libretro.wasm'];

const archive = join(root, 'node_modules/@emulatorjs/core-vice_x64/vice_x64-wasm.data');
const cacheDir = join(root, 'node_modules/.cache/c64-core');

/** Unpacks the core if it is not already, and returns the directory holding `c64CoreFiles`. */
export async function ensureC64Core(): Promise<string> {
  if (c64CoreFiles.every(f => existsSync(join(cacheDir, f)))) return cacheDir;

  const sevenZip = await SevenZip({ print: () => {}, printErr: () => {} });
  sevenZip.FS.writeFile('core.7z', readFileSync(archive));
  sevenZip.callMain(['x', 'core.7z', '-y']);

  mkdirSync(cacheDir, { recursive: true });
  for (const file of c64CoreFiles) writeFileSync(join(cacheDir, file), sevenZip.FS.readFile(file));
  return cacheDir;
}
