// A BBC Micro Model B driven by jsbeeb's building blocks (CPU, video, FDC, keyboard). jsbeeb has
// no embedding API (its page entry point is a script wired to DOM ids), so this does what its own
// headless/test harness does plus a canvas and a requestAnimationFrame loop. No audio yet.
import { Canvas } from 'jsbeeb/src/web/canvas.js';
import { Video } from 'jsbeeb/src/video.js';
import { FakeSoundChip } from 'jsbeeb/src/soundchip.js';
import { findModel } from 'jsbeeb/src/models.js';
import { machineSpec, nullIo } from 'jsbeeb/src/machine-spec.js';
import { discFor } from 'jsbeeb/src/fdc.js';
import { Typist } from 'jsbeeb/src/typist.js';

const MaxCyclesPerFrame = 200_000;
const ShiftBreakHoldMs = 1000;

export class BbcMicroMachine {
  private readonly model = findModel('B-DFS1.2');
  private readonly canvas: any;
  private cpu: any;
  private typist: any;
  private running = false;
  private lastFrame = 0;
  private ready: Promise<void>;
  private disc: unknown;

  constructor(screen: HTMLCanvasElement) {
    screen.width = 800;
    screen.height = 600;
    this.canvas = new Canvas(screen, false);
    const video = new Video(this.model.isMaster, this.canvas.fb32,
      (minx: number, miny: number, maxx: number, maxy: number) => this.canvas.paint(minx, miny, maxx, maxy, { fields: 1 }));
    this.cpu = new this.model.Cpu(this.model, {
      ...nullIo({ video, soundChip: new FakeSoundChip() }),
      config: machineSpec({ keyLayout: 'natural' }),
    });
    this.typist = new Typist(this.cpu);
    this.ready = this.cpu.initialise();
  }

  get isRunning() { return this.running; }

  /** Hard-resets the machine with the image in drive 0 and shift-breaks it: the image's boot option runs !BOOT. */
  async boot(image: Uint8Array | undefined): Promise<void> {
    await this.ready;
    this.typist.cancel();
    this.cpu.reset(true);
    this.disc = image ? discFor('MAIN.ssd', image) : undefined;
    this.cpu.fdc.loadDisc(0, this.disc);
    if (image) {
      const shift = this.model.keys.SHIFT;
      this.typist.type([shift, ShiftBreakHoldMs, shift], false);
    }
    this.run();
  }

  run() {
    if (this.running) return;
    this.running = true;
    this.lastFrame = 0;
    requestAnimationFrame(t => this.frame(t));
  }

  pause() { this.running = false; this.clearKeys(); }

  // The natural layout names a key by the character it produced, so what you type comes out right
  // whatever your keyboard layout; remember the name used on the way down for the release.
  private readonly held = new Map<string, string>();

  keyDown(e: KeyboardEvent) {
    const name = e.key.length === 1 ? e.key : e.code;
    this.held.set(e.code, name);
    this.cpu.keyboardInterface.keyDown(name, e.shiftKey);
  }

  keyUp(e: KeyboardEvent) {
    this.cpu.keyboardInterface.keyUp(this.held.get(e.code) ?? e.code);
    this.held.delete(e.code);
  }

  clearKeys() { this.held.clear(); this.cpu.keyboardInterface.clearKeys(); }
  /** BREAK key: hold reset, release. */
  breakKey(down: boolean) { this.cpu.setReset(down); }

  private frame(now: number) {
    if (!this.running) return;
    requestAnimationFrame(t => this.frame(t));
    if (this.lastFrame !== 0) {
      const cycles = Math.min(((now - this.lastFrame) * this.model.cyclesPerSecond) / 1000, MaxCyclesPerFrame) | 0;
      this.cpu.execute(cycles);
    }
    this.lastFrame = now;
  }
}
