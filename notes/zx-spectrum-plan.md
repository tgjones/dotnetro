# ZX Spectrum (Z80) target plan

Goal: `dnrc` / the web playground can compile a C# program to Z80 code and produce a ZX Spectrum 48K snapshot
(`.sna`) that runs in an emulator, with `Console` I/O working and the `.cs` lit tests executing on a Z80 emulator.
This is a second Irie target, `z80`, plus a second system, `zxspectrum`, layered on it (the same split as
`mos6502` / `mos6502-bbcmicro`).

Why Spectrum rather than ZX80: the ROM gives us text output (`RST 16`) and keyboard scanning (`LAST-K`) for free,
the character set is near-ASCII, and a `.sna` snapshot is a fixed-size file with no loader. The ZX80 would need a
custom display-file writer, a non-ASCII charset and a program-file format, for a worse emulator story.

## What changed since the first draft (2026-10-15 → 2026-10-20)

The first draft assumed Irie had a generic register allocator (graph colouring over register classes) that a
Z80 backend would simply instantiate. **That allocator no longer exists.** `078809e` made beam placement the
default and deleted the colouring allocator; `PipelineBuilder` now goes shape selection → placement, with no
register classes, phi elimination or two-address pass in between. Since then the pipeline also gained inlining,
value-range analysis, `RangeNarrowing`, `RangeFold`, `ConstantBranchFold` and `UnreachableBlockElimination` (all
generic and free for the Z80), and callee-first placement with per-function conventions (off by default).

