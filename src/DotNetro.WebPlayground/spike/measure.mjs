// WP2 spike: boots the published playground under Node and measures cold start and compile
// latency, plus the Brotli size of the published payload.
//
//   dotnet publish src/DotNetro.WebPlayground -c Release -o /tmp/dnpub
//   node src/DotNetro.WebPlayground/spike/measure.mjs /tmp/dnpub [cs-file...]
//
// Node and Chrome share V8 and the same Mono WebAssembly runtime, so latencies are a fair
// proxy for the browser (a Web Worker adds no meaningful cost); sizes are exact.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, basename } from 'node:path';
import { pathToFileURL } from 'node:url';

const [publishDir, ...files] = process.argv.slice(2);
const framework = join(publishDir, 'wwwroot', '_framework');

// --- sizes -------------------------------------------------------------------------------
const groups = { 'runtime (dotnet.*)': 0, 'BCL': 0, 'Roslyn': 0, 'Irie + compiler + playground': 0, 'other': 0 };
const raw = { ...groups };
for (const name of readdirSync(framework)) {
  if (!name.endsWith('.br')) continue;
  const brotli = statSync(join(framework, name)).size;
  const original = statSync(join(framework, name.slice(0, -3))).size;
  const group =
    name.startsWith('dotnet') ? 'runtime (dotnet.*)'
    : name.startsWith('Microsoft.CodeAnalysis') ? 'Roslyn'
    : /^(Irie|DotNetro)/.test(name) ? 'Irie + compiler + playground'
    : /^(System|Microsoft)\./.test(name) ? 'BCL' : 'other';
  groups[group] += brotli;
  raw[group] += original;
}
console.log('Published payload (Brotli / uncompressed):');
let total = 0, totalRaw = 0;
for (const [g, size] of Object.entries(groups)) {
  console.log(`  ${g.padEnd(32)} ${(size / 1024).toFixed(0).padStart(6)} KiB  /  ${(raw[g] / 1024).toFixed(0).padStart(6)} KiB`);
  total += size; totalRaw += raw[g];
}
console.log(`  ${'TOTAL'.padEnd(32)} ${(total / 1024).toFixed(0).padStart(6)} KiB  /  ${(totalRaw / 1024).toFixed(0).padStart(6)} KiB`);

// --- cold start --------------------------------------------------------------------------
const t0 = performance.now();
const { dotnet } = await import(pathToFileURL(join(framework, 'dotnet.js')).href);
const runtime = await dotnet.withDiagnosticTracing(false).create();
const exports = await runtime.getAssemblyExports('DotNetro.WebPlayground.dll');
const playground = exports.DotNetro.WebPlayground.Playground;
const tBoot = performance.now();
console.log(`\nruntime boot: ${(tBoot - t0).toFixed(0)} ms`);
console.log('targets:', playground.GetTargets());

// --- compile -----------------------------------------------------------------------------
function compile(source, optimize = true) {
  const t = performance.now();
  const cs = JSON.parse(playground.CompileCSharp(source, optimize));
  const tCs = performance.now();
  if (!cs.ok) return { stage: 'roslyn', cs, ms: [tCs - t] };
  const dll = Uint8Array.from(Buffer.from(cs.assembly, 'base64'));
  const res = JSON.parse(playground.CompileForTarget(dll, 'bbcmicro', false));
  const tDn = performance.now();
  return { stage: 'done', cs, res, ms: [tCs - t, tDn - tCs] };
}

for (const file of files) {
  const source = readFileSync(file, 'utf8');
  console.log(`\n${basename(file)}`);
  for (let i = 0; i < 3; i++) {
    // Vary the source after the first run so the Roslyn cache does not hide its latency.
    const r = compile(i === 0 ? source : `${source}\n// run ${i}\n`);
    if (r.stage !== 'done' || !r.res.ok) {
      console.log('  FAILED', JSON.stringify(r.stage === 'roslyn' ? r.cs.diagnostics : r.res.diagnostics));
      break;
    }
    console.log(`  run ${i + 1}: roslyn ${r.ms[0].toFixed(0)} ms, dotnetro ${r.ms[1].toFixed(0)} ms` +
      `  (asm ${r.res.asm.length} chars, image ${Buffer.from(r.res.image, 'base64').length} bytes, origin ${r.res.origin.toString(16)})`);
    if (i === 0) {
      writeFileSync(`/tmp/${basename(file, '.cs')}.ssd`, Buffer.from(r.res.image, 'base64'));
      writeFileSync(`/tmp/${basename(file, '.cs')}.dll`, Buffer.from(r.cs.assembly, 'base64'));
    }
  }
}
process.exit(0);
