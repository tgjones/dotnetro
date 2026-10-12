import type { CSharpResult, TargetResult } from './protocol';

// What a panel publishes for its downstream panels. A source panel publishes the Roslyn result
// (including the IL assembly); a disassembly panel publishes the target result (asm + image).
export type PanelResult =
  | { kind: 'csharp'; result: CSharpResult }
  | { kind: 'target'; targetId: string; result: TargetResult };

export interface PanelInfo { id: string; type: string; upstreamId?: string }

type Listener = (id: string, result: PanelResult | undefined) => void;

// Tiny pub/sub: latest result per panel id, plus the panel graph (who feeds whom).
export class Store {
  private readonly results = new Map<string, PanelResult>();
  private readonly panels = new Map<string, PanelInfo>();
  private readonly listeners = new Set<Listener>();
  private readonly panelListeners = new Set<() => void>();
  private counter = 0;

  newId(prefix: string): string {
    let id: string;
    do { id = `${prefix}${++this.counter}`; } while (this.panels.has(id));
    return id;
  }

  /** Call when restoring panels with known ids so newId never collides. */
  reserve(id: string) {
    const n = Number(id.replace(/^\D+/, ''));
    if (Number.isFinite(n)) this.counter = Math.max(this.counter, n);
  }

  registerPanel(info: PanelInfo) { this.panels.set(info.id, info); this.reserve(info.id); this.panelListeners.forEach(l => l()); }
  unregisterPanel(id: string) { this.panels.delete(id); this.publish(id, undefined); this.panelListeners.forEach(l => l()); }
  getPanels(): PanelInfo[] { return [...this.panels.values()]; }
  onPanelsChanged(listener: () => void): () => void { this.panelListeners.add(listener); return () => this.panelListeners.delete(listener); }

  publish(id: string, result: PanelResult | undefined) {
    if (result) this.results.set(id, result); else this.results.delete(id);
    this.listeners.forEach(l => l(id, result));
  }

  get(id: string): PanelResult | undefined { return this.results.get(id); }

  /** Calls back on every publish of any panel. */
  subscribeAll(callback: (id: string, result: PanelResult | undefined) => void): () => void {
    this.listeners.add(callback);
    return () => this.listeners.delete(callback);
  }

  /** Calls back now (if a result exists) and on every later change of `id`. */
  subscribe(id: string, callback: (result: PanelResult | undefined) => void): () => void {
    const listener: Listener = (changed, result) => { if (changed === id) callback(result); };
    this.listeners.add(listener);
    const current = this.results.get(id);
    if (current) callback(current);
    return () => this.listeners.delete(listener);
  }

  /** Calls back for every panel whose upstream is `upstreamId` (e.g. all disassemblies of a source). */
  subscribeDownstream(upstreamId: string, callback: () => void): () => void {
    const listener: Listener = id => { if (this.panels.get(id)?.upstreamId === upstreamId) callback(); };
    this.listeners.add(listener);
    const off = this.onPanelsChanged(callback);
    return () => { this.listeners.delete(listener); off(); };
  }

  downstreamResults(upstreamId: string): PanelResult[] {
    return this.getPanels().filter(p => p.upstreamId === upstreamId).map(p => this.results.get(p.id)).filter((r): r is PanelResult => !!r);
  }
}
