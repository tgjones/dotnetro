# Irie codegen improvement ideas

A backlog of MOS6502 codegen improvements that are understood but not yet done.
Each entry says what the improvement is, why, what it would touch, and what is
still unknown. Entries are not commitments; when one is picked up, work it through
the `improve-codegen` workflow (score against the llvm-mos corpus in
[llvm-mos-comparison.md](llvm-mos-comparison.md)) and remove it from here.

## A `cmp.zero` op that defines only N/Z

**Status:** not started. Raised while fixing a late-optimization bug (below).

### Background

`mos6502.cmp.imm $r, #0` currently serves two different purposes:

1. **Test a value for zero / sign** (`eq`/`ne` against 0, or the high-byte sign test
   for `slt` against 0). Only N and Z matter.
2. **One link in the unsigned multi-byte compare ladder** (`uge`/`ult` on i16/i32),
   where a byte of the constant happens to be 0. Here the following `BCC`/`BCS`
   reads **C**, which `cmp #0` always sets.

`MOS6502LateOptimizationPass` (the llvm-mos `lowerCmpZeros` analogue) erases a
`cmp #0` when the register was just produced by a load or transfer, since those
already leave N/Z correct. That is only sound for role 1. It used to drop role 2
too, so the unsigned ladder branched on a stale carry and gave wrong answers for
e.g. `uint x < y` with zero upper bytes. The pass now keeps the compare while a
later instruction in the block reads `$c` (`CarryIsReadAfter`). Regression test:
`CmpUltConstZeroBytes-MachineCode.irie`.

### The idea

Follow llvm-mos, which has a `CmpZero` pseudo defining **only** N/Z. Its late-opt
fold applies only to that pseudo; if no fold happens it is lowered to a real
compare.

- Add `mos6502.cmp.zero` (defines `$n`, `$z`; no `$c`).
- The instruction selector emits it for role 1 and keeps a real `cmp.imm #0` for
  role 2 (the ladder genuinely needs C).
- The late-opt fold becomes unconditionally sound, so `CarryIsReadAfter` can go.
- Fallback lowering when not folded: `cmp.imm #0` (defines N, Z and C).
- Possible follow-on: lower an unfolded `cmp.zero` more cheaply when the value
  lives in `$x`/`$y` (e.g. transfer to `$a`, or `cpx/cpy #0`).

### Why not now

The current fix is correct and conservative. Its one blind spot is a carry value
live out of the block, which the selector never produces: every compare is
consumed by a branch in its own block. So this is a design cleanup plus a possible
small size win, not a correctness need.

### Unknowns / things to check before starting

- How many selector paths emit a zero compare today (`EmitCmp`, the signed
  high-byte sign test, the `ne`/`eq` against 0 cases)? That sets the size of the
  selector change.
- Which lit goldens contain a `CMP #$00` and would change.
- Whether the instruction-count scoreboard moves at all. If it does not, the main
  value is removing the lookahead from the pass.

### References

- llvm-mos: `MOSLateOptimization::lowerCmpZeros` and `lowerCmpZero`
  (`llvm/lib/Target/MOS/MOSLateOptimization.cpp`).
- Irie: `src/Irie/Target/MOS6502/MOS6502LateOptimizationPass.cs`,
  `MOS6502InstructionSelector.cs` (`EmitCmp`, wide-compare ladder),
  `MOS6502Dialect.cs` (`CmpInfo`).

## Sign extension and multi-byte constant ±1

**Status:** not started. Measured from the width cases in the llvm-mos scoreboard
(`llvm-mos-comparison.md`): `widths/sign-extend` 13 instructions vs llvm-mos's 11,
`widths/zero-extend` 9 vs 6, `widths/mixed-widths` 46 vs 43.

**The sign byte itself is not the gap.** `cast.sext` (legalized in
`MOS6502LegalizerInfo.LowerExtend`) builds the high byte as
`select (slt top, 0), 0xFF, 0`, which lowers to a branch diamond (`cmp #0; bmi`,
`ldx #0` / `ldx #$FF`). llvm-mos's `sign-extend.s` does the same
(`tax; bpl; ldx #255; jmp; ldx #0`). A branch-free form (arithmetic shift, or a
carry-based sequence) might still be cheaper, but llvm-mos does not use one here,
and Irie has no shift op yet, so treat it as unproven.

**The difference is what happens after the extend:** `(int)a - 1` on the
sign-extended i16.

- Irie: a two-byte `sec; sbc #1; ... sbc #0` chain, with the register shuffling
  (`tay; txa; ...; tax; tya`) it forces: 7 instructions after the diamond.
- llvm-mos: `clc; adc #255; cmp #255; bne; dex`, i.e. add to the low byte only and
  decrement the high byte when the low byte wrapped, 5 instructions.

