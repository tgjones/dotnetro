# Beam placement: close the gap to the colouring allocator, then finish S4

Status: not started. Written 2026-10-18 at the end of the session that did S4.1–S4.4.
Self-contained: you should not need to read
[register-allocator-design.md](../doc/irie/register-allocator-design.md) (789+ lines) to
do this; section references below are for the curious.

## Why this exists

DotNetro is moving Irie's MOS6502 register allocation from a graph-colouring allocator
(`--placement colouring`, the default, to be deleted) to a *placement* design
(`--placement beam`): isel emits unpinned byte operations, then a beam search decides
which of A, X, Y (or a zero-page "home") holds each value at each point.

Stage S4 of the staged plan is "placement across blocks, then **delete the colouring
allocator and the passes it needs**". Deleting is conditional on this exit criterion
(agreed with the maintainer):

> Scoreboard total and C# set better than today (colouring); **no case worse by more
> than 2 instructions without a written reason.**

The cross-block work (S4.1–S4.4) is done and committed. The criterion is **not** met:
most of the cases where beam is still larger than colouring are single-block functions,
so block-boundary placement cannot fix them. This plan is the within-block burn-down.
When it is done, resume **S4.5** (the switch-over and deletion, described at the end).

## What is already done (commits on `feat/irie-exec`)

| Commit | Step | What |
|---|---|---|
| `0de0eeb` | S4.1 | Home classes (`MOS6502HomeClasses.cs`): block parameters and copies share one memory home with their arguments when never live together |
| `7b97d8e` | S4.2 | A block with one incoming edge starts from the registers its predecessor left; lookahead on the exit state |
| `6545bd2` | S4.3a | Joins and loop headers start from the registers of their *first* incoming edge (primary); other edges reconcile (`MOS6502EntryContract.cs`: `Snapshot`, `ExitRole`) |
| `510bde1` | S4.3b | Loop-carried block parameters can be loaded into registers at the loop header; candidates judged by lookahead down to the latch |

Details are in the "S4 progress" bullets of the design doc §5.

## Where we stand (Release build, per `notes/measure-placement.sh`)

| Set | Colouring | Beam now |
|---|--:|--:|
| `LlvmMosReference` corpus code, bytes (43 cases) | 3660 | 3719 (+1.6%) |
| C# lit programs + benchmark kernels, bytes (120 programs) | 182 912 | 169 374 (−7.4%) |
| Corpus cycles of the functions under test (sum) | 5 903 825 | 5 962 929 (+1.0%) |

Beam is smaller than colouring on 11 corpus cases, equal on 14, **larger on 18** (12 of
them by more than 2 bytes). The C# programs where beam is larger (12 of 120):
`BoxStruct`, `DivRemPowerOfTwo`, `Int64Arithmetic`, `Int64Bitwise`, `Int64Calls`,
`Int64Memory`, `Int64MulDivRem`, `Int64Shifts`, `MulInt32`, `MulPowerOfTwo`,
`NarrowIntegerCompare`, `RecursionLiveAcrossCalls` (mostly small margins, `Int64Memory`
+426 and `Int64MulDivRem` +514 bytes are the largest).

### The corpus cases still larger than colouring (bytes of code; cycles)

| Case | Colouring | Beam | Δ bytes | Colouring cycles | Beam cycles |
|---|--:|--:|--:|--:|--:|
| `basics/stack-args` | 130 | 191 | +61 | 56 320 | 79 360 |
| `pressure/pressure-high` | 434 | 484 | +50 | 2 436 281 | 2 455 481 |
| `realistic/gcd` | 351 | 371 | +20 | 2 211 744 | 2 241 408 |
| `basics/chain-arith` | 81 | 99 | +18 | 155 491 | 162 403 |
| `memory/struct-pass` | 50 | 66 | +16 | 143 248 | 149 392 |
| `widths/mixed-widths` | 77 | 90 | +13 | 28 555 | 33 931 |
| `realistic/fibonacci` | 96 | 109 | +13 | 29 000 | 37 750 |
| `memory/global-rw` | 20 | 28 | +8 | 512 | 704 |
| `constraints/shift-var` | 23 | 29 | +6 | 35 040 | 37 344 |
| `realistic/strlen` | 44 | 49 | +5 | 746 | 836 |
| `basics/add-i32` | 22 | 27 | +5 | 10 240 | 12 032 |
| `control-flow/early-return` | 39 | 42 | +3 | 8104 | 8162 |
| `realistic/factorial-recursive` | 128 | 130 | +2 | 9908 | 9780 |
| `coalescing/swap` | 17 | 19 | +2 | 8192 | 8704 |
| `widths/zero-extend` | 12 | 13 | +1 | 286 | 286 |
| `widths/sign-extend` | 21 | 22 | +1 | 358 | 384 |
| `pressure/pressure-i64` | 547 | 548 | +1 | 211 200 | 212 992 |
| `control-flow/if-else` | 54 | 55 | +1 | 14 721 | 14 721 |

