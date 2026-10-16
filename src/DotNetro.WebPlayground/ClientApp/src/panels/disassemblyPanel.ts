import type { ComponentContainer } from 'golden-layout';
import { ASM_LANGUAGE, monaco, type Monaco } from '../monaco';
import { monacoTheme } from '../theme';
import { ADD_PANEL_ITEMS, OPT_PIPELINE, disassemblyTitle, el, summarize, type AppContext, type PanelState } from './context';

/** Read-only assembly view for one target. Feeds emulator panels with the image. */
export class DisassemblyPanel {
  private readonly editor: Monaco.editor.IStandaloneCodeEditor;
  private readonly status = el('span', 'status');
  private readonly targetSelect = el('select');
  private readonly download = el('a', 'download');
  private url: string | undefined;
  private readonly disposers: (() => void)[] = [];

  constructor(private readonly container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    const root = el('div', 'panel');
    const toolbar = el('div', 'toolbar');
    const add = el('button', undefined, '+');
    add.title = 'Add a panel that consumes this output';
    const addWrap = el('span', 'menu-anchor');
    addWrap.append(add);
    toolbar.append(el('label', undefined, 'Target '), this.targetSelect, addWrap);
    const host = el('div', 'editor-host');
    this.download.hidden = true;
    const footer = el('div', 'statusbar');
    footer.append(this.status, this.download);
    root.append(toolbar, host, footer);
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
    this.updateTitle();
    this.status.onclick = () => { if (this.status.classList.contains('link') && state.upstreamId) ctx.showProblems(state.upstreamId); };

    void ctx.client.ready.then(targets => {
      for (const t of targets) {
        const option = el('option', undefined, t.displayName);
        option.value = t.id;
        this.targetSelect.append(option);
      }
      if (!targets.some(t => t.id === this.targetId) && targets.length) {
        // Old link naming a removed target: fall back to the first one.
        state.options.targetId = targets[0].id;
        container.setState(state as never);
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

    add.onclick = e => { e.stopPropagation(); this.menuClose ? this.menuClose() : this.openMenu(addWrap); };
    this.disposers.push(() => this.menuClose?.());

    // A new Opt Pipeline viewer has nothing to show until the next compile, so recompile with snapshots.
    this.disposers.push(ctx.store.onPanelsChanged(() => {
      if (this.wantsSnapshots() && !this.requestedSnapshots) void this.recompile();
    }));

    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, () => this.recompile()));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private menuClose: (() => void) | undefined;
  private requestedSnapshots = false;

  private wantsSnapshots() {
    return this.ctx.store.getPanels().some(p => p.type === OPT_PIPELINE && p.upstreamId === this.state.id);
  }

  private openMenu(anchor: HTMLElement) {
    const kind = this.ctx.client.targets.find(t => t.id === this.targetId)?.emulatorKind ?? 'jsbeeb';
    const menu = el('div', 'menu');
    for (const item of ADD_PANEL_ITEMS) {
      const type = item.panelType(kind);
      const button = el('button', 'menu-item', item.label);
      button.disabled = !type;
      button.onclick = () => {
        this.menuClose?.();
        if (!type) return;
        const id = this.ctx.store.newId(item.idPrefix);
        this.ctx.addPanel(type, { id, upstreamId: this.state.id, options: {} }, item.title(this.ctx.store, id, this.state.id));
      };
      menu.append(button);
    }
    anchor.append(menu);
    const onDown = (e: MouseEvent) => { if (!menu.contains(e.target as Node)) close(); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close(); };
    const close = () => {
      menu.remove();
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
      this.menuClose = undefined;
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    this.menuClose = close;
  }

  private get targetId() { return (this.state.options.targetId as string | undefined) ?? 'bbcmicro'; }

  private updateTitle() {
    this.container.setTitle(disassemblyTitle(this.state.id, this.state.upstreamId));
  }

  private async recompile() {
    const upstream = this.state.upstreamId ? this.ctx.store.get(this.state.upstreamId) : undefined;
    if (upstream?.kind !== 'csharp') return;
    if (!upstream.result.ok || !upstream.result.assembly) {
      // Roslyn errors: keep the last good output, say why it is stale.
      this.status.textContent = 'C# has errors';
      this.status.classList.add('link', 'error');
      return;
    }

    this.status.textContent = 'Compiling…';
    this.status.classList.remove('link', 'error');
    const targetId = this.targetId;
    const snapshots = this.requestedSnapshots = this.wantsSnapshots();
    const result = await this.ctx.client.compileForTarget(this.state.id, upstream.result.assembly, targetId, snapshots);
    if (!result) return; // superseded
    this.ctx.store.publish(this.state.id, { kind: 'target', targetId, result });

    if (result.ok) {
      this.editor.setValue(result.asm ?? '');
      const size = result.image ? atob(result.image).length : 0;
      this.setImage(result.image);
      this.status.textContent = `OK (image ${size} bytes @ $${(result.origin ?? 0).toString(16).toUpperCase()})`;
    } else {
      const summary = summarize(result.diagnostics);
      this.status.textContent = `Compilation failed${summary ? `: ${summary}` : ''}`;
      this.status.classList.add('link', 'error');
    }
  }

  private setImage(base64: string | undefined) {
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = base64 ? URL.createObjectURL(new Blob([Uint8Array.from(atob(base64), c => c.charCodeAt(0))])) : undefined;
    this.download.hidden = !this.url;
    if (this.url) {
      const fileName = this.ctx.client.targets.find(t => t.id === this.targetId)?.imageFileName ?? 'image';
      this.download.href = this.url;
      this.download.download = fileName;
      this.download.textContent = fileName;
    }
  }

  private dispose() {
    if (this.url) URL.revokeObjectURL(this.url);
    this.disposers.forEach(d => d());
    this.ctx.store.unregisterPanel(this.state.id);
    this.editor.dispose();
  }
}
