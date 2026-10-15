import type { ComponentContainer } from 'golden-layout';
import { MIR_LANGUAGE, monaco, type Monaco } from '../monaco';
import { monacoTheme } from '../theme';
import { el, pipelineTitle, type AppContext, type PanelState } from './context';
import { buildRows, type PipelineRow } from './pipelineRows';

/**
 * Opt Pipeline viewer: every Irie pass on the left, and on the right the module's MIR after the
 * selected pass as a diff against the MIR after the previous pass. Consumes the pass snapshots
 * published by its upstream disassembly panel.
 */
export class OptPipelinePanel {
  private readonly list = el('div', 'pipeline-list');
  private readonly status = el('span', 'status', 'Waiting for a successful compile…');
  private readonly hideUnchanged: HTMLInputElement;
  private readonly sideBySide: HTMLInputElement;
  private readonly showFull: HTMLInputElement;
  private readonly search = el('input');
  private readonly editor: Monaco.editor.IStandaloneDiffEditor;
  private readonly original: Monaco.editor.ITextModel;
  private readonly modified: Monaco.editor.ITextModel;
  private readonly disposers: (() => void)[] = [];
  private rows: PipelineRow[] = [];
  private selected = -1;

  constructor(container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    const options = state.options;
    const toggle = (label: string, checked: boolean, key: string) => {
      const input = el('input');
      input.type = 'checkbox';
      input.checked = checked;
      input.onchange = () => { options[key] = input.checked; container.setState(state as never); this.applyOptions(); this.renderList(); };
      const wrap = el('label');
      wrap.append(input, ` ${label}`);
      return { input, wrap };
    };
    const hide = toggle('Hide unchanged', options.hideUnchanged !== false, 'hideUnchanged');
    const side = toggle('Side by side', options.sideBySide === true, 'sideBySide');
    const full = toggle('Show full module', options.showFull === true, 'showFull');
    this.hideUnchanged = hide.input;
    this.sideBySide = side.input;
    this.showFull = full.input;
    this.search.type = 'search';
    this.search.placeholder = 'Filter passes…';
    this.search.oninput = () => this.renderList();

    const bar = el('div', 'toolbar');
    bar.append(hide.wrap, side.wrap, full.wrap, this.search);
    const body = el('div', 'pipeline-body');
    const host = el('div', 'editor-host');
    body.append(this.list, host);
    const footer = el('div', 'statusbar');
    footer.append(this.status);
    const root = el('div', 'panel');
    root.append(bar, body, footer);
    container.element.append(root);

    this.list.tabIndex = 0;
    this.list.onkeydown = e => {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      e.preventDefault();
      const visible = this.visibleRows();
      const at = visible.findIndex(r => r.index === this.selected);
      const next = visible[Math.max(0, Math.min(visible.length - 1, at + (e.key === 'ArrowDown' ? 1 : -1)))];
      if (next) this.select(next.index);
    };

    this.original = monaco.editor.createModel('', MIR_LANGUAGE);
    this.modified = monaco.editor.createModel('', MIR_LANGUAGE);
    this.editor = monaco.editor.createDiffEditor(host, {
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      theme: monacoTheme(),
      renderSideBySide: this.sideBySide.checked,
      hideUnchangedRegions: { enabled: !this.showFull.checked },
    });
    this.editor.setModel({ original: this.original, modified: this.modified });

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string, upstreamId: state.upstreamId, focus: () => this.list.focus() });
    container.setState(state as never);
    // The compiler panel may register after this one when a layout is restored, so retitle on changes.
    const updateTitle = () => container.setTitle(pipelineTitle(ctx.store, state.id, state.upstreamId));
    updateTitle();
    this.disposers.push(ctx.store.onPanelsChanged(updateTitle));
    if (state.upstreamId) this.disposers.push(ctx.store.subscribe(state.upstreamId, r => this.onResult(r)));
    container.on('beforeComponentRelease', () => this.dispose());
  }

  private onResult(r: ReturnType<AppContext['store']['get']>) {
    if (r?.kind !== 'target') return;
    const snapshots = r.result.passSnapshots;
    if (!snapshots?.length) {
      // Failed compile, or one without snapshots: keep the last good rows.
      if (!r.result.ok) this.status.textContent = this.rows.length ? `${this.summary()} (last successful compile)` : 'Compilation failed';
      return;
    }
    const previous = this.rows[this.selected];
    const wanted = previous ? this.keyOf(this.rows, previous) : { name: this.state.options.pass as string | undefined, ordinal: (this.state.options.passOrdinal as number | undefined) ?? 0 };
    this.rows = buildRows(snapshots);
    const match = wanted.name === undefined ? undefined : this.rows.find(row => row.name === wanted.name && this.keyOf(this.rows, row).ordinal === wanted.ordinal);
    this.selected = (match ?? this.rows.find(row => row.index > 0 && row.changed) ?? this.rows[0]).index;
    this.status.textContent = this.summary();
    this.renderList();
    this.showSelected();
  }

  /** Name plus ordinal among same-named passes: stable across compiles, unlike the index. */
  private keyOf(rows: PipelineRow[], row: PipelineRow) {
    return { name: row.name, ordinal: rows.filter(r => r.name === row.name && r.index < row.index).length };
  }

  private summary() {
    return `${this.rows.length - 1} passes, ${this.rows.filter(r => r.index > 0 && r.changed).length} changed the MIR`;
  }

  private visibleRows() {
    const text = this.search.value.trim().toLowerCase();
    return this.rows.filter(r => (!this.hideUnchanged.checked || r.changed) && (!text || r.name.toLowerCase().includes(text)));
  }

  private renderList() {
    this.list.replaceChildren(...this.visibleRows().map(r => {
      const item = el('div', `pipeline-row${r.changed ? ' changed' : ''}${r.index === this.selected ? ' selected' : ''}`, r.name);
      item.onclick = () => { this.select(r.index); this.list.focus(); };
      return item;
    }));
    this.list.querySelector('.selected')?.scrollIntoView({ block: 'nearest' });
  }

  private select(index: number) {
    this.selected = index;
    this.renderList();
    this.showSelected();
  }

  private showSelected() {
    const row = this.rows[this.selected];
    if (!row) return;
    // The baseline has nothing before it: diff it against itself.
    this.original.setValue(row.before ?? row.after);
    this.modified.setValue(row.after);
    const key = this.keyOf(this.rows, row);
    this.state.options.pass = key.name;
    this.state.options.passOrdinal = key.ordinal;
  }

  private applyOptions() {
    this.editor.updateOptions({
      renderSideBySide: this.sideBySide.checked,
      hideUnchangedRegions: { enabled: !this.showFull.checked },
    });
  }

  private dispose() {
    this.disposers.forEach(d => d());
    this.editor.dispose();
    this.original.dispose();
    this.modified.dispose();
    this.ctx.store.unregisterPanel(this.state.id);
  }
}
