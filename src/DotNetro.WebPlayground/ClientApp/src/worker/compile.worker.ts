// Hosts the .NET (WebAssembly) runtime off the main thread. Requests are queued and drained from
// a timer so that, when several arrive while a synchronous compile is running, only the newest
// per (kind, key) is executed: stale compiles are dropped before they start.
import type { CSharpResult, Stage, TargetInfo, TargetResult, WorkerRequest, WorkerResponse } from '../protocol';

type Playground = {
  GetTargets(): string;
  CompileCSharp(source: string, optimize: boolean): string;
  CompileForTarget(assembly: Uint8Array, targetId: string, snapshots: boolean): string;
};

const post = (message: WorkerResponse) => self.postMessage(message);

let playground: Playground | undefined;
const queue: Exclude<WorkerRequest, { type: 'init' }>[] = [];
let draining = false;

function onRequest(data: Exclude<WorkerRequest, { type: 'init' }>) {
  queue.push(data);
  if (!draining) {
    draining = true;
    setTimeout(drain, 0);
  }
}

// Requests that arrive while the runtime boots are queued (drain waits for `playground`). The
// handlers are deliberately not installed on `self.onmessage`: the .NET runtime owns that slot in
// a worker, and booting hangs if it is taken.
const initListener = ({ data }: MessageEvent<WorkerRequest>) => {
  if (data.type === 'init') {
    self.removeEventListener('message', initListener);
    self.addEventListener('message', ({ data }: MessageEvent<WorkerRequest>) => { if (data.type !== 'init') onRequest(data); });
    void init(data.baseUrl).catch(error => post({ type: 'init-error', message: String(error?.message ?? error) }));
  }
};
self.addEventListener('message', initListener);

function drain() {
  draining = false;
  if (!playground) {
    // Not booted yet: keep the queue, run it when init finishes.
    return;
  }
  while (queue.length > 0) {
    const request = queue.shift()!;
    if (queue.some(q => q.type === request.type && q.key === request.key)) continue; // superseded
    try {
      run(request);
    } catch (error) {
      const message = `Internal error: ${(error as Error)?.message ?? error}`;
      const result = { ok: false, diagnostics: [{ severity: 'error' as const, message, source: 'dotnetro' as const }] };
      if (request.type === 'csharp') post({ type: 'csharp-result', key: request.key, gen: request.gen, result });
      else post({ type: 'target-result', key: request.key, gen: request.gen, result });
    }
  }
}

function run(request: Exclude<WorkerRequest, { type: 'init' }>) {
  if (request.type === 'csharp') {
    const result = JSON.parse(playground!.CompileCSharp(request.source, request.optimize)) as CSharpResult;
    post({ type: 'csharp-result', key: request.key, gen: request.gen, result });
  } else {
    const bytes = Uint8Array.from(atob(request.assembly), c => c.charCodeAt(0));
    const result = JSON.parse(playground!.CompileForTarget(bytes, request.targetId, request.snapshots)) as TargetResult;
    post({ type: 'target-result', key: request.key, gen: request.gen, result });
  }
}

async function init(baseUrl: string) {
  const frameworkUrl = new URL('_framework/', baseUrl);

  // Sizes of everything the runtime will fetch, so progress is a real fraction. The manifest is
  // emitted by the build; without it the bar falls back to indeterminate (total = 0).
  let sizes: Record<string, number> = {};
  try {
    const response = await fetch(new URL('framework-manifest.json', baseUrl));
    if (response.ok) sizes = (await response.json()).files;
  } catch { /* indeterminate progress */ }
  const total = Object.values(sizes).reduce((a, b) => a + b, 0);

  let loaded = 0;
  const report = (stage: Stage) => post({ type: 'progress', stage, loaded, total });
  const stageOf = (type: string): Stage => (type === 'assembly' || type === 'pdb' ? 'compiler' : 'runtime');

  report('runtime');

  const { dotnet } = await import(/* @vite-ignore */ new URL('dotnet.js', frameworkUrl).href);

  const runtime = await dotnet
    .withResourceLoader((type: string, _name: string, defaultUri: string) => {
      if (type === 'dotnetjs') return defaultUri; // already imported above
      if (type === 'pdb') return null;
      return countedFetch(defaultUri, stageOf(type));
    })
    .create();

  async function countedFetch(uri: string, stage: Stage): Promise<Response> {
    const response = await fetch(uri);
    if (!response.ok || !response.body) throw new Error(`Failed to download ${uri}: ${response.status}`);
    const reader = response.body.getReader();
    const stream = new ReadableStream<Uint8Array>({
      async pull(controller) {
        const { done, value } = await reader.read();
        if (done) { controller.close(); return; }
        loaded += value.byteLength;
        report(stage);
        controller.enqueue(value);
      },
      cancel: reason => reader.cancel(reason),
    });
    return new Response(stream, { status: response.status, headers: response.headers });
  }

  loaded = total; // everything downloaded
  report('roslyn');
  const exports = await runtime.getAssemblyExports('DotNetro.WebPlayground.dll');
  playground = exports.DotNetro.WebPlayground.Playground as Playground;

  const targets = JSON.parse(playground.GetTargets()) as TargetInfo[];
  post({ type: 'ready', targets });
  if (queue.length > 0) setTimeout(drain, 0);
}
