# Range narrowing: shrink i32 arithmetic, compares and loop variables to the bytes they need

Status: in progress. Written 2026-10-19 after comparing Oscar64 and llvm-mos (see "Background").
Self-contained.

## Goal

C# promotes everything to `int`, so the translator emits `zext i8→i32`, then `addi`/`subi`/`cmpi` in
i32, and the legalizer splits each into four byte operations. Most of those values need one or two
bytes. A forward **value-range analysis** on generic SSA proves how many bytes a value needs, and a
rewrite shrinks the operation, the compare, or the block parameter to that width.

Success is measured in bytes: **the total size of the lit programs goes down, and no lit program
grows.** Baseline (BBC, Debug, `dnrc --emit program`, all 114 `DotNetro.Compiler.Tests/Lit/*.cs`):
**151,668 bytes**. Evidence that it is worth it: hand-narrowing the `ForLoop` counter to i8 took
`Main` from about 63 instructions to about 34 (counter in `$24..$27` becomes one register; the
four-byte `EOR #$80` compare ladder becomes `CMP #5 / BCS`).

## Decisions already made

- **Full range-based approach**, in two phases: straight-line narrowing first, then loops and block
  parameters.
- **Where**: a new generic-SSA pass, `RangeNarrowingPass`, **before** `SinglePredecessorBlockParamsPass`
  (that pass replaces single-predecessor block parameters by their edge arguments, which erases the
  vregs that carry per-edge refined ranges) and after `ArithSimplifyPass` (constants canonical).
  Everything downstream is unchanged: the legalizer splits the narrower ops and the existing
  constant-0 bytes plus DCE do the rest.
- **Ranges appear in the MIR text** (below), and the reference interpreter checks them as a
  soundness oracle.
- **Out of scope**: interprocedural ranges; folding compares and branches that a range decides (a
  cheap follow-on); the `WriteDigits` follow-up (see the end).

## Background (from the research)

- **Oscar64** keeps IR types wide: `IntegerValueRange` (min/max, with unknown/weak/bound/unbound
  states) on every operand, refined separately on the true/false successor of a branch, joined at
  loop heads with a fixpoint plus widening and a pattern-match for counted loops. The native code
  generator tests `IsUByte`/`IsSByte` per emission site. It does not narrow the IR, so a value in
  [4..1024] still shifts three bytes.
- **llvm-mos** narrows in generic IR (InstCombine, CVP/LVI, IPSCCP, and the MOS-specific
  `MOSIndexIV` / narrow-IV preference), then drops the facts at MIR; `(long)a+b` proves [0,511] and
  still emits a 4-byte chain. Its MIR-level folding of known-zero bytes is only
  `matchFoldAddE` (all inputs constant).
- **Why neither rewrites `op_N` to `ext(op_M)` generally**: upstream LLVM canonicalizes wide, keeps
  facts as `nuw`/`nsw`/`range` flags, and C `int` is 16 bits on that target so the payoff is
  smaller. Ours is 32 bits, so the payoff is larger. Risks are in the last section.

## Design

### The range

`ValueRange` is a closed interval `[lo, hi]` over the **signed reading** of the vreg's width,
computed in `Int128` (so i64 is representable). Full range for the width means "unknown". An
operation whose mathematical result can leave the type's range gives the full range (wrapping is
modular, so this is sound). Unsigned values of 2^(N-1) or more read as negative; they only matter
when the range straddles the sign boundary, in which case it is the full range.

Helpers: `FitsUnsigned(bits)` (lo ≥ 0 and hi < 2^bits) and `FitsSigned(bits)`. The smallest byte
multiple M < N that fits decides the target width; prefer the zero-extended form when `lo ≥ 0`
(`zext` is free, `sext` costs a branch or a sign broadcast).

### Seeds and transfer functions

Seeds: `arith.constant`; `cast.zext` of iM → `[0, 2^M-1]`; `cast.sext` → `[-2^(M-1), 2^(M-1)-1]`;
`cast.trunc` (identity if the source range fits, else full); loads of i8/i16 widths; `lshr` by a
constant; `and` with a non-negative constant; `urem` by a constant; function parameters of narrow
type; i1 results `[0,1]`.

Transfers: `addi`, `subi`, `muli`, `shl` by constant, `and`/`or`/`xor` (non-negative operands),
`lshr`/`ashr` by constant, `udivi`/`urem` by constant, `select` (join), `cmpi` (i1), and block
parameters (join of the incoming edge arguments). Anything else, including call results and
non-constant shift amounts, is the full range.

