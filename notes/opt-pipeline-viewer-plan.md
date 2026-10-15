# Opt Pipeline viewer plan

A Godbolt-style "Opt Pipeline Viewer" panel for the web playground (`src/DotNetro.WebPlayground`): a list of every Irie pass on the left, and on the right the MIR after the selected pass, shown as a diff against the MIR after the previous pass. Passes that did not change the MIR can be filtered out. The panel is opened from a new `+` menu in the disassembly panel's toolbar, which also offers the emulator.

This builds on the deferred `OptPipelinePanel` item in `notes/web-playground-plan.md`.

## Decisions (agreed)

- **Scope of a view:** the whole module's MIR, as `MirModule.Write` prints it (globals, then every function: user code, the runtime and corelib). There is no per-function selection.
- **Diff:** Monaco's built-in diff editor, inline (unified) by default with a toggle for side-by-side. Unchanged regions are collapsed by default (Monaco's `hideUnchangedRegions`), with a toggle to show the full module.
- **Filtering:** "Hide unchanged passes" (default on), where unchanged means the module text is identical to the previous pass's, plus a pass-name search box. No normalisation of vreg numbering or whitespace, and no hand-maintained list of verifier/analysis passes.
- **Final stage:** MIR passes only. The emitted assembly stays in the disassembly panel.
- **Snapshots are captured on demand,** only while a pipeline panel exists downstream of a disassembly panel.
- **Panel plumbing:** the viewer is a downstream panel of a disassembly panel (`upstreamId`), like the emulator, and consumes the `TargetResult` the disassembly panel publishes.
- **Highlighting:** a small Monarch tokenizer for MIR text, used by the diff editor.

## What I found in the repo (so this plan needs no re-exploration)

### Compiler side (already in place)

- `src/Irie/Passes/PassManager.cs`: `AfterEachPass` is an `Action<Pass, CompilationContext>?` invoked after each pass. `PipelineBuilder.AddStandardPipeline` (`src/Irie/Passes/PipelineBuilder.cs`) adds roughly 40 passes in order; some target-supplied passes are added through `target.Add*Passes(passMgr)`. The same pass can appear more than once (`FrameLoweringPass` runs twice, and `PostRegisterAllocationVerifierPass` twice), so pass **names are not unique**; always identify a pass by its index.
- `src/DotNetro.Compiler/CompilerDriver.cs` (`Compile(CompileRequest)`): when `CompileRequest.CaptureSnapshots` is true, it sets `passMgr.AfterEachPass` to write the whole module with `module.Write(writer, target.RegisterInfo.GetRegisterName)` and appends `new PassSnapshot(pass.Name, writer.ToString())`. The list comes back as `CompilationResult.PassSnapshots` (`IReadOnlyList<PassSnapshot>?`, null unless requested). `PassSnapshot` is `record PassSnapshot(string PassName, string MirText)` in the same file. There is **no snapshot of the module before the first pass**, so the first pass has nothing to diff against.
- `src/DotNetro.Compiler.Tests/CompilerDriverTests.cs`: `CaptureSnapshotsRecordsMirAfterEachPass` (line ~73) asserts snapshots are non-empty and every one has non-empty `PassName` and `MirText`; another test (line ~36) asserts `PassSnapshots` is null by default.
- `Irie.Tools.Compiler` (`iriec`) has its own `--print-after-all` / `--print-changed` implementation in `src/Irie.Tools.Compiler/Program.cs` (line ~70). It is independent of the playground path and does not need to change.

### Playground side (already in place)