(The "instruction" in the criterion means a real asm instruction; compare with
`iriec --emit=asm` line counts as well as bytes. Bytes are the number to track.)

## Orientation: how beam placement works (enough to start)

Pipeline for `--placement beam` (`src/Irie/Passes/PipelineBuilder.cs`): legalized byte-level
SSA MIR → `MOS6502ShapeSelector` (unpinned ops: `adc sbc and ora eor shle lshre cmp_br cmp
load store const inc dec`) → **`MOS6502BeamPlacementPass`** → post-placement passes
(program layout of the homes into zero page, branch folding, `MOS6502LateOptimizationPass`
that drops a redundant `cmp #0`, …) → emission. `--placement naive` is the K=1 baseline
(`MOS6502NaivePlacementPass`, the base class `Placer` holds the shared machinery);
`--placement colouring` is the old pipeline.

Key files (all under `src/Irie/Target/MOS6502/`):

- `MOS6502BeamPlacementPass.cs` — the whole search. `BeamPlacer` (per function) and its
  nested `Work` (a working copy of a search state plus the moves that change it).
  - A **state** is what A, X, Y, the carry C, the N/Z flags (as "reflects value v") and the
    scratch pointer pair (`PointerLo/Hi`) hold, which values have their **home** written
    (`H`), and which still live in the zero-page register they arrived in (`F`).
  - The search runs over *stretches* of operations between **barriers** (calls, frame
    accesses `FrameLoadByte/StoreByte`, hand-written runtime code). Barriers are placed by
    the naive code path (`Placer.PlaceInstruction`): they expect every live value in its
    home and registers empty. `IsSearchable` says what is searched.
  - For each operation `ExpandOne` makes every *plan* (which register the operand sits in,
    commuted or not, store-vs-move to save a register) as a `Work`; each plan is a list of
    `Step`s (a variant of `MOS6502Variants` plus a memory operand, or an action). Cost =
    `bytes × SizeWeight + cycles × SpeedWeight × block frequency` taken from the variant
    table, so the cost of a placement is what it emits. Beam width 8
    (`BeamPlacementOptions`).
  - Values are stored to their home lazily (`SaveRegister`, `EnsureHome`, `StoreLive`).
  - **Runs of copies** (call argument/result setup, returns, ABI entry copies) are one
    parallel move: `Work.CopyRun`.
  - Arguments arriving in zero-page registers stay in place for the whole function when
    nothing could overwrite them: `ArgumentsStayInPlace()` — **a likely culprit below**.
- `MOS6502Variants.cs` — the table of variants per operation (operand places `A X Y Carry
  Home Imm Abs Pair`, template, bytes, cycles). Single source of truth for cost and
  emission.
- `MOS6502NaivePlacementPass.cs` — `Placer` base: homes (`Home`, `HomeKey`), liveness,
  edge moves, the naive code for every op and for barriers.
- `MOS6502HomeClasses.cs` — which values share a home (S4.1).
- `MOS6502EntryContract.cs` — `Snapshot` / `ExitRole`: the cross-block state (S4.2–S4.3).
- `PlacementChecker.cs` — **independent translation validation** of every placed block
  (runs on every compile unless `IRIE_NO_PLACEMENT_CHECK` is set). It must stay green; it
  has caught every placement bug found so far. Trust it, and do not weaken it.
- `src/Irie/Passes/Analyses/LoopAnalysis.cs` — loops and static `8^depth` block frequency.

Design context you may want: llvm-mos (the reference compiler) and Oscar64 are both used as
references; see `AGENTS.md` ("MOS6502 codegen design reference") for where the local
toolchains are. A pipeline crash or a worse-than-colouring result is usually a design
question: find the root cause, do not add special-case workarounds (`AGENTS.md`).

## Build, test, measure

