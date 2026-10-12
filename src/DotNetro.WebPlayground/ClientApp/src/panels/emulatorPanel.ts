import type { ComponentContainer } from 'golden-layout';
import { el, type AppContext, type PanelState } from './context';
import { BbcMicroMachine } from './bbcMicroMachine';

/**
 * BBC Micro emulator (jsbeeb). Consumes the image published by its upstream disassembly panel:
 * every successful compile boots the new image. Pauses while hidden or when the page is hidden.
 */
export class BbcMicroEmulatorPanel {
  private readonly screen = el('canvas', 'bbc-screen');
  private readonly status = el('span', 'status', 'Waiting for a successful compile…');
  private readonly pauseButton = el('button', undefined, 'Pause');
  private readonly download = el('a', undefined, 'MAIN.ssd');
  private readonly machine: BbcMicroMachine;
  private readonly disposers: (() => void)[] = [];
  private url: string | undefined;
  private image: Uint8Array | undefined;
  private paused = false;
  private visible = true;

  constructor(container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    this.machine = new BbcMicroMachine(this.screen);
    this.screen.tabIndex = 0;

    const reset = el('button', undefined, 'Reset');
    reset.onclick = () => this.restart();
    this.pauseButton.onclick = () => { this.paused = !this.paused; this.sync(); };
    this.download.download = 'MAIN.ssd';
    this.download.hidden = true;
    this.download.className = 'bbc-download';
    const bar = el('div', 'toolbar');
    bar.append(reset, this.pauseButton, this.download, this.status);
    const root = el('div', 'panel');
    const wrap = el('div', 'bbc-screen-wrap');
    wrap.append(this.screen);
    root.append(bar, wrap);
    container.element.append(root);

    // Keys reach the machine only while the screen has focus.
    this.screen.addEventListener('keydown', e => { e.preventDefault(); if (e.code === 'F12' || e.code === 'Pause') this.machine.breakKey(true); else this.machine.keyDown(e); });
    this.screen.addEventListener('keyup', e => { e.preventDefault(); if (e.code === 'F12' || e.code === 'Pause') this.machine.breakKey(false); else this.machine.keyUp(e); });
    this.screen.addEventListener('blur', () => this.machine.clearKeys());
    this.screen.addEventListener('mousedown', () => this.screen.focus());

    const onVisibility = () => this.sync();
    document.addEventListener('visibilitychange', onVisibility);
    this.disposers.push(() => document.removeEventListener('visibilitychange', onVisibility));
    container.on('show', () => { this.visible = true; this.sync(); });
    container.on('hide', () => { this.visible = false; this.sync(); });

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string, upstreamId: state.upstreamId });
    container.setState(state as never);
    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, r => this.onResult(r)));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private onResult(r: ReturnType<AppContext['store']['get']>) {
    if (r?.kind !== 'target' || !r.result.ok || !r.result.image) return;
    this.image = Uint8Array.from(atob(r.result.image), c => c.charCodeAt(0));
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = URL.createObjectURL(new Blob([this.image as BlobPart]));
    this.download.href = this.url;
    this.download.hidden = false;
    this.restart();
  }

  private restart() {
    if (!this.image) return;
    this.paused = false;
    this.status.textContent = `Running (${this.image.length} byte image)`;
    this.machine.boot(this.image).catch(e => { this.status.textContent = `Emulator error: ${e?.message ?? e}`; });
    this.sync();
  }

  /** Run only when wanted, shown, and the page is visible. */
  private sync() {
    this.pauseButton.textContent = this.paused ? 'Run' : 'Pause';
    const shouldRun = !this.paused && this.visible && document.visibilityState === 'visible';
    if (shouldRun && this.image) this.machine.run();
    else if (!shouldRun && this.machine.isRunning) this.machine.pause();
  }

  private dispose() {
    this.machine.pause();
    this.disposers.forEach(d => d());
    if (this.url) URL.revokeObjectURL(this.url);
    this.ctx.store.unregisterPanel(this.state.id);
  }
}