- `src/DotNetro.WebPlayground/Playground.cs`: `CompileForTarget(byte[] assembly, string targetId, bool captureSnapshots)` already forwards the flag and returns `TargetResult` with `PassSnapshotInfo[]? PassSnapshots` (`record PassSnapshotInfo(string PassName, string MirText)`). `PlaygroundJson` is a source-generated `JsonSerializerContext` with camelCase naming and `DefaultIgnoreCondition = WhenWritingNull`, so a null property is omitted from the JSON.
- `ClientApp/src/protocol.ts`: `PassSnapshot { passName; mirText }` and `TargetResult.passSnapshots?`; the worker request already carries `snapshots: boolean`. `ClientApp/src/compileClient.ts`: `compileForTarget(key, assembly, targetId, snapshots = false)`. Nothing in the UI sets `snapshots` yet.
- `ClientApp/src/store.ts`: `Store` holds the latest `PanelResult` per panel id and the panel graph. `registerPanel({ id, type, upstreamId, focus })`, `subscribe(id, cb)` (calls back immediately if a result exists), `getPanels()`, `onPanelsChanged(cb)`, `newId(prefix)` (per-prefix numbering: `s`, `d`, `e`, `p` are used).
- `ClientApp/src/panels/disassemblyPanel.ts`: toolbar with a target `<select>` and a `+ Emulator` button (`addEmulator`) that calls `ctx.addPanel(EMULATOR_PANELS[kind], { id: store.newId('e'), upstreamId: state.id, options: {} }, 'Emulator')`. `recompile()` calls `ctx.client.compileForTarget(this.state.id, assembly, targetId)` (snapshots currently never requested) and publishes `{ kind: 'target', targetId, result }`.
- `ClientApp/src/panels/emulatorPanel.ts` and `problemsPanel.ts` are the templates for a downstream panel: `container.element.append(root)`, `ctx.store.registerPanel(...)`, `container.setState(state)`, title via a helper in `context.ts` that is refreshed on `store.onPanelsChanged` (the upstream may register after the panel when a layout is restored), subscribe to the upstream, dispose everything in `container.on('beforeComponentRelease')`.
- `ClientApp/src/panels/context.ts`: panel type constants (`SOURCE`, `DISASSEMBLY`, `PROBLEMS`, `EMULATOR_BBC`), `PanelState { id, upstreamId?, options }`, title helpers (`sourceTitle`, `compilerLabel`, `disassemblyTitle`, `emulatorTitle`), and the `el(tag, className?, text?)` DOM helper.
- `ClientApp/src/main.ts`: `layout.registerComponentFactoryFunction(<type>, factory(<Panel class>))` per panel type, and the known-type set passed to `sanitizeLayout` (`new Set([SOURCE, DISASSEMBLY, EMULATOR_BBC, PROBLEMS])`). `ctx.addPanel` opens a new stack at the end of the top row. Layout/persistence (`persistence.ts`) saves every panel's `PanelState` as Golden Layout `componentState`, so anything in `options` is automatically shared in permalinks; an unknown panel type is dropped by `sanitizeLayout`.
- `ClientApp/src/monaco.ts`: registers the `asm6502` language with a Monarch tokenizer and exports `monaco` (the runtime API, via an alias import) and `Monaco` (the types). `monacoTheme()` is in `theme.ts`. Monaco is `^0.57.0`, whose diff editor supports `renderSideBySide` and `hideUnchangedRegions`.
- `ClientApp/src/app.css`: shared `.panel`, `.toolbar`, `.statusbar`, `.editor-host` classes; theme variables `--bar`, `--border`, `--accent`; dark mode via `:root[data-theme='dark']`.
- There is no TypeScript test runner in `ClientApp`. WP4/WP5 verified client logic with throwaway Node scripts and a headless Chromium (Playwright) run.

### MIR text format (for the tokenizer)

Written by `src/Irie/Mir/Writing/MirWriter.cs`; `;` starts a line comment.

```
global @counter : i16
global @g : i16 = bytes "\x00\x00"
global @x : i32 = [bytes "AB", @foo]

func @add_zero : (i16) -> i16 {
  frame_slot 0 : i8 @slot0 stackid 1 offset 2
bb0(%0 : i16):
    %1 : i16 = arith.constant 0
    %2 : i16 = arith.addi %0, %1
    core.return %2
}
```

After instruction selection and RA: `bb0():`, `[liveins: $a, $x]`, physregs (`$a`, `$x`, `$zp2`), opcodes such as `mos6502.lda.imm`, symbols `@name`, and `cf.br bb1(%v)` block targets.

## Design

### Snapshots

`PassSnapshot` becomes `(string PassName, string? MirText)`, where `MirText` is **null when the module text is identical to the previous snapshot's**. This keeps the payload small (most passes change little) without any per-function bookkeeping, and the client reconstructs the full sequence by carrying the previous text forward. A new first snapshot named `Translation` is recorded before any pass runs, so the first pass has a baseline; it always has text.

### Client model

A pure module, `ClientApp/src/panels/pipelineRows.ts`, with no DOM or Monaco dependency:

```ts
interface PipelineRow {
  index: number;          // position in the snapshot list; the identity of a row (names repeat)
  name: string;
  changed: boolean;       // mirText was present (always true for the Translation baseline)
  before?: string;        // module text after the previous row (undefined for the baseline)
  after: string;          // module text after this row
}
function buildRows(snapshots: PassSnapshot[]): PipelineRow[];
```

The visible list is `rows.filter(r => !hideUnchanged || r.changed).filter(matches(search))`. For a selected row the diff is `before` → `after`. Because a hidden pass produced the same text as its predecessor, the "before" of a visible pass is always the output of the pass just before it, hidden or not.

### Panel UI