### Branch refinement (phase 2)

On `cf.cond_br %c, T(args), F(args)` where `%c = cmpi pred x, y` (a compare defined in the same
block), each edge argument that is exactly `x` or `y` gets its range intersected with the bound the
predicate implies on that edge (`slt`, `ult`, `sle`, `ule`, `eq`, `ne`, and their inversions on the
false edge). The bound uses the other operand's `lo`/`hi`. The result is the range of the **edge
argument value**, which is what the target block parameter joins; it is not stored on the original
vreg.

### Loops: widening then narrowing

Process blocks in reverse post-order, iterate to a fixpoint. After a bounded number of rounds at a
block parameter (suggest 2), a bound that is still moving jumps to the type's limit (widening). Then
run one or two narrowing rounds in which branch refinement pulls the bounds back. Worked case:
`for (i = 0; i < len; i++)` with `len ∈ [0,65535]` widens `i` to `[0, +inf)`, the `slt i, len`
refinement on the body edge gives `[0,65534]`, and `i+1 = [1,65535]`, so the header parameter joins
to `[0,65535]` and the counter is i16. Without widening the iteration takes 65,535 rounds.

### Rewrite rules (soundness: each is exact when the range is right)

Let `r = op_N(a, b)` with `range(r)` fitting M < N bits:

- **Arithmetic**: add/sub/mul/shl/and/or/xor → `ext(op_M(trunc a, trunc b))`, with `zext` if
  `lo ≥ 0` else `sext`. Modular arithmetic makes the low M bits correct for any operand values; the
  range makes the extension restore the high bits.
- **Shifts and division**: `lshr`/`udivi`/`urem` need the operand range to fit M unsigned;
  `ashr`/signed forms need it to fit M signed. Then `ext(op_M(trunc a, …))`.
- **Compare**: if both operand ranges fit M (both unsigned, or both signed), `cmpi pred a, b` →
  `cmpi pred' (trunc a), (trunc b)` at width M, using `ult`/`ule`/etc. for the unsigned case.
- **Block parameter** `p : iN` with range fitting M → `p' : iM`; every incoming edge passes
  `trunc arg`; at the top of the block `p = ext(p')` (zext/sext as above). The cleanup peephole
  below removes the `trunc`/`ext` pairs.
- **Cleanup peephole** (local, in the same pass): `trunc(zext x)` / `trunc(sext x)` of matching or
  wider target → `x` or a narrower `zext`/`sext` of `x`; `zext(trunc y)` where `y`'s range already
  fits → `y`'s extension; dead extensions removed.

Narrowing `muli` also switches the libcall from `__mulsi3` to `__mulhi3`, and a program that no
longer calls `__mulsi3` stops linking it.

### Cost check

A narrowed result that is consumed at full width needs an `ext`. For `zext` this is free (a
constant-0 byte). For `sext` it costs a branch or a broadcast, so the first version should rewrite
to a `sext` result only when the consumers are themselves narrowed, and otherwise prefer leaving
the operation wide. Decide the exact rule by measuring `SignedSum`-style cases in step 6.

### MIR text representation

```
%5 : i32 in [0, 766] = arith.addi %3, %4
bb2(%1 : i32 in [0, 65535], %2 : ptr):
```

- Closed interval, signed reading, after the type in `%id : type`.
- Printed only when narrower than the type's full range, so output for MIR that no pass has
  annotated is unchanged.
- The parser accepts it, so `--start-at` and `--run-pass` tests can hand-write ranges.
- Stored on the annotation: `TypedVReg(IRType Type, ValueRange? Range = null)`
  (`src/Irie/Mir/VRegAnnotation.cs`; parsed in `MirParser.cs`, near `ParseAnnotation`).
- A pass that replaces a definition creates a fresh vreg and does not carry a range over; the
  legalizer's fresh byte vregs therefore carry none, and post-legalizer goldens are unaffected.

### Soundness oracle

`MirInterpreter` (`src/Irie.Tools.Interpreter`) asserts, for every vreg that has a range, that each
value it computes lies inside it, and throws `InterpreterException` otherwise. The existing
interpreter-based execution tests then check the analysis. The emulator-diff lit tests
(`DotNetro.Compiler.Tests`) check the rewritten programs end to end.

## Steps

Keep each step green (`dotnet test --solution src`) before the next.

1. **[x] Range type and text form.** Add `ValueRange`, the `TypedVReg` field, the writer and the
   parser (`in [lo, hi]`), and round-trip tests in `Irie.Tests/Mir`. No pass yet.
