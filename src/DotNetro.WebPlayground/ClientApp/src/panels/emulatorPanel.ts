import type { ComponentContainer } from 'golden-layout';
import { el, emulatorTitle, type AppContext, type PanelState } from './context';
import { BbcMicroMachine } from './bbcMicroMachine';
import { C64Machine } from './c64Machine';
import type { Machine } from './machine';

const MACHINES: Record<string, (screen: HTMLCanvasElement) => Machine> = {
  jsbeeb: screen => new BbcMicroMachine(screen),
  'vice-c64': screen => new C64Machine(screen),
};

/**
 * An emulator panel. Consumes the image published by its upstream disassembly panel: every
 * successful compile boots the new image. Pauses while hidden or when the page is hidden.
 * The machine follows the upstream target: when the disassembly panel switches to a target with
 * another `emulatorKind`, the old machine is torn down and one for the new system takes its place.
 */
export class EmulatorPanel {
  private screen!: HTMLCanvasElement;
  private readonly wrap = el('div', 'bbc-screen-wrap');
  private readonly status = el('span', 'status', 'Waiting for a successful compile…');
  private readonly pauseButton = el('button', undefined, 'Pause');
  private machine: Machine | undefined;
  private machineKind: string | undefined;
  private readonly disposers: (() => void)[] = [];
  private image: Uint8Array | undefined;
  private targetName = '';
  private paused = false;
  private visible = true;

  constructor(
    container: ComponentContainer,
    private readonly state: PanelState,
    private readonly ctx: AppContext,
  ) {
    const reset = el('button', undefined, 'Reset');
    reset.onclick = () => this.restart();
    this.pauseButton.onclick = () => { this.paused = !this.paused; this.sync(); };
    const bar = el('div', 'toolbar');
    bar.append(reset, this.pauseButton);
    const root = el('div', 'panel');
    const footer = el('div', 'statusbar');
    footer.append(this.status);
    root.append(bar, this.wrap, footer);
    container.element.append(root);

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
    const target = this.ctx.client.targets.find(t => t.id === r.targetId);
    const createMachine = target && MACHINES[target.emulatorKind];
    if (!target || !createMachine) return;
    this.useMachine(target.emulatorKind, createMachine);
    this.targetName = target.displayName;
    this.image = Uint8Array.from(atob(r.result.image), c => c.charCodeAt(0));
    this.restart();
  }

  /** Keeps the machine if it already runs this kind of system; otherwise swaps in a fresh one on a fresh canvas. */
  private useMachine(kind: string, create: (screen: HTMLCanvasElement) => Machine) {
    if (this.machine && this.machineKind === kind) return;
    this.machine?.dispose();
    this.screen?.remove();
    this.screen = el('canvas', 'bbc-screen');
    this.wrap.replaceChildren(this.screen);
    this.machine = create(this.screen);
    this.machine.bindInput(this.screen);
    this.machineKind = kind;
  }

  private restart() {
    if (!this.image || !this.machine) return;
    this.paused = false;
    this.status.textContent = `${this.targetName} running (${this.image.length} byte image)`;
    this.machine.boot(this.image).catch(e => { this.status.textContent = `Emulator error: ${e?.message ?? e}`; });
    this.sync();
  }

  /** Run only when wanted, shown, and the page is visible. */
  private sync() {
    this.pauseButton.textContent = this.paused ? 'Run' : 'Pause';
    const shouldRun = !this.paused && this.visible && document.visibilityState === 'visible';
    if (!this.machine) return;
    if (shouldRun && this.image) this.machine.run();
    else if (!shouldRun && this.machine.isRunning) this.machine.pause();
  }

  private dispose() {
    this.machine?.dispose();
    this.disposers.forEach(d => d());
    this.ctx.store.unregisterPanel(this.state.id);
  }
}
