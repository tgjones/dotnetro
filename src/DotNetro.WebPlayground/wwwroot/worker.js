// Hosts the .NET runtime off the main thread and forwards compile requests to the
// [JSExport] surface of DotNetro.WebPlayground.Playground.
import { dotnet } from './_framework/dotnet.js';

const started = performance.now();
const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports('DotNetro.WebPlayground.dll');
const playground = exports.DotNetro.WebPlayground.Playground;

function base64ToBytes(base64) {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

self.onmessage = ({ data: { id, source, optimize, targetId } }) => {
  const t0 = performance.now();
  const csharp = JSON.parse(playground.CompileCSharp(source, optimize));
  const t1 = performance.now();
  let target = null;
  if (csharp.ok) {
    target = JSON.parse(playground.CompileForTarget(base64ToBytes(csharp.assembly), targetId, false));
  }
  const t2 = performance.now();
  self.postMessage({ type: 'result', id, csharp, target, roslynMs: t1 - t0, dotnetroMs: t2 - t1 });
};

self.postMessage({ type: 'ready', targets: JSON.parse(playground.GetTargets()), bootMs: performance.now() - started });
