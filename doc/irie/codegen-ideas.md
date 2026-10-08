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
