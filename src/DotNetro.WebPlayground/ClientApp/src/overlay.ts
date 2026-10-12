import type { Stage } from './protocol';

const labels: Record<Stage, string> = {
  runtime: 'Downloading runtime…',
  compiler: 'Downloading compiler…',
  roslyn: 'Initialising Roslyn…',
  compiling: 'Compiling…',
};

// Drives the static overlay markup in index.html (which paints before any JS runs).
export class Overlay {
  private readonly root = document.getElementById('overlay')!;
  private readonly bar = document.getElementById('overlay-bar') as HTMLElement;
  private readonly stage = document.getElementById('overlay-stage')!;
  private readonly error = document.getElementById('overlay-error')!;
  private readonly retry = document.getElementById('overlay-retry') as HTMLButtonElement;

  progress(stage: Stage, loaded: number, total: number) {
    this.stage.textContent = labels[stage];
    const fraction = total > 0 ? Math.min(loaded / total, 1) : stage === 'compiling' ? 1 : 0;
    this.bar.style.width = `${(fraction * 100).toFixed(1)}%`;
    this.bar.classList.toggle('indeterminate', total === 0 && stage !== 'compiling');
  }

  fail(error: Error, retry: () => void) {
    this.root.classList.add('failed');
    this.stage.textContent = 'Failed to load the compiler.';
    this.error.textContent = error.message;
    this.retry.onclick = retry;
  }

  hide() {
    this.root.classList.add('done');
    this.root.addEventListener('transitionend', () => this.root.remove(), { once: true });
    window.setTimeout(() => this.root.remove(), 1000); // in case the transition never fires
  }
}
