import type { CompileClient } from '../compileClient';
import type { Store } from '../store';

export interface AppContext {
  store: Store;
  client: CompileClient;
  addPanel(type: string, state: PanelState, title: string): void;
  /** Opens the Problems panel of a C# editor, or brings the existing one to the front. */
  showProblems(sourceId: string): void;
}

/** Serializable per-panel state (what Golden Layout saves in the layout config). */
export interface PanelState {
  id: string;
  upstreamId?: string;
  options: Record<string, unknown>;
}

export const SOURCE = 'source';
export const DISASSEMBLY = 'disassembly';
export const PROBLEMS = 'problems';
export const EMULATOR_BBC = 'emulator-bbcmicro';

/** emulatorKind (from TargetInfo) → panel component type. One emulator per system. */
export const EMULATOR_PANELS: Record<string, string> = { jsbeeb: EMULATOR_BBC };

export function el<K extends keyof HTMLElementTagNameMap>(tag: K, className?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

/** Per-type ordinal of a panel, from its id ("d2" → 2). */
const ordinal = (id: string) => id.replace(/^\D+/, '');

export const sourceTitle = (id: string) => `C# Editor #${ordinal(id)}`;
export const compilerLabel = (id: string) => `Compiler #${ordinal(id)}`;

/** "Compiler #1 (C# Editor #1)" style titles that name the panels a panel is derived from. */
export function disassemblyTitle(targetName: string, id: string, upstreamId?: string) {
  return `${targetName} ${compilerLabel(id)}${upstreamId ? ` (${sourceTitle(upstreamId)})` : ''}`;
}

export function emulatorTitle(store: Store, id: string, upstreamId?: string) {
  const parents: string[] = [];
  if (upstreamId) {
    const compiler = store.getPanels().find(p => p.id === upstreamId);
    if (compiler?.upstreamId) parents.push(sourceTitle(compiler.upstreamId));
    parents.push(compilerLabel(upstreamId));
  }
  return `Emulator #${ordinal(id)}${parents.length ? ` (${parents.join(', ')})` : ''}`;
}

export const problemsTitle = (sourceId: string) => `Problems (${sourceTitle(sourceId)})`;

export function summarize(diagnostics: readonly { severity: string }[]): string {
  const errors = diagnostics.filter(d => d.severity === 'error').length;
  const warnings = diagnostics.filter(d => d.severity === 'warning').length;
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
  return [errors && plural(errors, 'error'), warnings && plural(warnings, 'warning')].filter(Boolean).join(', ');
}
