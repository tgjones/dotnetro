import type { CompileClient } from '../compileClient';
import type { Store } from '../store';

export interface AppContext {
  store: Store;
  client: CompileClient;
  addPanel(type: string, state: PanelState, title: string): void;
}

/** Serializable per-panel state (what Golden Layout saves in the layout config). */
export interface PanelState {
  id: string;
  upstreamId?: string;
  options: Record<string, unknown>;
}

export const SOURCE = 'source';
export const DISASSEMBLY = 'disassembly';
export const EMULATOR_BBC = 'emulator-bbcmicro';

/** emulatorKind (from TargetInfo) → panel component type. One emulator per system. */
export const EMULATOR_PANELS: Record<string, string> = { jsbeeb: EMULATOR_BBC };

export function el<K extends keyof HTMLElementTagNameMap>(tag: K, className?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}
