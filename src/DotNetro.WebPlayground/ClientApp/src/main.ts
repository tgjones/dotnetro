import { GoldenLayout, LayoutConfig, type ComponentContainer } from 'golden-layout';
import { CompileClient } from './compileClient';
import { DEFAULT_SOURCE, SourcePanel } from './panels/sourcePanel';
import { ProblemsPanel } from './panels/problemsPanel';
import { DisassemblyPanel } from './panels/disassemblyPanel';
import { BbcMicroEmulatorPanel, C64EmulatorPanel } from './panels/emulatorPanel';
import { OptPipelinePanel } from './panels/optPipelinePanel';
import { DISASSEMBLY, EMULATOR_BBC, EMULATOR_C64, OPT_PIPELINE, PROBLEMS, SOURCE, problemsTitle, type AppContext, type PanelState } from './panels/context';
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
layout.registerComponentFactoryFunction(EMULATOR_C64, factory(C64EmulatorPanel));
layout.registerComponentFactoryFunction(PROBLEMS, factory(ProblemsPanel));
layout.registerComponentFactoryFunction(OPT_PIPELINE, factory(OptPipelinePanel));

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
const restored = saved && sanitizeLayout(saved, new Set([SOURCE, DISASSEMBLY, EMULATOR_BBC, EMULATOR_C64, PROBLEMS, OPT_PIPELINE]));
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
// Octicons (MIT) "link" and "mark-github" glyphs, inlined so no font or image request is needed.
const icon = (path: string) =>
  `<svg viewBox="0 0 16 16" width="20" height="20" fill="currentColor" aria-hidden="true"><path d="${path}"/></svg>`;
const LINK_ICON = 'M7.775 3.275a.75.75 0 001.06 1.06l1.25-1.25a2 2 0 112.83 2.83l-2.5 2.5a2 2 0 01-2.83 0 .75.75 0 00-1.06 1.06 3.5 3.5 0 004.95 0l2.5-2.5a3.5 3.5 0 00-4.95-4.95l-1.25 1.25zm-4.69 9.64a2 2 0 010-2.83l2.5-2.5a2 2 0 012.83 0 .75.75 0 001.06-1.06 3.5 3.5 0 00-4.95 0l-2.5 2.5a3.5 3.5 0 004.95 4.95l1.25-1.25a.75.75 0 00-1.06-1.06l-1.25 1.25a2 2 0 01-2.83 0z';
const GITHUB_ICON = 'M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z';

const copyLink = document.createElement('button');
copyLink.className = 'share-button';
copyLink.title = 'Copy permalink';
copyLink.setAttribute('aria-label', 'Copy permalink');
copyLink.innerHTML = icon(LINK_ICON);
const copyStatus = document.createElement('span');
copyLink.append(copyStatus);
copyLink.onclick = async () => {
  save();
  try { await navigator.clipboard.writeText(location.href); copyStatus.textContent = 'Copied!'; }
  catch { copyStatus.textContent = 'Copy the address bar'; }
  setTimeout(() => { copyStatus.textContent = ''; }, 2000);
};

const githubLink = document.createElement('a');
githubLink.className = 'github-link';
githubLink.href = 'https://github.com/tgjones/dotnetro';
githubLink.target = '_blank';
githubLink.rel = 'noopener';
githubLink.title = 'DotNetro on GitHub';
githubLink.setAttribute('aria-label', 'DotNetro on GitHub');
githubLink.innerHTML = icon(GITHUB_ICON);

document.querySelector('header')!.append(shareWarning, copyLink, githubLink);

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