So the idea is a multi-byte add/sub of a small constant lowered as "low-byte
ALU op, then conditionally inc/dec the higher bytes". `MOS6502IncrementStrengthReductionPass`
already handles in-register ±1 and is the natural neighbour; check
`sign-extend.txt` for which llvm-mos pass produces it before choosing where to put
it.

**`zero-extend`** (9 vs 6) has no branch at all: the extend is a free `ldx #0`.
The overhead there is the same constant-add shape (`adc #1` then carry into the
high byte vs llvm-mos's `adc #1; bne; inx`), plus register shuffling. The same
fix would cover both.

## `switch`: binary search and jump tables

**Status:** not started. The IL `switch` opcode works today (`TranslateSwitch` in
`IlToMirTranslator.cs`; lit tests `SwitchInt32`, `SwitchSharedTargets`, `SwitchByte`),
but lowers every table to a **linear chain** of `cmpi eq` + `cond_br`, one test per
case, in order. Cases that already target the fall-through block are skipped. Correct,
but dispatch is O(n) with a 4-byte compare per case for an `int` selector.

### What llvm-mos does

llvm-mos has all three strategies. Checked against the source only; there is no
`llvm-mos/build` checkout locally, so none of this was confirmed by compiling C.

- **Jump tables.** `MOSTargetLowering` caps a table at 256 entries (the index lives in
  an 8-bit register) and rejects ranges over 256 in `isSuitableForJumpTable`
  (`MOSISelLowering.cpp` ~L52-66). `MOSLegalizerInfo::legalizeBrJt` (~L2310-2350)
  lowers `G_BRJT` to one of two forms:
  - **Split tables:** the table is two byte arrays (lo, hi). Two `G_LOAD_ABS_IDX`
    loads fetch the halves, `G_MERGE_VALUES` joins them into a 16-bit pointer, and
    `G_BRINDIRECT` jumps to it.
  - **65C02-style subtargets** (`hasJMPIdxIndir`, at most 128 entries): shift the index
    left one bit and emit `G_BRINDIRECT_IDX`, selecting to `JMPIdxIndir` (`jmp (abs,x)`).
- **Binary search, bit tests, clustering.** MOS adds no switch lowering of its own
  beyond the jump-table limit. GlobalISel's `IRTranslator` uses the shared `SwitchCG`
  machinery (`SwitchLoweringUtils`), which groups cases into clusters and picks a jump
  table, bit tests, or compare-and-branch. For compare-and-branch it splits the
  clusters into a balanced tree (`splitWorkItem`). Only the hooks were confirmed in
  `IRTranslator.cpp`; the cluster-splitting logic itself was not read.

The earlier claim in this project that llvm-mos has no jump-table lowering was wrong.

### The idea, in two independent steps

1. **Binary search** (translator-only, no new Irie ops). Sort the (value, target)
   pairs, compare against the median with a signed/unsigned `<`, and recurse; fall to
   equality tests at the leaves. Gives O(log n) dispatch. Lowest-risk first step.
2. **Jump table** for dense tables. Needs new pieces in Irie:
   - an indirect-branch op (`jmp (abs)`) and an indexed absolute load (check whether
     `mos6502.lda.abs.x` and friends already exist; llvm-mos's `G_LOAD_ABS_IDX` is the
     analogue);
   - jump-table data emitted as lo and hi byte arrays;
   - a block-address operand, so a global's initialiser can name a block's label;
   - a selection heuristic (density, entry count, `-Os` size cost) in the layer that
     owns it. In llvm-mos that is generic `SwitchCG`, not the translator, so decide
     whether Irie wants a generic `cf.switch` that the target legalizes, instead of
     the front end committing to a strategy.

Design question to settle first: add a multiway `cf.switch` terminator to the Cf
dialect (mirrors `G_BRJT` / LLVM `switch`; the legalizer picks the strategy per
target) versus keeping the lowering in the IL translator. The first keeps the front
end simple and lets `iriec` lit tests exercise it.

### Unknowns / things to check before starting

- Whether the instruction-count and byte-size scoreboard has any switch cases. If not,
  add some (small dense, large dense, sparse, byte selector) before tuning.
- Break-even entry count for a table versus a chain or tree, in bytes and cycles.
  The split lo/hi table costs about 2n bytes of data plus about 10 bytes of dispatch.
- Whether the 65C02 `jmp (abs,x)` form matters for the BBC Micro target (6502 or
  65C02 depending on the model).
- Block placement and `TailDuplicationPass` interactions with a block that is only
  reachable through an indirect branch.

### References

- Irie: `IlToMirTranslator.cs` (`TranslateSwitch`, `ReadSwitchTargets`).
- llvm-mos: `llvm/lib/Target/MOS/MOSISelLowering.cpp`, `MOSLegalizerInfo.cpp`
  (`legalizeBrJt`), `MOSInstrGISel.td` (`G_BRINDIRECT_IDX`);
  `llvm/lib/CodeGen/SwitchLoweringUtils.cpp`, `GlobalISel/IRTranslator.cpp`.
