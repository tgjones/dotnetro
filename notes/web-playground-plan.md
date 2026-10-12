# Web playground plan

A Godbolt-style playground at `tgjones.github.io/dotnetro`: C# editor → 6502 disassembly → embedded BBC Micro emulator, all running in the browser. Lives in `src/DotNetro.WebPlayground`.

## Decisions (agreed)

- **UI stack:** thin TS/JS shell (GoldenLayout + Monaco + jsbeeb) owns the layout and all panels. Blazor WebAssembly is used *only* as a compile worker (Roslyn + DotNetro compiler), exposed through `[JSExport]`. No Razor components for panels.
- **Emulator:** jsbeeb for the BBC Micro. One emulator implementation *per system* (no switching between alternates for one system). Future systems each get their own emulator panel type.
- **.NET:** stay on SDK/TFM 10 (`$(DotNetVersion)` = `net10.0`). Do not copy DotNetLab's .NET 11 preview.
- **BCL surface:** compile user code against the real reference assemblies we embed, restricted to a small allow-list. Unsupported APIs are rejected by the DotNetro compiler with diagnostics (see "Reference assemblies").
- **Future direction (not v1):** as the supported IL subset grows we will compile real `System.Private.CoreLib` method bodies rather than hand-write stubs for each BCL method. The plan must not block this (see "Reference and implementation assemblies").
- **Startup:** the page compiles immediately on load, so every asset the first compile needs (including the implementation assemblies) is fetched eagerly at startup, with a progress indicator (see WP3). No lazy-loading of compile-critical assets.
- **Persistence/sharing in v1:** shareable URL encodes compressed source *and* the full layout and per-panel options.
- **Targets:** the target system lives on the disassembly panel, not the editor. One source panel may feed several disassembly panels, each feeding its own emulator panel.
- **Deferred:** MIR view and Opt Pipeline viewer (the design must leave room for them; do not build them in v1).

## What I found in the repo (so this plan needs no re-exploration)

- `src/Directory.Build.props`: `DotNetVersion=net10.0`, nullable and implicit usings on. Solution is `src/DotNetro.slnx` (add the new project there). CI is `.github/workflows/ci.yml` (ubuntu, `dotnet test --solution src` in Debug and Release).
- `src/DotNetro.Compiler/CompilerDriver.cs`: `Compile(string dotNetAssemblyPath, string entryPointMethodName)` hard-codes `new MOS6502BbcMicroTarget()`, builds `IlToMirTranslator(path, target.GetRuntime())`, runs `PipelineBuilder.AddStandardPipeline`, emits MachineCode, encodes with `MOS6502BinaryEncoder`, packages with `target.PackageImage`, and returns `CompilationResult(AssemblyCode, Listing, CompiledProgram, CompiledImage)` (the asm text is in the first field; `Listing` is unused). Errors are thrown as exceptions.
- **File-system dependencies that block WASM:**
  - `EcmaAssembly` (`src/DotNetro.Compiler/EcmaAssembly.cs`) opens `new PEReader(File.OpenRead(assemblyPath))` and uses the path for `GetHashCode`.
  - `TypeSystemContext.ResolveAssembly(string path)` caches by path; `ResolveAssembly(AssemblyName)` (line ~88) does `Assembly.Load(fullName).Location` to find the core library (`mscorlib`/`System.Runtime`) and the `DotNetro` runtime library (`RuntimeAssembly`), both by host-runtime lookup.
  - `IlToMirTranslator` ctor takes `rootAssemblyPath`.