2. **[x] Interpreter oracle.** Make the interpreter assert ranges. Test with hand-written MIR:
   one whose range holds, one whose range is deliberately wrong and must throw.
3. **[x] Analysis, straight-line.** `Passes/Analyses`: seeds and transfer functions for a function
   without loops (a single pass over reverse post-order; block parameters join their edges, and a
   loop-carried block parameter is the full range for now). Add `--run-pass RangeNarrowing`
   `.irie` lit tests (this needs the pass in the pipeline, so step 3 already adds it after `ArithSimplifyPass`; it only annotates, and constants are not annotated) whose `CHECK` lines pin the printed ranges. Include i64 and signed cases.
4. **[x] Rewrite: arithmetic and the cleanup peephole.** Add the rules for add/sub/mul/shl/bitwise,
   and the trunc/ext cleanup (zero-extended results only; a result with a negative range stays wide until step 6's cost rule; step 4 also merges duplicate casts within a block). The pass is already in `PipelineBuilder.AddStandardPipeline` after `ArithSimplifyPass` (step 3). Result: 151,668 → 150,419 bytes, no program grew (`notes/measure-lit-sizes.sh`, added in step 4). Expect `ByteArithmeticPromotion`, `TernaryMixedWidth` and
   `SwitchByte` to shrink. Regenerate affected goldens with `dotlit --regenerate` and **read every
   diff**.
5. **[ ] Rewrite: compares, shifts, divisions.** Narrow `cmpi` (check that `LegalizeCmpI` and
   `PlacementChecker` handle the narrower shapes), then `lshr`/`ashr`/`udivi`/`urem`, then the
   `__mulsi3`→`__mulhi3` switch. Expect `NarrowIntegerCompare`, `CompareBranch*` and `MulInt32`
   to change.
6. **[ ] Cost check and measure.** Implement the extension-cost rule. Add a script (replace the
   deleted `notes/measure-placement.sh`) that compiles every lit program with `dnrc --emit
   program` and prints per-program and total bytes. Compare to the 151,668 baseline. Any program
   that grows is a defect to fix or an explained exception.
7. **[ ] Phase 2: loops and block parameters.** Add widening and narrowing at block-parameter joins,
   branch refinement, and the block-parameter shrink rewrite. Expect the largest wins here:
   `ForLoop`, `ArrayLoopSum`, `OptWhileLoop`, `ArrayByte`, string indexer loops. Check how the beam
   placer treats narrower parameters, and re-run the suite for placement regressions.
8. **[ ] Regenerate, review, document.** `dotlit --regenerate` for both lit suites; review the
   complete diff; re-run `dotnet test --solution src` in Debug and Release. Document the pass in
   its class comment, add it to the pass-order summary in `src/Irie/AGENTS.md`, and note the
   result in `doc/irie/llvm-mos-comparison.md` if the Oscar64 comparison table lists the gap.

## Risks

- **Soundness**: a wrong range silently miscompiles. Mitigation: the interpreter oracle from step
  2 and the emulator diff on every lit program. Do not skip the oracle on any rewritten test.
- **Signed vs unsigned**: picking `zext` or `sext` wrongly is the classic bug. The signed
  canonical reading and the oracle catch it.
- **Extension cost can enlarge a program**: see the cost check; the per-program size script is the
  guard.
- **Phi-web consistency**: shrinking a block parameter must be mirrored on every incoming edge,
  including two edges from one `cond_br` to the same block.
- **Interaction with other passes and the placement search**: `ArithSimplify` patterns,
  `HoistCommonCodePass`, and beam placement of narrower parameters are unchecked assumptions to
  verify in steps 4 and 7.
- **Golden churn**: any MIR golden that prints generic-dialect MIR after this pass will now show
  ranges. Enumerate them in step 4 by running the suite, not by assumption.

## Follow-up tasks (not part of this plan)

- **`WriteDigits` in the corelib** (`src/DotNetro.CoreLib/System/Console.cs`): range analysis cannot
  help, because the quotient estimate is a real 32-bit computation and the remainder bound is a
  relational fact. Declaring `var remainder = (byte)(value - ((quotient << 3) + (quotient << 1)));`
  is valid (the true remainder is at most 15) and cut a standalone copy from 311 to 287
  instructions with today's compiler. Apply, run the lit suite, and measure.
- **Static folding of compares and branches** that ranges decide (Oscar64's
  `SimplifyIntegerRangeRelops`).
- **Interprocedural return and argument ranges** for non-escaping static methods.
