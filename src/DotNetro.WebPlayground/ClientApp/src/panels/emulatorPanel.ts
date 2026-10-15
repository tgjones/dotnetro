import type { ComponentContainer } from 'golden-layout';
import { el, emulatorTitle, type AppContext, type PanelState } from './context';
import { BbcMicroMachine } from './bbcMicroMachine';
import { C64Machine } from './c64Machine';
import type { Machine } from './machine';

/**
 * An emulator panel. Consumes the image published by its upstream disassembly panel: every
 * successful compile boots the new image. Pauses while hidden or when the page is hidden.
 * The machine (BBC Micro, C64) comes from the subclass.
 */
abstract class EmulatorPanel {
  /** The `emulatorKind` of the targets whose images this machine runs. */
  protected abstract get emulatorKind(): string;

  private readonly screen = el('canvas', 'bbc-screen');
  private readonly status = el('span', 'status', 'Waiting for a successful compile…');
  private readonly pauseButton = el('button', undefined, 'Pause');
  private readonly machine: Machine;
  private readonly disposers: (() => void)[] = [];
  private image: Uint8Array | undefined;
  private paused = false;
  private visible = true;

  constructor(
    container: ComponentContainer,
    private readonly state: PanelState,
    private readonly ctx: AppContext,
    createMachine: (screen: HTMLCanvasElement) => Machine,
  ) {
    this.machine = createMachine(this.screen);

    const reset = el('button', undefined, 'Reset');
    reset.onclick = () => this.restart();
    this.pauseButton.onclick = () => { this.paused = !this.paused; this.sync(); };
    const bar = el('div', 'toolbar');
    bar.append(reset, this.pauseButton);
    const root = el('div', 'panel');
    const wrap = el('div', 'bbc-screen-wrap');
    wrap.append(this.screen);
    const footer = el('div', 'statusbar');
    footer.append(this.status);
    root.append(bar, wrap, footer);
    container.element.append(root);
    this.machine.bindInput(this.screen);

    const onVisibility = () => this.sync();
    document.addEventListener('visibilitychange', onVisibility);
    this.disposers.push(() => document.removeEventListener('visibilitychange', onVisibility));
    container.on('show', () => { this.visible = true; this.sync(); });
    container.on('hide', () => { this.visible = false; this.sync(); });

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string, upstreamId: state.upstreamId });
    container.setState(state as never);
    // The compiler panel may register after this one when a layout is restored, so retitle on changes.
    const updateTitle = () => container.setTitle(emulatorTitle(ctx.store, state.id, state.upstreamId));
    updateTitle();
    this.disposers.push(ctx.store.onPanelsChanged(updateTitle));
    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, r => this.onResult(r)));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private onResult(r: ReturnType<AppContext['store']['get']>) {
    if (r?.kind !== 'target' || !r.result.ok || !r.result.image) return;
    // An image for another system would boot as garbage here.
    const kind = this.ctx.client.targets.find(t => t.id === r.targetId)?.emulatorKind;
    if (kind !== this.emulatorKind) return;
    this.image = Uint8Array.from(atob(r.result.image), c => c.charCodeAt(0));
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
    this.machine.dispose();
    this.disposers.forEach(d => d());
    this.ctx.store.unregisterPanel(this.state.id);
  }
}

export class BbcMicroEmulatorPanel extends EmulatorPanel {
  protected get emulatorKind() { return 'jsbeeb'; }

  constructor(container: ComponentContainer, state: PanelState, ctx: AppContext) {
    super(container, state, ctx, screen => new BbcMicroMachine(screen));
  }
}

export class C64EmulatorPanel extends EmulatorPanel {
  protected get emulatorKind() { return 'vice-c64'; }

  constructor(container: ComponentContainer, state: PanelState, ctx: AppContext) {
    super(container, state, ctx, screen => new C64Machine(screen));
  }
}