```
┌ toolbar: [x] Hide unchanged   [ ] Side by side   [ ] Show full module   [search…] ┐
├────────────────────┬───────────────────────────────────────────────────────────────┤
│ 0 Translation      │  Monaco diff editor (read-only, language irie-mir)            │
│ 1 ArithSimplify  ● │  original = MIR after the previous pass                       │
│ 2 HoistCommon…   ● │  modified = MIR after the selected pass                       │
│ …                  │  unchanged regions collapsed unless "Show full module"        │
├────────────────────┴───────────────────────────────────────────────────────────────┤
│ status: "42 passes, 17 changed the MIR"                                            │
└────────────────────────────────────────────────────────────────────────────────────┘
```

- The list is keyboard- and click-selectable. Changed passes are drawn in the accent/green style, unchanged ones dimmed (visible only when the filter is off), as in Godbolt. The baseline row is labelled `Translation`.
- With no snapshots (no successful compile yet, or the compile failed), the panel shows a short message and keeps the last good rows, like the disassembly panel keeps its last good output.
- The selected pass is remembered by `index` within a compile but persisted by **name** in `options.pass`, together with the toggles (`hideUnchanged`, `sideBySide`, `showFull`); the search text is not persisted. After a new compile, the panel keeps the previous selection if a row with the same name and ordinal-among-same-named exists, otherwise selects the first changed row after the baseline.

### Requesting snapshots

The disassembly panel passes `snapshots = true` to `compileForTarget` exactly when at least one panel of type `OPT_PIPELINE` has it as `upstreamId`. It listens to `store.onPanelsChanged` and recompiles when that condition goes from false to true (the new viewer would otherwise have nothing to show until the next edit). When the last viewer closes, nothing is recompiled; the next compile simply omits snapshots.

### The `+` menu

`+ Emulator` becomes a single `+` button that opens a small popup menu with `Emulator` and `Opt Pipeline`. Items are defined in a table in `context.ts` (label, panel type, id prefix, title, whether available for the current target), so later panel types are one entry each. `Emulator` is disabled when the target has no emulator panel (`EMULATOR_PANELS[kind]` undefined). The menu closes on selection, outside click or Escape. Multiple viewers per disassembly panel are allowed.

## Work packages

### OP1: capture the baseline and sparse snapshots (compiler)

1. `CompilerDriver.Compile(CompileRequest)`: when `CaptureSnapshots`, write the module once before `passMgr.Run` and add `new PassSnapshot("Translation", text)`. In `AfterEachPass`, keep the previous text; add `new PassSnapshot(pass.Name, text == previous ? null : text)` and update `previous`. Change the record to `PassSnapshot(string PassName, string? MirText)` and update the XML docs (`CaptureSnapshots` param and `PassSnapshots` property) to describe the null-means-unchanged convention.
2. `Playground.cs`: `PassSnapshotInfo(string PassName, string? MirText)`. No serializer-context change is needed (`WhenWritingNull` omits the property). Update the `CompileForTarget` doc comment.
3. `CompilerDriverTests`: update `CaptureSnapshotsRecordsMirAfterEachPass` so it asserts: the first snapshot is `Translation` with non-empty text; every `PassName` is non-empty; at least one later snapshot has text and at least one has `null`; and every snapshot after the first with text differs from the nearest earlier snapshot with text. Keep the "null by default" assertion.
4. `dotnet test --project src/DotNetro.Compiler.Tests`.

### OP2: client types, row model and MIR language

1. `protocol.ts`: `PassSnapshot { passName: string; mirText?: string }`.
2. `panels/pipelineRows.ts`: `buildRows` as above (carry-forward of text, `changed` flag, baseline row). Verify with a throwaway Node script covering: all-changed, runs of unchanged passes, repeated pass names (distinct by index), and the baseline (no `before`).
3. `monaco.ts`: register `MIR_LANGUAGE = 'irie-mir'` with a Monarch tokenizer. Tokens: `;` comments; `%123` virtual registers; `$a`/`$zp2` physical registers; `@name` symbols; `bb12` / `bb12:` block labels; `dialect.op.suffix` opcodes (the identifier before the first space following ` = ` or at line start after indentation, e.g. `arith.addi`, `mos6502.lda.imm`); type names (`i8`, `i16`, `i32`, `i64`, `ptr`); numbers (decimal and hex); keywords `func`, `global`, `frame_slot`, `bytes`, `stackid`, `offset`, `liveins`; string literals for `bytes "…"`; `->`, `:`, `=`, brackets, braces and parentheses as delimiters. Keep the style of the existing `asm6502` registration.
4. `app.css`: a few additions only (see OP4); reuse theme variables.

### OP3: the Opt Pipeline panel

