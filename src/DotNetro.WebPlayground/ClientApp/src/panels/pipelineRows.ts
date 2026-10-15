import type { PassSnapshot } from '../protocol';

export interface PipelineRow {
  /** Position in the snapshot list; the identity of a row (pass names repeat). */
  index: number;
  name: string;
  /** The pass changed the module text (always true for the `Translation` baseline). */
  changed: boolean;
  /** Module text after the previous row; undefined for the baseline. */
  before?: string;
  /** Module text after this row. */
  after: string;
}

/**
 * Rebuilds the full sequence from the sparse snapshot list: a snapshot without
 * `mirText` left the module unchanged, so it carries the previous text forward.
 */
export function buildRows(snapshots: PassSnapshot[]): PipelineRow[] {
  const rows: PipelineRow[] = [];
  let previous: string | undefined;
  snapshots.forEach((snapshot, index) => {
    const changed = snapshot.mirText !== undefined && snapshot.mirText !== null;
    const after = changed ? snapshot.mirText! : (previous ?? '');
    rows.push({ index, name: snapshot.passName, changed, before: previous, after });
    previous = after;
  });
  return rows;
}