- `MOS6502Target.GetRuntime()` reads `runtime.irie` from an embedded resource, so it already works without a file system.
- `PassManager.AfterEachPass` (`Irie/Passes/PassManager.cs`) is an existing per-pass hook, which is the natural place to capture MIR snapshots for the future MIR/Opt Pipeline views.
- User programs are compiled against the **real BCL reference assemblies** by `DotNetro.Compiler.Tests.CsCompiler` (refs: `System.Private.CoreLib`/object, `System.Runtime`, `System.Console`; global usings for `System`, `System.Collections.Generic`, `System.IO`, `System.Threading`, `System.Threading.Tasks`; `AllowUnsafe`; Debug or Release optimisation). The translator special-cases BCL calls such as `Console.WriteLine` and routes them to `DotNetro.Runtime.ConsoleHelper`/`runtime.irie`. `src/DotNetro` (the runtime library) holds `ConsoleHelper`, `ManagedHeap`, `MathHelper`.
- The existing emulator (`DotNetro.Compiler.Tests.Emulator`, Aemula 6502 core with stubbed OSWRCH/OSASCI/OSWORD) has no video or keyboard. It stays as the test harness; it is not used by the playground.
- Reference project: `/Users/timjones/Code/DotNetLab` (`src/Worker`, `src/WorkerWebAssembly`, `MetadataReferenceService.BlazorWasm`). Borrow the worker split and the reference-assembly delivery approach, not the Blazor UI.

## Architecture

```
Main thread: TS shell (Vite)
  GoldenLayout host + panel registry
   ├─ SourcePanel        Monaco (C#); owns text; emits "source changed"
   ├─ DisassemblyPanel   Monaco (read-only asm); setting: target id; subscribes to a SourcePanel; publishes CompileResult
   │    └─ (later) MirPanel, OptPipelinePanel   subscribe to a DisassemblyPanel
   └─ EmulatorPanel      one type per system (BbcMicroEmulatorPanel, jsbeeb); subscribes to a DisassemblyPanel; consumes image bytes
  Persistence: state ⇄ URL hash

Web Worker: Blazor WASM (DotNetro.WebPlayground)
  [JSExport] Compile(source, options) → IL dll bytes (Roslyn, cached per source+options)
  [JSExport] CompileForTarget(dllBytes, targetId, options) → CompileResult
  [JSExport] GetTargets() → TargetInfo[]
```

### Panel dependency graph