1. `panels/context.ts`: constant `OPT_PIPELINE = 'opt-pipeline'`, title helper `pipelineTitle(store, id, upstreamId)` in the style of `emulatorTitle` ("Opt Pipeline #1 (C# Editor #1, Compiler #1)"), and the `+` menu item table (OP4 consumes it).
2. `panels/optPipelinePanel.ts`: `OptPipelinePanel(container, state, ctx)` following the emulator/problems panel structure:
   - builds the toolbar (toggles and search), the pass list and a `monaco.editor.createDiffEditor` (`readOnly: true`, `automaticLayout: true`, `renderSideBySide` from options, `hideUnchangedRegions: { enabled: !showFull }`, `originalEditable: false`, `minimap: { enabled: false }`, theme from `monacoTheme()`), with `original` and `modified` models in `MIR_LANGUAGE`;
   - registers with the store (`type: OPT_PIPELINE`, `upstreamId`, `focus`), `container.setState`, a title refreshed on `store.onPanelsChanged`;
   - subscribes to `state.upstreamId`; on a `target` result with `passSnapshots`, rebuilds rows and applies the selection rule above; on a result without snapshots, keeps the last rows and shows a status message;
   - on selection, calls `setModel`/updates the two models' text rather than creating models per click, and disposes both models and the editor on release (plus unsubscribes and `store.unregisterPanel`).
3. `main.ts`: register the factory for `OPT_PIPELINE` and add it to the known-type set for `sanitizeLayout`.
4. Check restoring a saved layout where the viewer registers before or after its disassembly panel (the second case must still request snapshots; see OP4).

### OP4: the `+` menu and on-demand snapshots (disassembly panel)

1. `disassemblyPanel.ts`: replace `addEmulator` with the `+` button and menu built from the item table. The menu is a small absolutely-positioned popup anchored to the button (`.menu`, `.menu-item`, `.menu-item[disabled]`), closed on outside click or Escape; remove the document listeners when it closes or the panel is released.
2. Compute `wantsSnapshots()` as `store.getPanels().some(p => p.type === OPT_PIPELINE && p.upstreamId === state.id)`; pass it as the `snapshots` argument in `recompile()`. Subscribe to `store.onPanelsChanged` (disposed with the panel) and call `recompile()` when `wantsSnapshots()` changes from false to true.
3. Manual verification in headless Chromium (as WP4 did): Hello World compiles; `+` → Opt Pipeline opens a viewer that fills in; edit the source and the viewer updates and keeps its selection; toggling the filter hides unchanged passes; the diff of a known-changing pass (e.g. the legalizer) shows removed and added lines; closing the viewer and editing no longer requests snapshots; reload from the permalink restores the viewer and its options.

### OP5: payload and performance check, docs

1. Measure, for Hello World and for the largest lit-test program that compiles, the size of `passSnapshots` in the JSON (sum of `mirText` lengths), the time of `JSON.parse`, and Monaco's time to diff a whole-module pair. Record the numbers in "Results" below.
2. If the payload or diff time is a problem, the knobs are: Monaco diff `maxComputationTimeMs`, and sending snapshots only after the viewer is first opened (already the case). Anything that narrows the module (for example dropping runtime functions) is a scope change and must be agreed first.
3. Update `notes/web-playground-plan.md` ("Deferred" bullet) to point here, and the `DotNetro.WebPlayground` bullet in `CLAUDE.md` to mention the Opt Pipeline panel and the on-demand `CaptureSnapshots` path.
4. `dotnet test --solution src` in Debug and Release; `npm run build` in `ClientApp`.

## Risks and open items

- **Payload size.** Whole-module text for about 40 passes is large (the module includes the runtime and corelib). The sparse encoding removes the duplicates; the measurement in OP5 decides whether anything more is needed.
- **Monaco diff cost** on very large modules. Collapsing unchanged regions helps rendering but the diff is still computed over the full text; `maxComputationTimeMs` bounds it at the price of a coarser diff.
- **Failed compiles lose snapshots.** If a pass throws, `Playground.CompileForTarget` returns only the internal-error diagnostic, so there is nothing to inspect for the failing pass. Returning the snapshots captured so far would be useful for debugging compiler bugs, but it changes the failure path and is out of scope here.
- **Globals.** Passes that only change globals (for example frame-slot storage) show up as changed because the diff is over the whole module, so there is no missing-information problem, but the change may be far from where a reader is looking; the collapsed-region view shows it.
- **Duplicate pass names.** Rows are keyed by index, never by name; the persisted selection uses name plus ordinal among same-named passes, so reordering the pipeline in a later release degrades to the default selection rather than erroring.

## Results

_(fill in during OP5)_
