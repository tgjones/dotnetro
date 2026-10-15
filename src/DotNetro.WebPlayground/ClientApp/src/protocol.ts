// Contract between the shell and the compile worker. The result shapes mirror the JSON that
// DotNetro.WebPlayground.Playground returns (camelCase).

export interface TargetInfo { id: string; displayName: string; emulatorKind: string }

export type Severity = 'error' | 'warning' | 'info';
export interface Diagnostic {
  severity: Severity;
  message: string;
  line?: number;
  column?: number;
  source: 'roslyn' | 'dotnetro';
}

export interface CSharpResult { ok: boolean; diagnostics: Diagnostic[]; assembly?: string }
/** `mirText` is absent when the module text is identical to the previous snapshot's. */
export interface PassSnapshot { passName: string; mirText?: string }
export interface TargetResult {
  ok: boolean;
  diagnostics: Diagnostic[];
  asm?: string;
  origin?: number;
  image?: string;
  passSnapshots?: PassSnapshot[];
}

export type Stage = 'runtime' | 'compiler' | 'roslyn' | 'compiling';

export type WorkerRequest =
  | { type: 'init'; baseUrl: string }
  | { type: 'csharp'; key: string; gen: number; source: string; optimize: boolean }
  | { type: 'target'; key: string; gen: number; assembly: string; targetId: string; snapshots: boolean };

export type WorkerResponse =
  | { type: 'progress'; stage: Stage; loaded: number; total: number }
  | { type: 'ready'; targets: TargetInfo[] }
  | { type: 'init-error'; message: string }
  | { type: 'csharp-result'; key: string; gen: number; result: CSharpResult }
  | { type: 'target-result'; key: string; gen: number; result: TargetResult };
