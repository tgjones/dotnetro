import type { ComponentContainer } from 'golden-layout';
import { ASM_LANGUAGE, monaco, type Monaco } from '../monaco';
import { monacoTheme } from '../theme';
import { EMULATOR_PANELS, el, type AppContext, type PanelState } from './context';

/** Read-only assembly view for one target. Feeds emulator panels with the image. */
export class DisassemblyPanel {
  private readonly editor: Monaco.editor.IStandaloneCodeEditor;
  private readonly status = el('span', 'status');
  private readonly targetSelect = el('select');
  private readonly disposers: (() => void)[] = [];

  constructor(private readonly container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    const root = el('div', 'panel');
    const toolbar = el('div', 'toolbar');
    const addEmulator = el('button', undefined, '+ Emulator');
    addEmulator.title = 'Add an emulator panel that runs this output';
    toolbar.append(el('label', undefined, 'Target '), this.targetSelect, addEmulator, this.status);
    const host = el('div', 'editor-host');
    root.append(toolbar, host);
    container.element.append(root);

    this.editor = monaco.editor.create(host, {
      value: '',
      language: ASM_LANGUAGE,
      theme: monacoTheme(),
      readOnly: true,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 13,
    });

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string, upstreamId: state.upstreamId });
    container.setState(state as never);

    void ctx.client.ready.then(targets => {
      for (const t of targets) {
        const option = el('option', undefined, t.displayName);
        option.value = t.id;
        this.targetSelect.append(option);
      }
      this.targetSelect.value = this.targetId;
      this.updateTitle();
    }).catch(() => {});

    this.targetSelect.onchange = () => {
      state.options.targetId = this.targetSelect.value;
      container.setState(state as never);
      this.updateTitle();
      this.recompile();
    };

    addEmulator.onclick = () => {
      const kind = ctx.client.targets.find(t => t.id === this.targetId)?.emulatorKind ?? 'jsbeeb';
      const type = EMULATOR_PANELS[kind];
      if (type) ctx.addPanel(type, { id: ctx.store.newId('e'), upstreamId: state.id, options: {} }, 'Emulator');
    };

    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, () => this.recompile()));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private get targetId() { return (this.state.options.targetId as string | undefined) ?? 'bbcmicro'; }

  private updateTitle() {
    const name = this.ctx.client.targets.find(t => t.id === this.targetId)?.displayName ?? this.targetId;
    this.container.setTitle(`${name} asm`);
  }

  private async recompile() {
    const upstream = this.state.upstreamId ? this.ctx.store.get(this.state.upstreamId) : undefined;
    if (upstream?.kind !== 'csharp') return;
    if (!upstream.result.ok || !upstream.result.assembly) {
      // Roslyn errors: keep the last good output, say why it is stale.
      this.status.textContent = 'C# has errors';
      return;
    }

    this.status.textContent = 'Compiling…';
    const targetId = this.targetId;
    const result = await this.ctx.client.compileForTarget(this.state.id, upstream.result.assembly, targetId);
    if (!result) return; // superseded
    this.ctx.store.publish(this.state.id, { kind: 'target', targetId, result });

    if (result.ok) {
      this.editor.setValue(result.asm ?? '');
      const size = result.image ? atob(result.image).length : 0;
      this.status.textContent = `OK (image ${size} bytes @ $${(result.origin ?? 0).toString(16).toUpperCase()})`;
    } else {
      this.status.textContent = result.diagnostics.map(d => d.message).join('; ') || 'Compilation failed';
    }
  }

  private dispose() {
    this.disposers.forEach(d => d());
    this.ctx.store.unregisterPanel(this.state.id);
    this.editor.dispose();
  }
}