The register-allocation design is written up in `doc/irie/register-allocator-design.md`; the 6502 overview is
`src/Irie/Target/MOS6502/README.md`. The consequence for this plan is the new section
[Register allocation for the Z80](#register-allocation-for-the-z80) and a reworked Stage 3 with a new
pre-stage (2b) that lifts the generic parts out of `Target/MOS6502/`.

Stage 0 has largely landed (see below).

## Findings (verified in the repo, 2026-10-20)

### Already target-neutral — reuse as is

- `PipelineBuilder.AddStandardPipeline(passMgr, target)` drives everything through `Target` hooks: `CallLowering`,
  `LegalizerInfo`, `CreateShapeSelector`, `CreateBeamPlacementPass`, `PseudoExpander`, `BranchLowering`,
  `FrameLowering`, `RegisterInfo`, `MachineCodeEmitter`, `GetRuntime()`, `GetLibrary()`, `DefaultOrigin`,
  `PackageImage`, and the `Add*Passes` extension slots (pre-isel, branch folding, post-placement, post-pseudo
  expansion, final, post-tail-duplication).
- Middle end: `ArithSimplify`, `Inlining`, `RangeNarrowing`, `RangeFold`, `ConstantBranchFold`,
  `UnreachableBlockElimination`, `SinglePredecessorBlockParams`, `HoistCommonCode`, `ReturnMerge`. Range
  narrowing is a bigger win on Z80 than it looks: .NET widens to i32, and narrowing to i8/i16 is what keeps
  values in registers.
- Generic legalizer driver + artifact combiner, `FrameLoweringPass`, `FrameAccessLoweringPass`,
  `PrologueEpilogueInsertionPass`, `CopyEliminationPass`, `CopyPropagationPass` (parameterised by
  `TargetRegisterInfo`), `PseudoExpansionPass`, `RegisterScavengingPass`, `PostRegisterAllocationVerifierPass`,
  `TailDuplicationPass`.
- Analyses: `LoopInfo` (natural loops, block frequency `8^depth`), `CallGraph` (SCCs, address-taken functions,
  indirect calls via the trampoline; calls are found through `Dialect.IsCall`), `ReentrancyAnalysis`,
  `LiveIntervalsAnalysis` (physreg-aware), `ValueRangeAnalysis`.
- `StaticFrameColorer`: pure call-graph "static stack" colouring ("a function's block sits above all its
  callees' blocks"); budget and sizes come from the caller. This is the whole-program memory layout engine and
  it is directly usable for Z80 static RAM.
- `PlacementTuning` / `--beam-width` / `--tune` plumbing, `MirFunction.SaveState`/`RestoreState`.
- Dialects (`arith`, `mem`, `cast`, `cf`, `call`, `pseudo`), MIR text/binary formats, the MachineCode layer.
- IL front end, the CoreLib split (`Foo.cs` shared + `Foo.<Target>.cs` platform half), `TargetRegistry` /
  `TargetDescriptor` (already carries `CoreLibId` and `EmulatorKind`), the playground's `EMULATOR_PANELS` map.
- `FrameSlot.StackId` is an opaque per-target id, so a Z80 can add its own frame placements without touching
  generic code.

### 6502-specific and needs generalising or replacing

Stage 0 leftovers (small):

- `Irie.csproj:8` embeds `Target/MOS6502/runtime.irie` as the only runtime resource. (`Target.GetRuntime()` is
  already what the translator calls — `CompilerDriver.cs:55` — so only the embedding is single-target.)
- `EcmaMethod.cs:67` and the translator's `InternalCall` error text (`IlToMirTranslator.cs:2512,2576`) say "the
  `runtime.irie`"; with two runtimes names only need to be unique per target. Comment/message only.
- Comment-vs-behaviour audit, still open: `Dialects/Call/CallOp.cs:32` (trampoline "parks the pointer in
  fixed zero-page slots"), the divide/remainder libcall names in `IlToMirTranslator.cs` ("same as
  `MOS6502LegalizerInfo.LowerLibcall`").
- **`LegalizerPass.NarrowWideBlockParameters` hard-codes 8-bit parts** (`LegalizerPass.cs:178-190`, comment at
  `:155`), independently of `LegalizerInfo`. A Z80 where `i16` is legal needs the maximum legal parameter width
  from the target. This is *behaviour*, not a comment — the first-draft audit list missed it.

Register allocation / placement (the new, bigger item) — see the next section for the full inventory.

### Web emulator options (checked 2026-10-15)

| Option | License | Notes |
|---|---|---|
| JSSpeccy 3 (`gasman/jsspeccy3`) | GPL-3.0 | WASM core in a Worker; 48K/128K/Pentagon; ROMs bundled in `static/roms/`. `runtime/jsspeccy.js` has `pause()`, `reset()`, `setMachine()`, `loadSnapshotFromStruct()`; worker messages `keyDown`/`keyUp`/`reset`/`loadSnapshot`/`loadMemory`; `runtime/snapshot.js` has `parseSNAFile`. Last push 2025-12-28. No npm package; README says the snapshot struct is undocumented. |
| `floooh/chips` | zlib | Header-only C, ZX 48K/128K system, builds to WASM with Emscripten. Active (pushed 2026-09-26). We would write our own JS wrapper. |
| zxplay | GPL-3.0 | JSSpeccy3 fork, archived 2026-06-20, URL-parameter driven. Not a base. |
| ZX Generation, ZX84, jsZX | MIT / ? / ? | Immature or unverified; not considered. |

**Recommendation:** start with JSSpeccy 3 (works today, SNA path is in its source), vendored/forked so we can
reach `pause`/`keyDown`/`keyUp`/`reset` rather than depending on its UI. Re-evaluate chips if the GPL-3.0
licence is a problem for the playground or JSSpeccy proves hard to drive.

## Register allocation for the Z80

### How the 6502 pipeline works now (so we know what we are porting)

There is no register allocator in the textbook sense. After legalization to byte-level SSA, the shape selector
emits *unpinned* byte operations (`adc`, `cmp_br`, `load`, …) on typed vregs, and a **placement** pass decides,
per function, callee first:

- *what A, X, Y, C and N/Z hold at every point* (a beam search, width 8, over a state that includes the pointer
  pair and which values are already in their memory homes);
- *which variant of each operation* is used (`MOS6502Variants`: one table giving operand locations, template,
  bytes, cycles, clobbers and the N/Z/C effect — emission and cost come from the same row);
- *when a value is stored to or reloaded from its **home*** (a function-local zero-page byte; spilling is just a
  lazy store, "everything in its home, registers empty" is always reachable so a solution always exists);
- block parameters are homes; edges write them (parallel moves ordered, cycles through a temp).

Around it: `HomeClasses` (coalesce phi webs and copies into shared homes before placement), `HomeColouring`
(after placement, interval-colour the homes on the final code), `MOS6502ProgramLayoutPass` (stack each
function's homes above its callees' via `StaticFrameColorer`; spill the least-used homes to absolute memory;
`.zeropage N` footprints for library routines), `MOS6502RecursionSavePass` (push/pop live homes around calls
inside a call-graph SCC), and `PlacementChecker` (translation validation: runs the shape block and the emitted
block symbolically and compares stores, calls, branches, exit homes, return registers; on every compile).
**No register is callee-saved**; call-graph stacking replaces saves.

### The Z80 differs in exactly the premise the 6502 design rests on

The design doc's first claim is "within one function, fit is free": there is always another zero-page byte. On
the Z80 that is false.

- Seven byte registers (`A B C D E H L`) with real pairs (`BC DE HL`), plus `IX`. Values compete for them, so
  *spilling is a real decision* again, and a poor one is expensive: a home is an absolute-RAM byte
  (`ld a,(nn)` 3 bytes/13 T; any other byte register goes via `HL` or an `ED`-prefixed `ld rr,(nn)`), against
  `ld a,b` at 1 byte/4 T. There is no zero page, so "homes in zero page, demoted by benefit to absolute" has
  no meaning: every home is absolute from the start.
- The registers are *not* interchangeable but are far more regular than A/X/Y: `A` is the ALU register
  (`add a,r`, `cp r`), `HL` the address register (`(HL)` operand, `add hl,rr`, `ld a,(hl)`), `DE` the other
  address (`ldir`, `ex de,hl`, `ld a,(de)`), `B` the loop counter (`djnz`), `BC` the block count. Pair
  aliasing (`HL` = `H`+`L`) must be in the search state.
- Cross-block register carry matters much more: a loop wants its counter in `B`, its pointer in `HL`, across
  the back edge. The 6502 placement mostly *resets* registers at joins; the open
  [join-entry-state plan](join-entry-state-plan.md) is the same problem and **should land before the Z80 placer
  is forked from the 6502 one**, or we fork a moving target.
- What *does* carry over, unchanged: the closed world, **no callee-saved registers + call-graph-stacked static
  homes** (a Z80 program is 48K, so the stacking that saves zero page now saves RAM and costs nothing at
  calls), per-byte (or per-word) values instead of classes, the cost-from-the-emission-table principle, lazy
  stores, `PlacementChecker` as the safety net, and the beam as a tunable (`--beam-width 1` for the playground).

This is also the shape SDCC's z80 backend takes (bytewise allocation with costs from a dry-run of the code
generator), so it is not an outlier design for this chip.

### Reuse inventory

Line counts are `wc -l` of today's tree. "Generic" means no MOS6502 symbol in the code (checked by grep);
"coupled" means the logic is generic but it is written against a MOS6502 type.

**1. Reuse as-is (generic, ~2.5K lines + the whole middle end).** `LoopInfo` (225), `CallGraph` (244),
`ReentrancyAnalysis` (104), `StaticFrameColorer` (112), `LiveIntervals`/`LiveIntervalsAnalysis` (~950),
`CopyPropagationPass` (193), `PostRegisterAllocationVerifierPass` (159, via `TargetRegisterInfo`),
`RegisterScavengingPass` (296; the A-then-X-then-Y preference must come from `RegisterInfo`, check before
relying on it), `PlacementTuning`, `MirFunction.SaveState`. These need only a Z80 `TargetRegisterInfo` (still
required: the verifier, scavenger and copy passes use register classes and clobber sets, even though placement
does not).

**2. Coupled but generic in substance — lift out of `Target/MOS6502/` (mechanical, ~800 lines).**

| Piece | Lines | Coupling | Lift |
|---|--:|---|---|
| `HomeColouring` + `PhysicalLiveness` | 124 + 54 | `MOS6502Registers.IsHome/Home` | take a small `HomeRegisters` descriptor (base id, map) from the target; otherwise pure interval colouring on physreg liveness |
| `HomeClasses` | 202 | built from `MOS6502Placer`'s `_liveIn/_liveOut/_uses/_constants/_remat` tables | split the target-neutral value-liveness and use/constant tables out of `MOS6502Placer` into a shared `PlacementFacts`; `HomeClasses` then takes that |
| `MOS6502ProgramLayoutPass` | 425 | zero-page window, `lda.zp → lda.abs` twin rewrite, `.zeropage N` library footprints | split into generic "stack homes over the call graph, demote by loop-weighted benefit if a budget binds, share across SCCs" and target hooks (budget, address rewrite, library footprints). For Z80 the budget is RAM and the twin rewrite is a no-op, so only the stacking half is exercised |
| `MOS6502RecursionSavePass` | 128 | PHA/PLA emission | the analysis (which homes are live across a call that stays in the caller's SCC) is generic; saving emission is a target hook (`push`/`pop` of pairs on Z80). This is also the Z80's answer to recursion in Stage 3e(i) |
| `BeamPlacementOptions`, `Reorderings`/`ConventionRewriter` | — | A/X/zero-page register vocabulary in `FunctionConvention` | **leave on the 6502.** Callee-chosen conventions gained 0.07% for 15% compile time and are off by default; the Z80 starts with a fixed convention |

**3. Generic skeleton, 6502 state — extract only after a second consumer exists.** `MOS6502BeamPlacementPass`
(2519) + `MOS6502Placer` (936) + `MOS6502Variants` (302) + `PlacementChecker` (804) ≈ 4.6K lines, the heart of
it. These contain a reusable skeleton:

- the beam loop (expand every variant → merge states that agree on every location → keep best K), stretches
  between barriers, cost `bytes·w_size + cycles·w_speed·freq(block)`, `Replay`;
- the cross-block protocol: reverse post-order, single-predecessor inherit, primary-edge snapshot for joins and
  loop headers (`FindInheriting`, `DefineEntry`/`SatisfyEntry`), edge-block creation, ordered parallel moves,
  `Lookahead`;
- the variant-table idea (one row = operand places + template + bytes + cycles + clobbers + flag effect);
- `PlacementChecker`'s hash-consed symbolic term algebra and its block-equivalence comparison (stores, calls,
  branch conditions, exit homes, return registers).

and 6502-only content: the state (`A X Y C NZ`, the scratch pointer pair), `Work` (carry handling,
`SetUpPointer`, `PickScratch(onlyAx)`), the `Place` enum (`A X Y Carry Home Imm Abs Pair`), and the per-op
symbolic semantics of the checker. I have **not** sized how entangled these are line by line, and 6502
assumptions (one accumulator, three registers, a carry that is a location) run through `Work`. The honest
position is that the *state representation* is the part that does not generalise: a Z80 state is seven byte
registers with pair aliasing and a flag set, so `State`, `LocationHash`, `Work.PutIn/SaveRegister/FindRegister`
are rewritten, not parameterised.

**Recommendation for category 3:** follow this plan's own rule from the first draft ("duplicate first and factor
after the second consumer exists"). Build `Z80Variants`, `Z80BeamPlacer`, `Z80PlacementChecker` as a fork,
lifting just category 2 up front. Once the Z80 placer works, factor the beam driver, the join protocol and the
checker's term algebra into `Passes/Placement/` with the two targets as clients; the second implementation is
what tells us where the seams are. An abstract "PlacementState" designed with one consumer would be guessing.

### Rough size

First-draft estimate: "the 6502 target is ~8K lines". The 6502 placement-related code alone (Beam + Placer +
Variants + Checker + HomeClasses + HomeColouring + PhysicalLiveness + ProgramLayout + RecursionSave +
ConventionRewriter) is ~5.8K lines, of which category 1 and 2 save us roughly 0.8K of rewriting and category 3
gives us a template rather than code. Expect the Z80 placer + variants + checker to be **3–4K lines of new
code**, the largest single item in the plan, more than the legalizer, selector and call lowering combined. The
saving versus the first draft is not "the allocator comes free" but "the design, the analyses, the layout
engine, the tuning/testing tools and the checker's method come free".

### Alternative considered: recover a conventional allocator

`git show 078809e^` still has the graph-colouring allocator with per-node class sets, and the Z80 is closer to a
conventional register machine than the 6502 ever was. Not recommended as the primary path:

- it was removed because it was 8% (corpus) to 12% (C#) larger than placement on the 6502 and needed about a
  dozen repair passes (relocation copies, two-address fixing, phi splitting, parallel-copy sequencing) to
  express irregular registers; the Z80's irregularities (`A`, `HL`, `DE`, `B`, `IX+d`) are the same class of
  problem;
- it was 6502-shaped (class sets, scratch pointer slots), so resurrecting it is not free either;
- it has no cost model and no `PlacementChecker`.

Keep it as the fallback if the spike below shows the beam search is too slow for the playground at a useful
quality (beam 1 + the Z80's wider state), in which case a linear-scan with classes is a reasonable low-cost
mode.

### Decision gate: placement spike (do before committing to Stage 3d)

After Stage 2 (assembler + emulator exist), hand-write a **beam-width-1 Z80 placer** over a minimal variant
table (`ld`, `add/adc/sub/sbc`, `inc/dec`, `cp`+`jp cc`, `ld a,(hl)`, `ld (hl),a`, 16-bit `add hl,rr`, home
store/reload) and run it on three kernels (a byte loop with a counter, a 16-bit pointer walk, a call with
two arguments). Compare bytes and T-states against SDCC (`--opt-code-size`) for the same source, and measure
placement time per function. Proceed to the full beam only if the greedy baseline is within reach of SDCC and
the state-space growth (7 registers vs 3) keeps beam 8 affordable. If not, fall back to the conventional
allocator above. This is the single biggest risk in the plan and a day or two of work answers it.

## Decisions

1. **Image format: `.sna` (48K).** 27-byte register header + 49152 bytes RAM. In the 48K variant `PC` is not in the
   header: it is pushed on the stack in the RAM image and restored by `RETN`. We write the stack so `RETN` lands on
   our entry point. No BASIC loader, no tape traps. `.tap` (BASIC loader + CODE block) is a later addition.
2. **Origin `0x8000`** (uncontended RAM; `0x4000-0x7FFF` is slowed by the ULA). Stack at the top of RAM,
   `0xFFFF` downward. The `.sna` header's `SP` and the pushed return address are set by `PackageImage`.
3. **ROM dependence is minimal and isolated.** `PutChar` = `LD A,ch; RST 16` after opening channel 2 once at
   start; `ReadLine` polls `LAST-K`. Everything lives in `runtime.irie`, so replacing the ROM later is local.
   The test emulator stubs `RST 16` in C#, mirroring the BBC OS stubs, so lit tests need no ROM.
   Interrupts: leave IM1 and the ROM's 50 Hz interrupt on (it maintains `LAST-K` / `FRAMES`); runtime code must
   not clobber `IY` (ROM expects `IY = 0x5C3A` at interrupt time). This rules `IY` out as a frame pointer
   unless interrupts are disabled around its use — use `IX` (see Stage 3e). The ROM interrupt handler is
   expected to preserve every register it uses (from memory of the ROM listing — verify against the ROM before
   relying on it), so generated code can keep values in registers across interrupts; and it touches no program
   homes, so the 6502 design doc's "interrupt handler is a separate call-graph root" case does not arise.
4. **Value model.** Byte registers `A,B,C,D,E,H,L`; pairs `BC,DE,HL`; index `IX` (`IX/IY` halves are
   undocumented — not used). Flags `Z,C,S,P/V,N,H` as first-class physregs, like the 6502's flags. 16-bit pairs
   are real, so `i16` is *legal*, not narrowed to bytes; `i32`/`i64` narrow to 16-bit halves. The placement
   unit is **byte or word**: `i8` values live in single registers, `i16` values in pairs (or as two bytes when
   the search finds that cheaper). Register classes exist only in `Z80RegisterInfo` for the post-placement
   passes; placement works on *locations* with a variant table, like the 6502. This is the largest design
   difference from the 6502 target and drives the legalizer and the variant table.
5. **Calling convention:** first 16-bit arg in `HL`, second in `DE`, third in `BC`, remaining on the stack;
   8-bit args in `A`, `E`, `C`; returns in `A` / `HL` / `DE:HL`. **No callee-saved registers**, as on the 6502:
   values live across a call are in homes, which call-graph stacking keeps disjoint from callees. (The first
   draft listed `IX`, `IY` as callee-saved; `IY` is simply never touched, and `IX` is used only as a frame
   pointer in reentrant functions, saved there.) Decide and document in `Z80CallLowering`; mirror how
   `MOS6502CallLowering` is structured. Arguments that arrive in registers stay there as long as nothing
   overwrites them while live (the 6502 mechanism, `FindArgumentsInPlace`, with registers where it has
   zero-page bytes).
6. **Reference compiler:** SDCC and llvm-z80 for design questions, in the same spirit as the llvm-mos rule in
   `AGENTS.md`. Check whether a local llvm-z80 checkout is available before Stage 3; if not, SDCC output is the
   fallback for "what does a good sequence look like". SDCC is also the cost-model reference for the placement
   spike. There is no `irie-report` corpus for Z80 and none is planned in this effort.

## Stages

Each stage ends green under `dotnet test --solution src` in Debug and Release.

### Stage 0 — Make the target a parameter (mostly landed)

- [x] `TargetDescriptor` carries `CoreLibId` and `EmulatorKind`; `HostAssemblyResolver.ForTarget(id)` exists.
- [x] `CompilerDriver.Compile(assemblyPath, entryPoint, targetId = bbcmicro)` and the `IlToMirTranslator`
      file-path ctor take a target id; `dnrc` has `--target`.
- [x] The translator gets its runtime from `target.GetRuntime()` (`CompilerDriver.cs:55`).
- [ ] `Irie.csproj`: embed each target's runtime as its own resource; `Z80Target.GetRuntime()` reads its own.
- [ ] Reword the "the `runtime.irie`" comments/messages (`EcmaMethod.cs:67`, `IlToMirTranslator.cs:2512,2576`).
- [ ] Audit the two remaining comment-vs-behaviour sites (`CallOp.cs:32`, the libcall naming in the
      translator).
- [ ] `LegalizerPass.NarrowWideBlockParameters`: take the maximum legal parameter width from the target
      (`LegalizerInfo`) instead of the literal 8. 6502 behaviour unchanged.
- [ ] Verify: existing tests unchanged; an unknown `--target` reports a clear error (check whether a test
      already covers this).

### Stage 1 — Z80 machine-code layer (assembler, encoder, disassembly)

- [ ] `Target/Z80/Z80Registers.cs`, `Z80Opcode.cs`, `Z80Op.cs` — op set for the MachineCode layer first
      (`ld r,r'`, `ld r,n`, `ld rr,nn`, `ld a,(nn)`, `ld (hl),r`, `ld r,(ix+d)`, `add/adc/sub/sbc/and/or/xor/cp`,
      `inc/dec`, `add hl,rr`, `sbc hl,rr`, `push/pop`, `jp/jr/djnz/call/ret` incl. conditions, `rlca…`, shifts,
      `ldir`, `ex de,hl`, `cpl`, `scf`, `ccf`…).
- [ ] `Z80AssemblyWriter` / `Z80AssemblyParser` (traditional `LD A,(HL)` syntax) for the MachineCode layer, wired
      into `TargetDescriptor.WriteAssembly`. The parser also reads the hand-written library (`libcalls.z80.s`,
      the analogue of `libcalls.s`).
- [ ] `Z80BinaryEncoder`: two-pass like `MOS6502BinaryEncoder` — layout, then encode; `JR` range checks (signed
      8-bit) with an error, not silent relaxation (no relaxation yet; add `JR`→`JP` only when a program needs it).
- [ ] Unit tests: encode/decode round trips for every encoding form; `.s` lit tests for assembler output.

### Stage 2 — Z80 test emulator (needed before the backend can be tested end to end)

- [ ] Add a Z80 core under `lib/` (own implementation or a vetted permissively-licensed one; no .NET Z80 core has
      been evaluated yet — decision to make here). Must cover the documented instruction set including
      `DD`-prefixed `IX` forms, `ED` block ops and flags (`H`, `N`, `P/V`). It also needs a **cycle count per
      instruction**: the placement cost model charges T-states, and `irie-exec` measures cycles on the 6502.
- [ ] `DotNetro.Compiler.Tests.Emulator`: new `ITargetSystem` `ZxSpectrumTargetSystem` (load at `0x8000`, set
      `SP`, stub `RST 16` → console output, stub `LAST-K` polling to feed `--input`, tick budget as for BBC).
      `--target-system zxspectrum`; `--trace` writes PC/AF/BC/DE/HL/SP per instruction.
- [ ] `Irie.Tools.Exec` / `Irie.Tools.Interpreter` Z80 execution target (`ExecTarget`): this is what runs the
      `RUN-exec` `.irie` corpus against the reference interpreter, and is the oracle for every Stage 3 step.
- [ ] Core self-test: a small set of hand-assembled programs run via the `Z80AssemblyParser` + `Z80BinaryEncoder`.
      A well-known conformance ROM (e.g. ZEXDOC) is optional but worth running once.

### Stage 2b — Lift the generic placement infrastructure (independent; can run alongside 1 and 2)

Pure refactors of the 6502 target with no output change (all lit goldens identical). One commit each.

- [ ] Split the value-liveness / use-count / constant / address-rematerialisation tables out of `MOS6502Placer`
      into a shared `PlacementFacts` (`Passes/Placement/`); `HomeClasses` consumes that.
- [ ] `HomeColouring` + `PhysicalLiveness` parameterised by a `HomeRegisters` descriptor from the target
      (`Target.HomeRegisters` or on `TargetRegisterInfo`); moved to `Passes/Placement/`.
- [ ] `ProgramLayoutPass`: split into the generic stacking/demotion/SCC logic and target hooks (budget,
      address-rewrite, library footprint). Keep `MOS6502ProgramLayoutPass` as the subclass.
- [ ] `RecursionSavePass`: generic analysis + target hook for the save/restore emission.
- [ ] Re-run the full suite and `sweep.py`; the `placement.cost` statistic must be unchanged.
- [ ] **Do not** extract the beam driver or `PlacementChecker` yet (see category 3 above).

### Stage 3 — Z80 backend (the bulk of the work)

Work incrementally; each sub-step gets `.irie` lit tests with complete `CHECK` listings per `AGENTS.md`
(generate with `dotlit --regenerate`).

- [ ] **3a Dialect + register info:** `Z80Dialect`/`Z80InstructionInfo` (operand classes, tied operands, implicit
      flag defs/uses), `Z80Registers`, `Z80RegisterClass` / `Z80RegisterInfo` (`a`, `r8`, `bc/de/hl/ix` pair
      classes, flag classes; used by the verifier, scavenger and copy passes, not by placement).
- [ ] **3b Legalizer:** `i8`, `i16` legal for add/sub/logic/shifts-by-constant; `i32` → two `i16` halves with
      carry chain (`adc hl,rr`); variable shifts and mul/div/rem → libcalls into the library (generalise the
      libcall naming from the 6502 so both targets share the mechanism); `cast.*`, `ptradd`, `mem.load/store` for
      `i8/i16/i32`. Block parameters narrow to the target's legal width (Stage 0).
- [ ] **3c Shape selector** (`Z80ShapeSelector`): unpinned operations on typed vregs, as for the 6502, now at
      `i8` and `i16`: `add16`, `inc16`, `cmp_br`, `load`/`store` (symbol, pointer, pointer+offset), `const`.
      Decide whether `i16` adds are matched as one `add16` or as a chain of byte `adc`s the placer re-fuses;
      prefer the former (the 6502 chose per-byte because it has no word ops).
- [ ] **3d Variants + placement:** `Z80Variants` (the single table of what each operation can be), then
      `Z80BeamPlacementPass` / `Z80Placer`, then `Z80PlacementChecker`, in that order, behind the spike in the
      decision gate. Order of work: greedy (beam 1, everything homed) → variants for registers and pairs →
      cross-block carry (after the join-entry-state plan) → full beam. Run `PlacementChecker` from the first
      working commit; it is what caught every fault the 6502 tests found.
- [ ] **3e Call lowering + frames:** `Z80CallLowering`; `Z80FrameLowering`. Local-variable strategy: homes and
      frame slots of non-reentrant functions are static RAM, stacked over the call graph by the Stage 2b layout
      pass (no frame pointer, absolute addressing). (i) Start with that plus `Z80RecursionSavePass` (push/pop of
      the live homes around intra-SCC calls) — the 6502 mechanism, so recursion works from the beginning with no
      frame pointer; (ii) later add an `IX`-based frame for functions whose frames need real stack storage
      (address-taken locals, large arrays), `IX` saved/restored in prologue/epilogue. `FrameSlot.StackId`
      carries the placement; `FrameLowering.LowerFrameAccess` emits the form.
- [ ] **3f Pseudo expansion + post-placement passes:** `Z80PseudoExpander` (copies between byte regs, pair moves
      via two byte moves or `push/pop`, `ex de,hl`), `Z80BranchLowering` (`jp`/`jr`), branch folding and block
      placement (read the 6502 `BranchFolderPass` / `BlockPlacementPass`; neither was checked for how generic
      they are — duplicate first, factor after).
- [ ] **3g MachineCode emitter + `Z80MachineCodeEmitTable`:** same shape as the 6502 emit table, throws on
      unmapped ops.
- [ ] **3h Target class:** `Z80Target` (chip), `ZxSpectrumTarget : Z80Target` (`DefaultOrigin = 0x8000`,
      `PackageImage` → `.sna`). Register via `Target.Add*Passes` hooks only; the standard pipeline is unchanged.
- [ ] Exit criterion: the existing `.irie` lit corpus has Z80 counterparts for the arithmetic / branch / call /
      memory cases, `RUN-exec` passes against the interpreter, and a hand-written "return 42" MIR program runs
      in the test emulator.

### Stage 4 — Spectrum system layer

- [ ] `DotNetro.CoreLib.ZxSpectrum` project + `Console.ZxSpectrum.cs` (platform half of `Console`).
- [ ] `Z80/runtime.irie` (Spectrum-specific part): `start` (set `SP`, open channel 2, call entry, then `halt`
      loop), `PutChar` (`RST 16`, CR→`0x0D`), `PlatformWriteLineInt32`, `PlatformReadLine` (poll `LAST-K` with
      echo), `PlatformBeep` (`BEEP` ROM routine or port `0xFE` loop), `StringBytes`, indirect-call trampoline
      (`jp (hl)` — simpler than the 6502's), plus the mul/div/rem libcalls from 3b. Any library routine that
      scratches static RAM declares its footprint like the 6502's `.zeropage N`, so layout stacks callers
      above it.
- [ ] `ZxSpectrumSnaWriter` (internal, like `BbcMicroDfsImage`): `.sna` = header with `SP` pointing at the pushed
      entry address, interrupts enabled, `IM 1`, border colour; RAM image `0x4000..0xFFFF` with the system-variable
      area initialised from a canned post-boot snapshot (so the ROM interrupt handler and `RST 16` work: the
      simplest robust approach is to embed a ~few-hundred-byte template of the `0x5C00-0x5CB5` system
      variables and the channel tables captured from a freshly booted 48K, rather than computing them).
      Unit tests: header fields, size is exactly 49179, entry address round trips through `RETN`.
- [ ] `TargetRegistry`: add `ZxSpectrumId = "zxspectrum"`, `EmulatorKind: "jsspeccy"`, with `Encode`/`WriteAssembly`
      from Stage 1.
- [ ] Port the `.cs` lit corpus to run on both targets (the `RUN` lines choose `--target`); tests that
      depend on BBC-specific behaviour stay BBC-only. `DIFF: dotnet emulated` keeps working.

### Stage 5 — Playground integration

- [ ] Vendor JSSpeccy 3 (GPL-3.0; confirm the playground's licensing, record it in `NOTICE`/docs) under
      `ClientApp/` as a built artefact or a patched fork exposing `pause()`, `reset()`, `keyDown`/`keyUp`,
      `loadSnapshotFromStruct` — all exist in `runtime/jsspeccy.js` / `worker.js` already. Includes the ROM
      (`static/roms/48.rom`): confirm redistribution terms before shipping (Amstrad has permitted this for
      emulators historically; verify the current wording).
- [ ] `panels/zxSpectrumMachine.ts` with the same surface as `BbcMicroMachine` (`boot(image)`, `run`, `pause`,
      `isRunning`, `keyDown`, `keyUp`, `clearKeys`); `panels/emulatorPanel.ts` generalised (or a sibling
      `zxSpectrumPanel.ts`) and `EMULATOR_PANELS` extended with `jsspeccy`. The C64 target (`vice-c64`,
      `panels/c64Machine.ts`) is the existing second-machine precedent to copy.
- [ ] Parse the `.sna` in the browser the way `runtime/snapshot.js` `parseSNAFile` does, or hand the bytes to
      `openUrl`/`loadSnapshotFromStruct` — pick whichever is stable after reading the vendored source.
- [ ] Target picker in the playground: new `TargetInfo` entry comes from `TargetRegistry` already.
- [ ] Fallback if JSSpeccy proves awkward to drive: wrap `floooh/chips` `zx.h` (zlib) via Emscripten; keep the
      `ZxSpectrumMachine` interface identical so the panel does not change.

## Risks / open questions

- **Placement is the plan's critical path.** Not "backend size" in general: the Z80 placer, variant table and
  checker are 3–4K lines of new code with a design question underneath (does the beam search over a 7-register
  state stay affordable and good?). Mitigations: the Stage 3d spike gate; beam 1 as the always-correct
  baseline (the 6502 design guarantees a solution exists from "all in homes, registers empty"); a fallback to a
  conventional allocator. Note the 6502 design doc records that beam 1 and some lookahead settings still fail
  the checker on a couple of programs (`FindInheriting`); the Z80 fork inherits that class of bug unless the
  join-entry-state work fixes it first.
- **Compile time in the WebAssembly playground.** The 6502 default (beam 8) was chosen on a state of A/X/Y/C/NZ.
  The Z80 state is far larger; expect to default the Z80 to a smaller width and measure with
  `Irie.Tools.Reference/sweep.py` (extend it for a Z80 target).
- **The join-entry-state plan** (`notes/join-entry-state-plan.md`, not started) changes `BeamPlacer`'s join
  logic. Sequence it before forking.
- **Static-RAM homes cost 3 bytes / 13+ T per access.** If `Z80Variants` leaves too much in homes the code will
  be uncompetitive with SDCC; the spike is also the first measurement of that.
- **`IY` and interrupts.** The ROM interrupt handler needs `IY = 0x5C3A`. Using `IY` or disabling interrupts
  anywhere in generated code has to be a conscious choice. Default: do not use `IY`; generated code never
  executes `DI`.
- **Contended memory timing** only changes speed, not results; ignore for correctness, note it for samples.
- **Indirect calls and recursion.** `CallGraph` sends an indirect call only to address-taken functions (the
  first draft's "every function" conservatism is gone). Recursion is handled by `RecursionSave` from the start
  (Stage 3e(i)), so delegates and interface dispatch are a code-quality concern, not a correctness one.
- **ROM licensing and GPL-3.0** (JSSpeccy) need a decision before Stage 5 ships, not before it starts.
- **Z80 core for the tests** (Stage 2): build vs adopt is undecided and no candidate has been evaluated; it
  needs cycle counts for the cost model and for `irie-exec`.
- **`.sna` system-variable template** (Stage 4): the exact bytes must be captured from a booted 48K; verify the
  snapshot runs in JSSpeccy (and ideally a second emulator) before relying on it.

## Suggested order and checkpoints

0 (finish) → 1 → 2 (a Z80 assembler + emulator + exec target, usable by hand-written tests) with **2b in
parallel** → *join-entry-state plan lands* → **placement spike gate** → 3a–3h (backend) → 4 (system + `.sna`) →
5 (playground). Stage 0 and 2b are independent and can land at any time. After Stage 2, re-check the plan: if
the Z80 core is slow or the encoder is more work than expected, adjust Stage 3's scope before starting it; if
the spike disappoints, replace 3d with the conventional-allocator fallback and keep everything else.