```bash
# Build (Release is what the measurements use)
dotnet build src/DotNetro.slnx -c Release

# Tests (both lit suites are the only tests that execute generated code)
dotnet test --project src/Irie.Tests -c Release            # ~1 min incl. beam executed cases
dotnet test --project src/DotNetro.Compiler.Tests -c Release   # ~5 min; runs every .cs under colouring, naive AND beam
# CI also runs Debug (dotnet test --solution src); run it before the final commit.

# Look at one case
src/Irie.Tests/bin/Release/net10.0/iriec --target mos6502 --emit=asm --placement beam \
    src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/basics/chain-arith.irie
# same with --placement colouring to compare; --stop-after InstructionSelector shows the
# shape MIR the placement receives.

# Execute a corpus case in the emulator (checks results against the interpreter, prints cycles)
src/Irie.Tests/bin/Release/net10.0/irie-exec <case.irie> --target mos6502-bbcmicro \
    --emulator src/DotNetro.Compiler.Tests.Emulator/bin/Release/net10.0/DotNetro.Compiler.Tests.Emulator \
    --placement beam --exec "chain_arith(*, *, *)"      # the directive is in the file's RUN-exec line

# Refresh golden CHECK blocks after an intended output change, then READ the diff
dotnet run -c Release --no-build --project src/DotLit -- --regenerate src/Irie.Tests/Lit/CodeGen/MOS6502/<file>.irie
```

C# lit programs: `dnrc` compiles an assembly; the tests build it with
`src/DotNetro.Compiler.Tests.CsCompiler`. `notes/measure-placement.sh` does both sets.

**Re-measurement.** `notes/measure-placement.sh <colouring|beam> <dir>` writes
`<dir>/corpus.tsv` (case, bytes, cycles, ok) and `<dir>/cs.tsv` (program, bytes); ~2.5
minutes each. The colouring allocator is still in the tree, so regenerate its baseline
first (`… colouring /tmp/base`) — this is the reference, per case. Then, after each fix:

```bash
notes/measure-placement.sh beam /tmp/now
awk -F'\t' '$4==1{b+=$2;c+=$3;n++}END{print n,b,c}' /tmp/now/corpus.tsv     # cases, bytes, cycles
awk -F'\t' '{b+=$2}END{print b}' /tmp/now/cs.tsv                              # C# bytes
join -t$'\t' <(sort /tmp/base/corpus.tsv) <(sort /tmp/now/corpus.tsv) \
  | awk -F'\t' '{d=$5-$2; if(d>0)print d,$1,$2,$5}' | sort -rn                 # cases still larger
```

Expected baselines (should reproduce): colouring 43 cases / 3660 bytes / 5 903 825 cycles,
C# 182 912 bytes; beam (HEAD `510bde1`) 3719 / 5 962 929 and 169 374. Absolute C# sizes
include the reachable corelib, so they move with corelib changes; compare like with like.

## Known causes and starting points

These are hypotheses from reading output; verify each by diffing asm against colouring
before fixing, and fix the *root cause* (see `AGENTS.md`).

1. **Arguments leave their zero-page registers for no reason** (`chain-arith`,
   `stack-args`, probably `struct-pass`, `mixed-widths`, `global-rw`). `ArgumentsStayInPlace`
   (beam pass, near the top) is all-or-nothing: it gives up if the function has *any* call,
   frame load/store, or copy into a fixed zero-page register. Effects seen in
   `chain_arith`: every argument byte is copied from where it arrived into a fresh home,
   and for the call `mulhi3(a, b)` the second operand is copied to a temporary home and
   back into the register it was already in (`LDA $02 / STA $18 … LDA $18 / STA $02`).
   Wanted: an argument that arrives in register R and is passed on in R needs no code; an
   argument stays in place until something that can actually overwrite *its* register
   (a call, a copy into that very register, a frame access if the frame code uses it) —
   per-register, not per-function. `Work.CopyRun` already tracks `F` (value still living in
   its arrival register) per value; the all-or-nothing switch is the cruder mechanism next
   to it. A frame access clobbers A, Y and the scratch pointer pair only.
2. **Redundant self-copies**: `LDA ($00),Y / STA $24 / LDA $24 / STA $24` in `stack-args`
   (a copy between two values of the same home class stores the home to itself). Copy
   coalescing (S4.1) makes a copy's source and result share a home; the copy then should
   emit nothing.
3. **Call setup through homes** where a direct register move would do (the parallel move in
   `CopyRun` step 5, the toGpr/toZp ordering); compare the call sequences against colouring
   in `chain-arith`, `many-calls` (already fine), `fibonacci`, `factorial-recursive`,
   `struct-pass`.
4. **Missing variants** the search could use: memory-destination forms (`INC zp`, `DEC
   zp`, `ASL zp`, `LSR zp` — only useful now that a result can share its operand's home,
   which S4.1 made possible), `abs,X` / `abs,Y` loads and stores (needs shape selection to
   fold an index into the address), `ADC abs` folding of a single-use symbol load. The
   variant table already has rows for several; the plans in `ExpandOne` do not generate them.