Each panel has a stable `id` and, for non-source panels, an `upstreamId`. A tiny pub/sub store holds `latestResult[panelId]`; a panel recomputes when its upstream result changes. Debounce source edits (~400 ms) and cancel superseded compiles (worker-side generation counter; discard stale results). Creating a second DisassemblyPanel pointing at the same SourcePanel with a different target must "just work", so no panel may assume a unique target. New-panel menu: "Add disassembly (target…)" under a source panel, "Add emulator" under a disassembly panel (emulator type chosen from the target's `emulatorKind`).

### Compile results (the contract between worker and shell)

```
TargetInfo      { id, displayName, emulatorKind }          // "bbcmicro" → "jsbeeb"
CompileResult   { ok, diagnostics[], asm?, image?: byte[], origin?, passSnapshots? }
Diagnostic      { severity, message, line?, column?, source: "roslyn" | "dotnetro" }
```

`passSnapshots` is `{ passName, mirText }[]`, produced via `PassManager.AfterEachPass` only when requested (`options.capturePassSnapshots`), so the common path stays cheap. v1 never sets it but the plumbing exists so the MIR/Opt Pipeline views are additive.

### Reference and implementation assemblies

Two distinct assembly sets, which must not be conflated: reference assemblies have no method bodies, so they serve Roslyn but cannot serve a compiler that translates BCL IL.

- **Compile-time references (Roslyn only):** `System.Runtime` and `System.Console` **reference** assemblies (from the `Microsoft.NETCore.App.Ref` pack, a few tens of KB each) plus the `DotNetro` runtime library. Passed to Roslyn as `MetadataReference`s. Roslyn-emitted user assemblies refer to `System.Runtime`.
- **Link-time implementation assemblies (DotNetro compiler only; spike result: for v1 the `System.Runtime` *reference* assembly is enough as the core library, see WP2):** supplied by the **target**, via the `IAssemblyResolver` (WP1). For v1 this is whatever the translator needs today: the `DotNetro` runtime library plus a core library that resolves `System.Object`, `String` etc. by name. When we start compiling BCL bodies it becomes the implementation `System.Private.CoreLib` plus the small implementation `System.Runtime` facade, so that references to `System.Runtime` follow the type-forwarders (`ExportedType`) into CoreLib. `EcmaAssembly` type resolution must follow forwarders (check whether it already does; it currently relies on the host's facade).
- **Per-target:** stock CoreLib assumes a 32/64-bit platform with platform `#if` code. Eventually a custom or trimmed CoreLib build for 16-bit targets is likely (as NativeAOT has its own corelib). So the implementation set comes from `TargetInfo`/the target registry, not from a global constant. Cache by target id.
- **Eager loading:** because the page compiles immediately on load, the implementation assemblies are fetched at startup alongside the runtime and Roslyn, not lazily. They count towards the startup progress total. A later build-time trimming step (stripping types unreachable from the supported surface) can shrink CoreLib once it is large.
- The reference allow-list is one manifest (`references.json` or a `ReferenceSet` class). Growing BCL support = add a reference + add translator support; nothing else changes.
- The "tiny subset" is therefore *not* a hand-built facade: the real reference surface is exposed, and the compiler rejects anything it cannot lower (`DotNetro` diagnostic with the offending method). If we later want unsupported APIs flagged in the editor, add a Roslyn analyzer that reads a supported-API list; out of scope for v1.
- **Spike item:** confirm which link-time core library the translator needs in v1. It currently resolves the host's `mscorlib` implementation. Test both (a) the reference `System.Runtime` as core and (b) a trimmed implementation `System.Private.CoreLib` + `System.Runtime` facade, and record sizes. (b) is the direction of travel, so prefer it if (a) is merely a stopgap.

## Work packages

### WP1: make the compiler byte-based and target-pluggable (independent, testable, no UI) — DONE (uncommitted, pending review)

Implemented as planned, with these deviations/findings:

- `EcmaAssembly` has only the `(context, name, byte[] image)` ctor; the path wrapper lives on `TypeSystemContext.ResolveAssembly(string path)`. Assembly identity is the simple name read from the image's own metadata, so `mscorlib`/`System.Runtime` → `System.Private.CoreLib` forwarding yields one `EcmaAssembly` (type identity preserved).
- `CoreAssembly` now asks the resolver for `System.Private.CoreLib` (was `mscorlib`), i.e. direction (b) of the spike item. Type-forwarders (`ExportedType`) were already followed by `EcmaAssembly.GetTypeImpl`.
- `IAssemblyResolver` (`HostAssemblyResolver` for dnrc/tests, `InMemoryAssemblyResolver` name→bytes for the playground) is in `AssemblyResolver.cs`.
- `Compile(CompileRequest)` returns the existing `CompilationResult`, extended with `Origin`, `Diagnostics`, `PassSnapshots`, `Succeeded` (one result type rather than a second `CompileResult`). Only `NotSupportedException` becomes a diagnostic (`source: "dotnetro"`, no line/column yet); other exceptions are compiler bugs and still throw. `Compile(path, entry)` is unchanged for callers.
- `TargetRegistry` (`TargetDescriptor`) also carries the chip's `Encode`/`WriteAssembly` delegates so `CompilerDriver` has no MOS6502 types left; `GetRuntime()` moved up to the Irie `Target` base class.
- **WP2 finding:** the translator resolves the declaring type of every call, so the link-time set must include `System.Console` (and the `System.Runtime` facade) as well as CoreLib and `DotNetro`. `TargetDescriptor.ImplementationAssemblies` lists `System.Private.CoreLib`, `System.Runtime`, `System.Console`, `DotNetro`. A `System.Console` *reference* assembly is probably enough (only the type/method signature is looked up); confirm in WP2.
- Tests: `CompilerDriverTests` (in-memory vs path-based byte-identical output, unsupported-construct diagnostic, snapshots, unknown target). `dotnet test --solution src` passes in Debug and Release.

1. `EcmaAssembly`: add a ctor taking `(TypeSystemContext, string name, byte[] image)`; keep the path ctor as a thin wrapper that reads the file. Key identity by name, not path.
2. `TypeSystemContext`: introduce `IAssemblyResolver { byte[]? Resolve(AssemblyName) }` (and a by-path convenience). Default implementation keeps today's `Assembly.Load(...).Location` behaviour for dnrc/tests; the playground supplies an in-memory resolver (name → bytes) for the target's implementation assemblies (core library, `DotNetro`, later CoreLib). The resolver is obtained from the target (`TargetRegistry` entry exposes the implementation-assembly list), and must follow `ExportedType` type-forwarders.
3. `IlToMirTranslator`: accept root assembly bytes plus the resolver. Keep the path ctor.
4. New entry point in `CompilerDriver`: `Compile(CompileRequest)` where `CompileRequest { byte[] Assembly, IAssemblyResolver Resolver, string TargetId, string EntryPoint, bool CaptureSnapshots }`, returning `CompileResult` with `Diagnostics` instead of throwing for expected failures (unsupported opcode/method). Keep the old `Compile(path, entry)` as a wrapper so `dnrc` and all lit tests are unchanged.
5. `TargetRegistry` (in `DotNetro.Compiler`): `id → { factory, displayName, emulatorKind, implementationAssemblies }`, initially `bbcmicro` only. Remove the hard-coded `MOS6502BbcMicroTarget` from `CompilerDriver`, and add `CompilationResult.Origin`.
6. Wire `AfterEachPass` snapshot capture behind the request flag.
7. Tests: unit test compiling an in-memory dll through the new entry point (assert asm and image match the path-based result); run `dotnet test --solution src` in Debug and Release.

### WP2: WASM spike (decide go/no-go on size and speed) — DONE (uncommitted, pending review)

**Verdict: go.** C# source → Roslyn → IL → DotNetro → asm + `.ssd` runs in WebAssembly, and the `.ssd` bytes are identical to native `dnrc` output (checked for HelloWorld and CalliDispatchTable). Repeated compiles in one process work (no static-state problems seen over 3 runs each).

**What exists:** `src/DotNetro.WebPlayground` (in `DotNetro.slnx`): `Playground` `[JSExport]` surface (`GetTargets`, `CompileCSharp(source, optimize)`, `CompileForTarget(byte[] dll, targetId, captureSnapshots)`, JSON results with base64 binaries), `RoslynCompiler`, `AssemblySet`; `wwwroot/{index.html,main.js,worker.js}` (Web Worker harness); `spike/measure.mjs` (Node measurement script, usage in its header).

**Deviations/decisions:**
- The project uses `Microsoft.NET.Sdk.WebAssembly` (the SDK underneath Blazor WebAssembly) rather than the Blazor WebAssembly SDK: there are no Razor components, so no `blazor.*`/ASP.NET payload. WebcIL is off (`WasmEnableWebcil=false`) so framework assemblies are plain PE files; `SatelliteResourceLanguages=en`; `InvariantGlobalization`.
- **Link-time core library = the `System.Runtime` *reference* assembly (option a).** Registered under the name the translator asks for (`System.Private.CoreLib`); it defines `System.Object`/`String`, which is all the translator needs. Verified natively against **every** lit test program: the image is byte-identical to the host-CoreLib build (only `LongBranches.cs` could not be compared: its host compile fails with `BadImageFormatException` regardless). Cost: 187 KiB Brotli, versus 4.4 MiB Brotli (17 MB raw) for the host `System.Private.CoreLib` (option b), so (b) is not viable until a trimmed CoreLib exists. The target manifest (`TargetDescriptor.ImplementationAssemblies`) is the single place to switch later.
- The three non-framework assemblies (`System.Runtime` and `System.Console` reference assemblies from the targeting pack, plus `DotNetro.dll`) are **embedded resources** of the playground assembly in this spike (MSBuild target `EmbedPlaygroundAssemblies`), so there is no fetch step. The same `System.Runtime`/`System.Console` reference assemblies feed Roslyn and the DotNetro compiler. WP3 may move them to fetched files if the byte-counting progress bar needs them separate.
- Roslyn runs with `WithConcurrentBuild(false)` (no threads in WASM). Results are cached per (optimize, source), 16 entries.
- Trimming: the default publish trimming (already `TrimMode=full`) worked unmodified with Roslyn. No AOT.

**Measurements** (Release publish; Brotli sizes are the `.br` files the publish emits; timings are Node 26 on an M-series Mac with files read from local disk, so they exclude network; Node and Chrome share V8 and the same Mono WASM runtime):

| Payload | Brotli | Raw |
| --- | --- | --- |
| .NET runtime (`dotnet.*`) | 1043 KiB | 3316 KiB |
| BCL (CoreLib, Xml, Regex, ... pulled in by Roslyn) | 1162 KiB | 3538 KiB |
| Roslyn | 1882 KiB | 6434 KiB |
| Irie + DotNetro.Compiler + playground (incl. 3 embedded assemblies) | 338 KiB | 1316 KiB |
| **Total** | **4425 KiB** (~4.3 MiB) | 14602 KiB |

| Step | Latency |
| --- | --- |
| Runtime boot (`dotnet.create()` + exports) | ~120 ms |
| First compile, HelloWorld (cold: Roslyn 730 ms + DotNetro 200 ms) | ~930 ms |
| First compile, CalliDispatchTable (warm Roslyn) | Roslyn 115 ms + DotNetro 325 ms |
| Warm HelloWorld | Roslyn ~15-20 ms + DotNetro ~60 ms |
| Warm CalliDispatchTable | Roslyn ~30 ms + DotNetro ~280 ms |

So cold start is ~4.3 MiB download plus ~1.1 s of boot and warm-up; edits recompile in well under half a second. The browser harness (`wwwroot`) is **untested**: there is no browser in this environment, only the Node measurement was run. To try it: `dotnet publish src/DotNetro.WebPlayground -c Release -o /tmp/dnpub && python3 -m http.server -d /tmp/dnpub/wwwroot`.

**Follow-ups for WP3+:** the BCL slice is large for what Roslyn needs (Xml, Regex, Http are pulled in by Roslyn's dependency graph); try excluding/stubbing them if download size matters. `CompileForTarget` JSON currently returns the image as base64; switch to a `byte[]` return if the emulator panel wants it without the copy.

1. Create `src/DotNetro.WebPlayground` (Blazor WebAssembly standalone, no UI, `[JSExport]` surface only; `net10.0`; add to `DotNetro.slnx`).
2. Implement `Compile(source)` using Roslyn (`Microsoft.CodeAnalysis.CSharp`, in-memory emit, references loaded from fetched bytes) and `CompileForTarget`. Mirror `CsCompiler` options (global usings, AllowUnsafe, console app, Debug/Release toggle).
3. Run in a Web Worker. Measure: download size (Brotli, split into runtime / Roslyn / implementation assemblies), cold start to first compile result, compile latency for `HelloWorld` and for `CalliDispatchTable.cs`.
4. Resolve the reference-assembly spike item above. Decide on trimming/AOT (`InvariantGlobalization`, `TrimMode`, no AOT initially since Roslyn trimming is risky).
5. Exit criteria: C# string → asm text and `.ssd` bytes in the browser, with sizes recorded in this file.

### WP3: UI shell — DONE except items noted (uncommitted, pending review)

Implemented in `src/DotNetro.WebPlayground/ClientApp` (Vite 8, TypeScript, golden-layout 2.6, monaco-editor 0.57). Verified in headless Chromium (Playwright, installed outside the repo): boot, first compile, overlay stages, Roslyn error markers, second disassembly panel; the browser run also validated the WP2 harness. `npm run build` type-checks and builds; `dist` needs `DOTNET_PUBLISH_DIR` (default `../bin/Release/net10.0/publish/wwwroot`) because the Vite plugin copies/serves the published `_framework` and writes `framework-manifest.json` (raw file sizes for the progress bar). `npm run dev` serves `_framework` from the same directory.

- **Layout of the code:** `store.ts` (pub/sub, panel graph with `upstreamId`), `compileClient.ts` (worker client; per-key generation numbers, superseded requests resolve `null`), `worker/compile.worker.ts` (boots .NET with a counting resource loader; drains its queue from a timer and drops superseded requests before they run), `panels/{source,disassembly,emulator}Panel.ts`, `monaco.ts` (+ generated `monacoFeatures.ts`: every editor feature, C# only, no TS/CSS/JSON services), `theme.ts`, `overlay.ts`, `public/sw.js`.
- **Panels:** Source (Monaco C#, debounced 400 ms, "Release IL" checkbox, `+ Disassembly`), Disassembly (read-only Monaco with a `asm6502` Monarch tokenizer, target dropdown, `+ Emulator`; any number per source, each its own target), Emulator (placeholder consuming the real image/origin contract plus a `.ssd` download; WP4 replaces it; `EMULATOR_PANELS` maps `emulatorKind` → panel type). Roslyn diagnostics show as Monaco markers with their position; DotNetro diagnostics (no location yet) pin to line 1. New panels open in their own stack (`addComponentAtLocation`, `FirstRow`).
- **Startup:** static overlay in `index.html` (paints before JS), determinate bar from the manifest, stages *Downloading runtime → Downloading compiler → Initialising Roslyn → Compiling…*, fades when the first compile result exists, error state with Retry. Service worker `sw.js`: cache-first for `_framework/` (fingerprinted names), registered in production only. Cold load from `python3 -m http.server` on localhost: overlay gone ~2 s.
- **Gotchas found:** (1) the .NET runtime owns `self.onmessage` in a worker: assigning it makes `create()` hang forever with no error, so the worker uses `addEventListener`. (2) Monaco 0.57's package `exports` cannot address `.css`, hence the `monaco-vs` alias to `esm/vs`. (3) `LocationSelector.TypeId` is a `const enum` (unusable with `isolatedModules`), so the numeric value is used.
- **Not done / follow-ups:** theming covers Monaco, Golden Layout and the app chrome via `prefers-color-scheme` but dark mode was not eyeballed; main JS chunk is ~4 MB (1 MiB gzip) because of Monaco's features, which could be code-split; `fflate` is not added yet (WP5); the source panel's `Release IL` option is per panel but there is no per-disassembly optimisation option yet.

1. `src/DotNetro.WebPlayground/ClientApp` (Vite + TypeScript). Dependencies: `golden-layout`, `monaco-editor` (C# language, a custom `6502 asm` Monarch tokenizer), `lz-string` or `fflate` for URL compression.
2. Panel registry and pub/sub store (see "Panel dependency graph"); a worker client with request cancellation.
3. Source, Disassembly panels with diagnostics shown as Monaco markers (Roslyn and DotNetro diagnostics) and a target dropdown on the disassembly panel header.
4. Default layout: Source | Disassembly (BBC Micro) | Emulator.
5. **Startup progress indicator** (as DotNetLab does): the shell shows a full-page overlay from first paint until the first compile result is ready, then fades it out. Because the worker hosts Blazor itself, drive the .NET runtime from the worker (`dotnet.js` `create()` with a custom resource loader) so we can count bytes: fetch every boot resource via `fetch` + `ReadableStream`, sum `loaded/total` against the boot manifest's known sizes, and `postMessage({stage, loaded, total})` to the shell. Show a determinate bar plus a stage label: *Downloading runtime → Downloading compiler → Initialising Roslyn → Compiling…*. The final stage covers the first compile (which also warms the JIT), so the overlay does not drop until real output appears. Show an error state with a retry button if a fetch fails. Make the page shell (layout skeleton and overlay) pure static HTML/CSS in `index.html` so it paints before any JS runs. Add a service worker (cache-first for `_framework` and the implementation assemblies, versioned) so repeat visits skip the download. Keep the progress UI working without the service worker.
6. Theming: follow `prefers-color-scheme`; Monaco and GoldenLayout themes match.

### WP4: BBC Micro emulator panel (jsbeeb)

1. Vendor or npm-depend on jsbeeb; **verify ROM licensing** (MOS, BASIC) before bundling. If jsbeeb's ROMs cannot be redistributed from our Pages site, load them from the jsbeeb-hosted location at runtime or document how to supply them. Treat this as a go/no-go check at the start of WP4.
2. `BbcMicroEmulatorPanel`: on a new `CompileResult.image`, load the `.ssd` into drive 0 and run it (`*RUN MAIN`/auto-boot via `*TAPE`-free command injection); provide Reset, Pause and Run controls. Keyboard focus only while the panel is active.
3. Pause the emulator when the panel is hidden (GoldenLayout tab not active).
4. The panel interface (`EmulatorPanel { loadImage(bytes, origin) }`) stays per system; registered in a map keyed by `emulatorKind`.

### WP5: persistence and sharing

1. State schema (versioned, `v: 1`): `{ v, layout: GoldenLayout config, panels: { [id]: { type, upstreamId?, options } }, source: { [id]: text } }`. Options include target id, optimisation level, and any per-panel view settings.
2. Serialisation: JSON → deflate (fflate) → base64url in `location.hash`. Update with `history.replaceState` (debounced); load on startup with a fallback to the default layout. Guard against oversize URLs (warn above ~8 KB; add a "copy link" button; future: gist import).
3. Unknown or removed panel types or targets in old links must degrade gracefully (drop the panel, keep the rest).
4. Persist the last state in `localStorage` as a convenience, with the hash taking precedence.

### WP6: GitHub Pages deployment

1. Workflow `.github/workflows/playground.yml`: on push to `main` (and a build-only job on pull requests that touch `src/DotNetro.WebPlayground`, `src/DotNetro.Compiler`, `src/Irie`), steps: checkout (with LFS, as in ci.yml), setup-dotnet 10, `dotnet publish src/DotNetro.WebPlayground -c Release`, `npm ci && npm run build` in `ClientApp`, assemble the site, `actions/upload-pages-artifact`, `actions/deploy-pages`. Needs the `pages: write` and `id-token: write` permissions and the Pages source set to "GitHub Actions".
2. Base path `/dotnetro/` for both the Vite build (`base`) and the Blazor `<base href>`/`--pathbase`; also `.nojekyll` and a `404.html` fallback copy of `index.html`. All asset URLs relative.
3. Brotli-precompressed `_framework` (the publish output already provides `.br`; check Pages serves them with the right `Content-Encoding`, else configure the loader to decompress).

### Deferred (design must not preclude)

- `MirPanel` (subscribes to a DisassemblyPanel, requests `capturePassSnapshots`, shows MIR after a chosen pass) and `OptPipelinePanel` (per-pass diff view, Godbolt-style).
- Additional targets (e.g. ZX80): add a `TargetRegistry` entry plus an `emulatorKind` and an emulator panel type. No change to the source or disassembly panels.
- An unsupported-API analyzer for the editor.

## Risks and open items

- **Payload size and startup** (Roslyn plus Irie): measured in WP2; mitigate with Brotli, a service worker cache, and a startup progress indicator (WP3). Implementation assemblies are loaded eagerly, not lazily, because the page compiles on load. Go/no-go gate.
- **Link-time core library** choice and type-forwarder resolution in the translator (WP2 spike item). Also whether stock CoreLib is acceptable for 16-bit targets long term (per-target CoreLib).
- **jsbeeb ROM licensing** (WP4 first step).
- **Compiler thread-safety/statics:** the pipeline is run repeatedly in one process; check for static mutable state (e.g. dialect registration via `MirBootstrap.EnsureRegistered`, caches) that misbehaves on repeated or concurrent compiles; the worker serialises compiles so only repeat-safety matters.
- **Roslyn Debug vs Release IL:** the translator supports a different IL subset by optimisation level (see `src/DotNetro/DotNetro.csproj` comment). Default the playground to Release IL, and expose it as a disassembly/source option.
- **GoldenLayout version:** v2 is current; confirm the API used by Godbolt-style add-panel menus before building the shell.
