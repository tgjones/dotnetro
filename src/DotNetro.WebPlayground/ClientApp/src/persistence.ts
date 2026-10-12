import { deflateSync, inflateSync, strFromU8, strToU8 } from 'fflate';
import type { LayoutConfig } from 'golden-layout';

/**
 * Versioned, shareable playground state. The Golden Layout config already embeds every panel's
 * `PanelState` (id, upstreamId, options incl. target id, optimisation level and the C# source) as
 * `componentState`, so the layout is the single source of truth: `{ v, layout }`.
 */
export interface SavedState { v: 1; layout: LayoutConfig }

export const STORAGE_KEY = 'dotnetro.playground.state';
export const WARN_URL_BYTES = 8 * 1024;

type Json = Record<string, any>;

export function encodeState(state: SavedState): string {
  const bytes = deflateSync(strToU8(JSON.stringify(state)), { level: 9 });
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function decodeState(text: string): SavedState | undefined {
  try {
    const binary = atob(text.replace(/-/g, '+').replace(/_/g, '/'));
    const bytes = Uint8Array.from(binary, c => c.charCodeAt(0));
    const parsed = JSON.parse(strFromU8(inflateSync(bytes)));
    return parsed?.v === 1 && parsed.layout ? parsed as SavedState : undefined;
  } catch {
    return undefined; // corrupt or foreign link: fall back to the default layout
  }
}

/** Hash takes precedence over the localStorage copy of the last state. */
export function loadSavedState(): SavedState | undefined {
  const hash = location.hash.replace(/^#/, '');
  if (hash) {
    const fromHash = decodeState(hash);
    if (fromHash) return fromHash;
  }
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored ? decodeState(stored) : undefined;
  } catch {
    return undefined;
  }
}

/** Writes the state to the hash (replaceState, no history entries) and localStorage. Returns the encoded length. */
export function storeState(state: SavedState): number {
  const encoded = encodeState(state);
  history.replaceState(null, '', `#${encoded}`);
  try { localStorage.setItem(STORAGE_KEY, encoded); } catch { /* storage unavailable or full: the hash still works */ }
  return encoded.length;
}

/**
 * Drops what an old or hand-edited link can no longer provide: unknown panel types, panels whose
 * upstream panel is gone (repeated until stable) and containers left empty. Returns undefined when
 * no panel survives, so the caller can use the default layout. Unknown *targets* are handled by the
 * disassembly panel itself (it falls back to the first available target).
 */
export function sanitizeLayout(state: SavedState, knownTypes: ReadonlySet<string>): LayoutConfig | undefined {
  const root = state.layout?.root as Json | undefined;
  if (!root) return undefined;

  const keep = (item: Json) => item.type !== 'component' || knownTypes.has(item.componentType);
  const prune = (item: Json | undefined, accept: (c: Json) => boolean): Json | undefined => {
    if (!item || typeof item !== 'object') return undefined;
    if (item.type === 'component') return keep(item) && accept(item) ? item : undefined;
    if (!Array.isArray(item.content)) return undefined;
    const content = item.content.map((c: Json) => prune(c, accept)).filter((c: Json | undefined): c is Json => !!c);
    if (content.length === 0) return undefined;
    const result: Json = { ...item, content };
    if (typeof result.activeItemIndex === 'number') result.activeItemIndex = Math.min(result.activeItemIndex, content.length - 1);
    return result;
  };

  const idsOf = (item: Json, ids = new Set<string>()): Set<string> => {
    if (item.type === 'component') ids.add(item.componentState?.id);
    else item.content?.forEach((c: Json) => idsOf(c, ids));
    return ids;
  };

  let current = prune(root, () => true);
  while (current) {
    const before = idsOf(current);
    current = prune(current, c => !!c.componentState?.id && (!c.componentState.upstreamId || before.has(c.componentState.upstreamId)));
    if (current && idsOf(current).size === before.size) break;
  }
  return current ? { ...state.layout, root: current } as LayoutConfig : undefined;
}
