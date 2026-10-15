/** What an emulator panel needs from a machine: boot an image, run/pause, take the screen's keys. */
export interface Machine {
  readonly isRunning: boolean;
  /** Hard-resets the machine with the compiled image loaded and started. */
  boot(image: Uint8Array): Promise<void>;
  run(): void;
  pause(): void;
  /** Routes keyboard input from the focusable screen element to the machine. */
  bindInput(screen: HTMLCanvasElement): void;
  /** Releases the machine's resources; it is not used again. */
  dispose(): void;
}
