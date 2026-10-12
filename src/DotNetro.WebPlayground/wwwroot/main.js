const source = document.getElementById('source');
const output = document.getElementById('output');
const status = document.getElementById('status');
const compileButton = document.getElementById('compile');
const download = document.getElementById('download');

const worker = new Worker('./worker.js', { type: 'module' });
const t0 = performance.now();
let nextId = 1;

worker.onerror = (e) => { status.textContent = `worker failed: ${e.message}`; };

worker.onmessage = ({ data }) => {
  if (data.type === 'ready') {
    status.textContent = `ready: runtime boot ${data.bootMs.toFixed(0)} ms, ${(performance.now() - t0).toFixed(0)} ms since page load`;
    compileButton.disabled = false;
    return;
  }

  const diagnostics = [...data.csharp.diagnostics, ...(data.target?.diagnostics ?? [])]
    .map(d => `${d.severity} ${d.source}${d.line ? ` (${d.line},${d.column})` : ''}: ${d.message}`);
  output.textContent = [...diagnostics, '', data.target?.asm ?? ''].join('\n');
  status.textContent = `roslyn ${data.roslynMs.toFixed(0)} ms, dotnetro ${data.dotnetroMs.toFixed(0)} ms`;

  if (data.target?.ok) {
    const bytes = Uint8Array.from(atob(data.target.image), c => c.charCodeAt(0));
    download.href = URL.createObjectURL(new Blob([bytes]));
    download.hidden = false;
  }
};

compileButton.onclick = () => {
  worker.postMessage({ id: nextId++, source: source.value, optimize: true, targetId: 'bbcmicro' });
};
