import { GoldenLayout, type ComponentContainer, type LayoutConfig } from 'golden-layout';
import { CompileClient } from './compileClient';
import { DEFAULT_SOURCE, SourcePanel } from './panels/sourcePanel';
import { DisassemblyPanel } from './panels/disassemblyPanel';
import { BbcMicroEmulatorPanel } from './panels/emulatorPanel';
import { DISASSEMBLY, EMULATOR_BBC, SOURCE, type AppContext, type PanelState } from './panels/context';
import { Store } from './store';
import { initTheme } from './theme';
import { Overlay } from './overlay';
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
};

const factory = <T>(Panel: new (c: ComponentContainer, s: PanelState, ctx: AppContext) => T) =>
  (container: ComponentContainer, state: unknown) => { new Panel(container, state as PanelState, ctx); };

layout.registerComponentFactoryFunction(SOURCE, factory(SourcePanel));
layout.registerComponentFactoryFunction(DISASSEMBLY, factory(DisassemblyPanel));
layout.registerComponentFactoryFunction(EMULATOR_BBC, factory(BbcMicroEmulatorPanel));

const defaultLayout: LayoutConfig = {
  header: { popout: false },
  root: {
    type: 'row',
    content: [
      { type: 'component', componentType: SOURCE, title: 'C#', width: 36, componentState: { id: 's1', options: { source: DEFAULT_SOURCE } } satisfies PanelState },
      { type: 'component', componentType: DISASSEMBLY, title: 'BBC Micro asm', width: 36, componentState: { id: 'd1', upstreamId: 's1', options: { targetId: 'bbcmicro' } } satisfies PanelState },
      { type: 'component', componentType: EMULATOR_BBC, title: 'Emulator', width: 28, componentState: { id: 'e1', upstreamId: 'd1', options: {} } satisfies PanelState },
    ],
  },
};

layout.loadLayout(defaultLayout);

const resize = () => layout.setSize(layout.container.clientWidth, layout.container.clientHeight);
window.addEventListener('resize', resize);
new ResizeObserver(resize).observe(layout.container);
