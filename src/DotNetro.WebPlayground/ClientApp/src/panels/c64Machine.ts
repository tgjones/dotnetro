// A Commodore 64 driven by the VICE libretro core that EmulatorJS builds for the web (see
// scripts/c64Core.ts for where it comes from). The core is a whole RetroArch build, so it owns
// its main loop, rendering (into the given canvas), audio and keyboard handling; we only
// load it, hand it the program and switch it on and off. It is driven the way EmulatorJS's own
// frontend does it (its GameManager.js), without the frontend.
import type { Machine } from './machine';

const coreScript = () => new URL('c64/vice_x64_libretro.js', document.baseURI).href;
const coreWasm = () => new URL('c64/vice_x64_libretro.wasm', document.baseURI).href;

let runtimeLoad: Promise<void> | undefined;

/** Loads the core's script once; it defines the global `EJS_Runtime` factory. */
function loadRuntime(): Promise<void> {
  runtimeLoad ??= new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = coreScript();
    script.onload = () => resolve();
    script.onerror = () => { runtimeLoad = undefined; reject(new Error('Could not load the C64 emulator core')); };
    document.head.append(script);
  });
  return runtimeLoad;
}

const retroArchConfig = [
  'video_vsync = true',
  'video_smooth = false',
  'audio_latency = 64',
  'savefile_directory = "/data/saves"',
].join('\n') + '\n';

const gamePath = '/game.prg';

// The VICE core's picture (borders included) is shown 4:3.
const DisplayAspect = 4 / 3;

export class C64Machine implements Machine {
  private module: any;
  private creating: Promise<any> | undefined;
  private started = false;
  private running = false;
  private disposed = false;
  private resizeObserver: ResizeObserver | undefined;

  constructor(private readonly screen: HTMLCanvasElement) {}

  get isRunning() { return this.running; }

  async boot(image: Uint8Array): Promise<void> {
    const module = await this.ensureModule();
    if (this.disposed) return;
    // The core loads content once per run; a new program replaces the file and restarts the machine.
    module.FS.writeFile(gamePath, image);
    if (this.started) {
      module.cwrap('system_restart', '', [])();
    } else {
      module.callMain([gamePath]);
      this.started = true;
    }
    this.run();
  }

  run() {
    this.running = true;
    if (this.started) this.module.cwrap('toggleMainLoop', null, ['number'])(1);
  }

  pause() {
    this.running = false;
    if (this.started) this.module.cwrap('toggleMainLoop', null, ['number'])(0);
  }

  bindInput(screen: HTMLCanvasElement) {
    // The core binds the page's key events itself; it only gets to act on them while the screen is focused.
    const enable = (on: boolean) => { if (this.started) this.module.cwrap('ejs_set_keyboard_enabled', null, ['number'])(on ? 1 : 0); };
    screen.tabIndex = 0;
    screen.addEventListener('focus', () => enable(true));
    screen.addEventListener('blur', () => enable(false));
    screen.addEventListener('keydown', e => e.preventDefault());
    screen.addEventListener('mousedown', () => screen.focus());

    // The core draws its picture across the whole canvas and sizes its render target from the canvas's
    // on-screen size, so give the canvas the picture's aspect ratio and centre it in the panel.
    const wrap = screen.parentElement!;
    const fit = () => {
      const { width, height } = wrap.getBoundingClientRect();
      const w = Math.floor(Math.min(width, height * DisplayAspect));
      const h = Math.floor(w / DisplayAspect);
      Object.assign(screen.style, { width: `${w}px`, height: `${h}px`, inset: 'auto', left: `${Math.floor((width - w) / 2)}px`, top: `${Math.floor((height - h) / 2)}px` });
    };
    this.resizeObserver = new ResizeObserver(fit);
    this.resizeObserver.observe(wrap);
  }

  dispose() {
    this.disposed = true;
    this.running = false;
    this.resizeObserver?.disconnect();
    const module = this.module;
    if (!module) return;
    try { if (this.started) module.cwrap('toggleMainLoop', null, ['number'])(0); } catch { /* already stopped */ }
    // Like EmulatorJS, give the core a moment to settle before tearing it down.
    setTimeout(() => { try { module.abort(); } catch { /* aborting always throws */ } }, 1000);
  }

  private ensureModule(): Promise<any> {
    this.creating ??= this.createModule();
    return this.creating;
  }

  private async createModule() {
    await loadRuntime();
    const module = await (window as any).EJS_Runtime({
      noInitialRun: true,
      canvas: this.screen,
      parent: this.screen.parentElement ?? document.body,
      arguments: [], preRun: [], postRun: [], callbacks: {}, totalDependencies: 0,
      // The core prints an EmulatorJS banner at start-up; nothing here wants its console output.
      print: () => {},
      printErr: () => {},
      locateFile: (file: string) => (file.endsWith('.wasm') ? coreWasm() : file),
      getSavExt: () => '.srm',
    });

    const mkdirs = (path: string) => {
      let current = '';
      for (const part of path.split('/').slice(0, -1)) {
        if (!part) continue;
        current += '/' + part;
        try { module.FS.mkdir(current); } catch { /* exists */ }
      }
    };
    mkdirs('/home/web_user/.config/retroarch/retroarch.cfg');
    module.FS.writeFile('/home/web_user/.config/retroarch/retroarch.cfg', retroArchConfig);
    mkdirs('/data/saves/x');
    // The core asks for its options file at start-up; the directory must exist and the defaults will do.
    module.callbacks.setupCoreSettingFile = (path: string) => { mkdirs(path); module.FS.writeFile(path, ''); };
    this.module = module;
    return module;
  }
}