5. **Wide arithmetic / pressure** (`pressure-high` +50, `pressure-i64` +1, `add-i32` +5,
   `gcd` +20, `mixed-widths`, `shift-var`): not diagnosed. Start with `add-i32` (smallest) and
   `pressure-high`; look at where beam stores and reloads a byte colouring keeps in a
   register, and at the transfers (`TAX`/`TAY`/`TXA`/`TYA`) it uses to juggle a carry chain.
6. **`early-return` +3, `swap` +2, `strlen` +5, `fibonacci` +13**: small; likely
   cross-block (join states chosen by lookahead from the first stretch only) or call setup.
   Diagnose after 1–3, which may already fix them.
7. **C# only**: `Int64Memory`, `Int64MulDivRem`, `Int64Calls`, `Int64Shifts` — probably the
   same causes at scale (wide values, calls). Re-measure after each fix.

Out of scope: the "work outside the allocator" list in the design doc (range narrowing,
known-zero bytes, loop-invariant code motion, i16 div helper). Not N/Z across edges either:
the late `cmp #0` optimisation only looks inside one block, so inheriting N/Z across an
edge would be priced as free but still be emitted; extending that pass is a possible
follow-up, not part of this.

## Method

Work through the causes in the order above (largest, most general first). For each:

1. Pick a case, diff beam against colouring asm, name the root cause.
2. Fix it in the beam pass / variant table (or the shape selector if that is the cause);
   no special cases for one function.
3. `dotnet test` both projects (Release). The `PlacementChecker` and the emulator-executed
   lit tests are the safety net; a checker failure means the placement is wrong, not that
   the checker is.
4. Regenerate any golden that legitimately changed (`dotlit --regenerate`), and *read* the
   diff. Keep CHECK blocks strict (`AGENTS.md`).
5. Re-measure (above). Record bytes/cycles in the commit message. Do not accept a change
   that makes the total or the C# set worse, or an individual case worse without a reason.
6. Commit per cause (this repo's convention: one commit per step; do not commit if tests
   are red). Comments must be self-contained — never cite plans or design-doc sections.
7. Append a short "S4 burn-down" bullet to the design doc's S4 progress with the new table.

Stop and ask the maintainer if a fix needs an architectural change (a new IR op, a new
pass, changing the checker), or if a cause turns out to need something from the
"out of scope" list.

## Done when

The exit criterion holds against a freshly measured colouring baseline: corpus total and C#
total better than colouring, and every case within 2 instructions of colouring or with a
written reason (put reasons in the design doc, one line per case). Then resume S4.5.

## S4.5 (resume here once the above holds)

1. Make `Beam` the default placement (`PipelineBuilder`, `iriec`, `dnrc`, `irie-exec`,
   `CompilerDriver`, `CompileRequest`); decide whether `Naive` stays as the correctness
   baseline (the plan said keep it).
2. Delete the colouring-only code (≈6K lines): `GraphColouringAllocator`,
   `RegisterAllocatorPass`, `PhiEliminationPass`, `TwoAddressInstructionPass`,
   `OperandLegalizationPass`, `RematerializableSinkPass`, `RegisterAssignmentVerifier`,
   `MOS6502ParallelCopyPass`, `MOS6502AddressingModeSelectorPass`,
   `MOS6502IncrementStrengthReductionPass`, the old `MOS6502InstructionSelector`, and the
   `Colouring` enum value / `--placement colouring` options. **Keep**: the shared tail
   (`RegisterScavengingPass`, `CopyEliminationPass`, `CopyPropagationPass`,
   `MOS6502LateOptimizationPass`; pseudo-expansion still mints scratch vregs, so the design
   doc's "scavenging goes" line is wrong and needs correcting), and `StaticFrameColorer`
   (program layout uses it). Check each deletion by building, not by name.
3. About 190 Irie lit tests and the executed `.irie` cases run on the default pipeline:
   regenerate goldens (`dotlit --regenerate`), review every diff, and port or remove
   colouring-specific ones. Merge `LitTestNaivePlacement` / `LitTestBeamPlacement` and the
   exec variants into the default.
4. Update the design doc (S4 progress, §4 table), `src/Irie/CLAUDE.md`,
   `doc/irie/llvm-mos-comparison.md` (regenerate with `irie-report generate-report`),
   `doc/dotnetro/benchmarks.md` (`generate-benchmarks`).
5. Run the full `dotnet test --solution src` in Debug and Release.
