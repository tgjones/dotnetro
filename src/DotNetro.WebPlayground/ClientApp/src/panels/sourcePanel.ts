import type { ComponentContainer } from 'golden-layout';
import { monaco, type Monaco } from '../monaco';
import { monacoTheme } from '../theme';
import type { Diagnostic } from '../protocol';
import { DISASSEMBLY, el, type AppContext, type PanelState } from './context';

export const DEFAULT_SOURCE = `Console.WriteLine("Hello, World!");
`;

const DEBOUNCE_MS = 400;

/** C# editor. Owns the text, compiles it with Roslyn and publishes the IL for downstream panels. */
export class SourcePanel {
  private readonly editor: Monaco.editor.IStandaloneCodeEditor;
  private readonly model: Monaco.editor.ITextModel;
  private readonly status = el('span', 'status');
  private timer: number | undefined;
  private readonly disposers: (() => void)[] = [];

  constructor(private readonly container: ComponentContainer, private readonly state: PanelState, private readonly ctx: AppContext) {
    const root = el('div', 'panel');
    const toolbar = el('div', 'toolbar');
    const release = el('label');
    const releaseBox = el('input');
    releaseBox.type = 'checkbox';
    releaseBox.checked = state.options.optimize !== false;
    release.append(releaseBox, ' Release IL');
    const addDisassembly = el('button', undefined, '+ Disassembly');
    addDisassembly.title = 'Add a disassembly panel fed by this source';
    toolbar.append(release, addDisassembly, this.status);
    const host = el('div', 'editor-host');
    root.append(toolbar, host);
    container.element.append(root);

    this.model = monaco.editor.createModel((state.options.source as string | undefined) ?? DEFAULT_SOURCE, 'csharp');
    this.editor = monaco.editor.create(host, {
      model: this.model,
      theme: monacoTheme(),
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 13,
    });

    ctx.store.registerPanel({ id: state.id, type: container.componentType as string });
    this.persist();

    this.model.onDidChangeContent(() => this.schedule());
    releaseBox.onchange = () => { state.options.optimize = releaseBox.checked; this.persist(); this.schedule(0); };
    addDisassembly.onclick = () => {
      const targetId = ctx.client.targets[0]?.id ?? 'bbcmicro';
      ctx.addPanel(DISASSEMBLY, { id: ctx.store.newId('d'), upstreamId: state.id, options: { targetId } }, 'Disassembly');
    };

    this.disposers.push(ctx.store.subscribeDownstream(state.id, () => this.showMarkers()));
    container.on('beforeComponentRelease', () => this.dispose());
    this.schedule(0);
  }

  private persist() {
    this.state.options.source = this.model.getValue();
    this.container.setState(this.state as never);
  }

  private schedule(delay = DEBOUNCE_MS) {
    window.clearTimeout(this.timer);
    this.timer = window.setTimeout(() => void this.compile(), delay);
  }

  private async compile() {
    this.persist();
    this.status.textContent = 'Compiling…';
    await this.ctx.client.ready.catch(() => {});
    const result = await this.ctx.client.compileCSharp(this.state.id, this.model.getValue(), this.state.options.optimize !== false);
    if (!result) return; // superseded by a newer edit
    this.ctx.store.publish(this.state.id, { kind: 'csharp', result });
    this.showMarkers();
  }

  private showMarkers() {
    const own = this.ctx.store.get(this.state.id);
    const diagnostics: Diagnostic[] = [];
    if (own?.kind === 'csharp') diagnostics.push(...own.result.diagnostics);
    for (const downstream of this.ctx.store.downstreamResults(this.state.id)) {
      if (downstream.kind === 'target') diagnostics.push(...downstream.result.diagnostics);
    }

    for (const source of ['roslyn', 'dotnetro'] as const) {
      monaco.editor.setModelMarkers(this.model, source, diagnostics.filter(d => d.source === source).map(d => this.toMarker(d)));
    }

    const errors = diagnostics.filter(d => d.severity === 'error').length;
    const warnings = diagnostics.filter(d => d.severity === 'warning').length;
    this.status.textContent = errors || warnings ? `${errors} error(s), ${warnings} warning(s)` : 'OK';
  }

  private toMarker(d: Diagnostic): Monaco.editor.IMarkerData {
    // DotNetro diagnostics have no location yet: pin them to the first line.
    const line = Math.min(Math.max(d.line ?? 1, 1), this.model.getLineCount());
    const column = d.column ?? 1;
    const word = d.line ? this.model.getWordAtPosition({ lineNumber: line, column }) : null;
    return {
      severity: d.severity === 'error' ? monaco.MarkerSeverity.Error
        : d.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Info,
      message: d.message,
      startLineNumber: line,
      startColumn: column,
      endLineNumber: line,
      endColumn: word ? word.endColumn : d.line ? column + 1 : this.model.getLineMaxColumn(line),
      source: d.source,
    };
  }

  private dispose() {
    window.clearTimeout(this.timer);
    this.disposers.forEach(d => d());
    this.ctx.store.unregisterPanel(this.state.id);
    this.editor.dispose();
    this.model.dispose();
  }
}
