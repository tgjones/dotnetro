import { GoldenLayout, LayoutConfig, type ComponentContainer } from 'golden-layout';
import { CompileClient } from './compileClient';
import { DEFAULT_SOURCE, SourcePanel } from './panels/sourcePanel';
import { ProblemsPanel } from './panels/problemsPanel';
import { DisassemblyPanel } from './panels/disassemblyPanel';
import { BbcMicroEmulatorPanel } from './panels/emulatorPanel';
import { DISASSEMBLY, EMULATOR_BBC, PROBLEMS, SOURCE, problemsTitle, type AppContext, type PanelState } from './panels/context';
import { Store } from './store';
import { initTheme } from './theme';
import { Overlay } from './overlay';
import { WARN_URL_BYTES, loadSavedState, sanitizeLayout, storeState } from './persistence';
import './app.css';

initTheme();

const overlay = new Overlay();
const store = new Store();
const client = new CompileClient();

if (import.meta.env.PROD && 'serviceWorker' in navigator) {
  // Cache-first for the fingerprinted .NET payload; failure to register only costs repeat-visit speed.
  navigator.serviceWorker.register(new URL('sw.js', document.baseURI)).catch(() => {});
}

client.onProgress = p => overlay.progress(p.stage, p.loaded, p.total);
client.ready.then(() => overlay.progress('compiling', 0, 0), error => overlay.fail(error, () => location.reload()));

// The overlay stays until the first real compile result exists, so it never drops onto an empty page.
const stopWatching = store.subscribeAll((_id, result) => {
  if (result && (result.kind === 'target' || !result.result.ok)) {
    overlay.hide();
    stopWatching();
  }
});

const layout = new GoldenLayout(document.getElementById('layout')!);

const ctx: AppContext = {
  store,
  client,
  // New panels get their own stack at the end of the top row (TypeId.FirstRow = 4; GoldenLayout
  // declares the enum `const`, which isolatedModules cannot import) rather than hiding a sibling tab.
  addPanel: (type, state, title) => { layout.addComponentAtLocation(type, state as never, title, [{ typeId: 4 }]); },
  showProblems: sourceId => {
    const existing = store.getPanels().find(p => p.type === PROBLEMS && p.upstreamId === sourceId);
    if (existing) existing.focus?.();
    else ctx.addPanel(PROBLEMS, { id: store.newId('p'), upstreamId: sourceId, options: {} }, problemsTitle(sourceId));
  },
};

const factory = <T>(Panel: new (c: ComponentContainer, s: PanelState, ctx: AppContext) => T) =>
  (container: ComponentContainer, state: unknown) => { new Panel(container, state as PanelState, ctx); };

layout.registerComponentFactoryFunction(SOURCE, factory(SourcePanel));
layout.registerComponentFactoryFunction(DISASSEMBLY, factory(DisassemblyPanel));
layout.registerComponentFactoryFunction(EMULATOR_BBC, factory(BbcMicroEmulatorPanel));
layout.registerComponentFactoryFunction(PROBLEMS, factory(ProblemsPanel));

const defaultLayout: LayoutConfig = {
  header: { popout: false },
  root: {
    type: 'row',
    content: [
      { type: 'component', componentType: SOURCE, title: 'C# Editor #1', width: 36, componentState: { id: 's1', options: { source: DEFAULT_SOURCE } } satisfies PanelState },
      { type: 'component', componentType: DISASSEMBLY, title: 'BBC Micro Compiler #1 (C# Editor #1)', width: 36, componentState: { id: 'd1', upstreamId: 's1', options: { targetId: 'bbcmicro' } } satisfies PanelState },
      { type: 'component', componentType: EMULATOR_BBC, title: 'Emulator #1 (C# Editor #1, Compiler #1)', width: 28, componentState: { id: 'e1', upstreamId: 'd1', options: {} } satisfies PanelState },
    ],
  },
};

const saved = loadSavedState();
const restored = saved && sanitizeLayout(saved, new Set([SOURCE, DISASSEMBLY, EMULATOR_BBC, PROBLEMS]));
try {
  layout.loadLayout(restored ?? defaultLayout);
} catch (e) {
  console.warn('Could not restore saved state, using the default layout', e);
  layout.clear();
  layout.loadLayout(defaultLayout);
}

// Share controls in the header: a link button and a warning once the URL gets long.
const shareWarning = document.createElement('span');
shareWarning.className = 'share-warning';
const copyLink = document.createElement('button');
copyLink.textContent = 'Copy Permalink';
copyLink.onclick = async () => {
  save();
  try { await navigator.clipboard.writeText(location.href); copyLink.textContent = 'Copied!'; }
  catch { copyLink.textContent = 'Copy the address bar'; }
  setTimeout(() => { copyLink.textContent = 'Copy Permalink'; }, 2000);
};
document.querySelector('header')!.append(shareWarning, copyLink);

// Debounced save of the whole layout (which embeds every panel's state) to the hash and localStorage.
let saveTimer: number | undefined;
function save() {
  window.clearTimeout(saveTimer);
  const length = storeState({ v: 1, layout: LayoutConfig.fromResolved(layout.saveLayout()) });
  shareWarning.textContent = length > WARN_URL_BYTES
    ? `Link is ${(length / 1024).toFixed(1)} KB; some chat/browsers may truncate it. Consider shortening the source.`
    : '';
}
layout.on('stateChanged', () => { window.clearTimeout(saveTimer); saveTimer = window.setTimeout(save, 500); });
window.addEventListener('pagehide', save);

const resize = () => layout.setSize(layout.container.clientWidth, layout.container.clientHeight);
window.addEventListener('resize', resize);
new ResizeObserver(resize).observe(layout.container);
