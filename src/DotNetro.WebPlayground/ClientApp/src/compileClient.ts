import type { CSharpResult, Stage, TargetInfo, TargetResult, WorkerResponse } from './protocol';

export interface Progress { stage: Stage; loaded: number; total: number }

// Talks to the compile worker. Every request carries a generation number per (kind, key); a
// response is delivered only if it is still the newest for its key, so callers never see stale
// results (the worker also drops superseded requests that have not started yet).
export class CompileClient {
  private readonly worker = new Worker(new URL('./worker/compile.worker.ts', import.meta.url), { type: 'module' });
  private readonly generations = new Map<string, number>();
  private readonly pending = new Map<string, (result: never) => void>();
  private nextGen = 1;

  targets: TargetInfo[] = [];
  onProgress: (progress: Progress) => void = () => {};

  readonly ready: Promise<TargetInfo[]>;

  constructor() {
    let resolveReady!: (targets: TargetInfo[]) => void;
    let rejectReady!: (error: Error) => void;
    this.ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
    this.ready.catch(() => {}); // surfaced through the overlay, not as an unhandled rejection

    this.worker.onmessage = ({ data }: MessageEvent<WorkerResponse>) => {
      switch (data.type) {
        case 'progress': this.onProgress(data); break;
        case 'ready': this.targets = data.targets; resolveReady(data.targets); break;
        case 'init-error': rejectReady(new Error(data.message)); break;
        case 'csharp-result': this.deliver('csharp', data.key, data.gen, data.result); break;
        case 'target-result': this.deliver('target', data.key, data.gen, data.result); break;
      }
    };
    this.worker.onerror = e => rejectReady(new Error(e.message || 'The compile worker failed to start.'));
    this.worker.postMessage({ type: 'init', baseUrl: new URL('./', document.baseURI).href });
  }

  /** Resolves with null when a newer request for the same key replaced this one. */
  compileCSharp(key: string, source: string, optimize: boolean): Promise<CSharpResult | null> {
    const gen = this.begin('csharp', key);
    const result = this.await<CSharpResult>('csharp', key, gen);
    this.worker.postMessage({ type: 'csharp', key, gen, source, optimize });
    return result;
  }

  compileForTarget(key: string, assembly: string, targetId: string, snapshots = false): Promise<TargetResult | null> {
    const gen = this.begin('target', key);
    const result = this.await<TargetResult>('target', key, gen);
    this.worker.postMessage({ type: 'target', key, gen, assembly, targetId, snapshots });
    return result;
  }

  private begin(kind: string, key: string): number {
    const gen = this.nextGen++;
    const id = `${kind}:${key}`;
    // Resolve the superseded request's promise with null.
    const stale = this.generations.get(id);
    if (stale !== undefined) (this.pending.get(`${id}:${stale}`) as ((r: null) => void) | undefined)?.(null);
    if (stale !== undefined) this.pending.delete(`${id}:${stale}`);
    this.generations.set(id, gen);
    return gen;
  }

  private await<T>(kind: string, key: string, gen: number): Promise<T | null> {
    return new Promise(resolve => this.pending.set(`${kind}:${key}:${gen}`, resolve as (r: never) => void));
  }

  private deliver(kind: string, key: string, gen: number, result: unknown) {
    const id = `${kind}:${key}:${gen}`;
    this.pending.get(id)?.(result as never);
    this.pending.delete(id);
  }
}
