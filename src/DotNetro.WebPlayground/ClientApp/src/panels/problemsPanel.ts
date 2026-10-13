import type { ComponentContainer } from 'golden-layout';
import type { Diagnostic } from '../protocol';
import { PROBLEMS, el, problemsTitle, summarize, type AppContext, type PanelState } from './context';

const SOURCE_LABELS: Record<Diagnostic['source'], string> = { roslyn: 'C#', dotnetro: 'DotNetro' };

/** Every error and warning of one C# editor and of the compiler panels fed by it, with room for long messages. */
export class ProblemsPanel {
  private readonly list = el('div', 'problems');
  private readonly status = el('span', 'status');
  private readonly disposers: (() => void)[] = [];

  constructor(container: ComponentContainer, state: PanelState, private readonly ctx: AppContext) {
    const sourceId = state.upstreamId!;
    const footer = el('div', 'statusbar');
    footer.append(this.status);
    const root = el('div', 'panel');
    root.append(this.list, footer);
    container.element.append(root);
    container.setTitle(problemsTitle(sourceId));

    ctx.store.registerPanel({ id: state.id, type: PROBLEMS, upstreamId: sourceId, focus: () => container.focus() });
    container.setState(state as never);

    const refresh = () => this.render(sourceId);
    this.disposers.push(ctx.store.subscribe(sourceId, refresh), ctx.store.subscribeDownstream(sourceId, refresh));
    refresh();
    container.on('beforeComponentRelease', () => {
      this.disposers.forEach(d => d());
      ctx.store.unregisterPanel(state.id);
    });
  }

  private rows(sourceId: string): Diagnostic[] {
    const rows: Diagnostic[] = [];
    const own = this.ctx.store.get(sourceId);
    if (own?.kind === 'csharp') rows.push(...own.result.diagnostics);
    for (const panel of this.ctx.store.getPanels().filter(p => p.upstreamId === sourceId)) {
      const result = this.ctx.store.get(panel.id);
      if (result?.kind === 'target') rows.push(...result.result.diagnostics);
    }
    return rows;
  }

  private render(sourceId: string) {
    const rows = this.rows(sourceId);
    this.list.replaceChildren();
    if (rows.length === 0) this.list.append(el('div', 'empty', 'No problems'));
    for (const d of rows) {
      const row = el('div', `problem ${d.severity}`);
      row.append(el('span', 'origin', SOURCE_LABELS[d.source]), el('span', 'message', d.message));
      if (d.line) {
        row.append(el('span', 'where', `${d.line}:${d.column ?? 1}`));
        row.classList.add('clickable');
        row.onclick = () => this.ctx.store.reveal(sourceId, d.line!, d.column ?? 1);
      }
      this.list.append(row);
    }
    this.status.textContent = summarize(rows) || 'No problems';
    this.status.classList.toggle('error', rows.some(d => d.severity === 'error'));
  }
}
