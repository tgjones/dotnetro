import type { ComponentContainer } from 'golden-layout';
import { el, type AppContext, type PanelState } from './context';

/**
 * Placeholder for the BBC Micro emulator (WP4 replaces it with jsbeeb). It already consumes the
 * real contract: the image bytes and origin published by its upstream disassembly panel.
 */
export class BbcMicroEmulatorPanel {
  private readonly info = el('p');
  private readonly download = el('a', undefined, 'Download MAIN.ssd');
  private readonly disposers: (() => void)[] = [];
  private url: string | undefined;

  constructor(container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    const root = el('div', 'panel placeholder');
    this.download.download = 'MAIN.ssd';
    this.download.hidden = true;
    root.append(el('h3', undefined, 'BBC Micro'), this.info, this.download);
    container.element.append(root);
    this.info.textContent = 'Waiting for a successful compile…';

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string, upstreamId: state.upstreamId });
    container.setState(state as never);
    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, r => this.onResult(r)));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private onResult(r: ReturnType<AppContext['store']['get']>) {
    if (r?.kind !== 'target' || !r.result.ok || !r.result.image) return;
    const bytes = Uint8Array.from(atob(r.result.image), c => c.charCodeAt(0));
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = URL.createObjectURL(new Blob([bytes]));
    this.download.href = this.url;
    this.download.hidden = false;
    this.info.textContent = `Emulator arrives with WP4. Image ready: ${bytes.length} bytes, loads at $${(r.result.origin ?? 0).toString(16).toUpperCase()}.`;
  }

  private dispose() {
    this.disposers.forEach(d => d());
    if (this.url) URL.revokeObjectURL(this.url);
    this.ctx.store.unregisterPanel(this.state.id);
  }
}
