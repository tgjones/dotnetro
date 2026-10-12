// Entry for Monaco's editor worker. A local file (rather than importing the package's worker with
// `?worker` directly) keeps Vite's dev dependency optimizer from pre-bundling it, which breaks
// the worker's default export.
import 'monaco-vs/editor/editor.worker.js';
