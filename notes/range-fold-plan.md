# Range folding: fold compares and branches that value ranges decide

Status: proposed. Written 2026-10-19. Follow-up to [range-narrowing-plan.md](range-narrowing-plan.md)
("Static folding of compares and branches that ranges decide", Oscar64's `SimplifyIntegerRangeRelops`).
Self-contained.

## Goal

`ValueRangeAnalysis` already knows the range of every integer vreg, including the refinement a branch
applies on each of its edges. `RangeNarrowingPass` uses it only to shrink operand widths; it never
decides a compare. A compare whose outcome the ranges fix (a `zext i8` tested `< 256`, a loop counter
tested against a bound it provably never reaches) still costs a compare, a branch and a dead arm all the
way to the emitted code.

Three small passes, each doing one thing, remove it:

1. **`RangeFoldPass`**: replaces an `arith.cmpi` that the operand ranges decide with an `i1` constant.
2. **`ConstantBranchFoldPass`**: turns a `cf.cond_br` on a constant condition into a `cf.br`.
3. **`UnreachableBlockEliminationPass`**: removes blocks no longer reachable from the entry block.

Only the first knows about ranges. The second needs only constants, so it also folds constants from the
front end and `ArithSimplifyPass`. The third needs only the CFG, so it can run wherever a pass may orphan
blocks.

## Non-goals

- General jump threading or sparse conditional constant propagation.
- Re-running the group until nothing changes (see "Cascades").
- Sharing analysis results between passes. `RangeFoldPass` recomputes `ValueRangeAnalysis.Compute`; the
  call is the single seam to replace when a shared, invalidating analysis cache exists.
- Changing `RangeNarrowingPass`.

## Design

### Pipeline

In `PipelineBuilder.AddStandardPipeline`
([PipelineBuilder.cs](../src/Irie/Passes/PipelineBuilder.cs)):

```
ArithSimplify -> RangeNarrowing -> RangeFold -> ConstantBranchFold -> UnreachableBlockElimination
              -> SinglePredecessorBlockParams -> HoistCommonCode -> ...
```

`RangeFold` runs after `RangeNarrowing` so it sees the cleaned-up, narrowed operands. It must run before
`SinglePredecessorBlockParams`, which erases the block parameters that carry per-edge ranges. Update the
pass-order comment at the top of `PipelineBuilder` too.

### `ValueRange.TryDecide`

A pure helper on `ValueRange`
([ValueRange.cs](../src/Irie/Mir/ValueRange.cs)): `bool? TryDecide(ArithCmpPredicate predicate, ValueRange other)`.
Returns true or false when the predicate holds for every / no pair of values drawn from the two ranges,
null otherwise.

- Signed predicates compare `Lo`/`Hi` directly.
- Unsigned predicates are decided only when both ranges are non-negative; a range that may be negative is
  a large unsigned value, so it stays undecided.
- `eq` is false when the ranges are disjoint and true when both are the same single value; `ne` is the
  inverse.

### `RangeFoldPass`

`MirFunctionPass`. Computes `ValueRangeAnalysis.Compute(function)`, then for each
`arith.cmpi %r, predicate, %a, %b` with both operand ranges known and `TryDecide` non-null, builds an `i1`
`arith.constant`, calls `function.ReplaceAllUsesOfRegister(result, constant)` and removes the compare.
Uses of the result in `BlockTarget` arguments are covered by `ReplaceAllUsesOfRegister`.

### `ConstantBranchFoldPass`

`MirFunctionPass`. For each `cf.cond_br %c, bbT(args), bbF(args)` where `%c` is defined by an
`arith.constant`, replaces the terminator with `cf.br` to the taken target, keeping that target's block
arguments. The untaken edge and its arguments disappear with the old instruction. Leaves the now-dead
constant to dead-code removal (or removes it if unused).

### `UnreachableBlockEliminationPass`

`MirFunctionPass`. Calls `RebuildCfg`, walks from `Blocks[0]`, removes every unvisited block with
`function.Blocks.Remove`, then calls `RebuildCfg` again so predecessor and successor lists are right for
later passes. Removing a block drops only its own outgoing edges, so every remaining edge still matches
its target's parameter list. Blocks that stay reachable but now have one incoming edge are tidied by
`SinglePredecessorBlockParamsPass`.

## Steps

- [x] 1. `ValueRange.TryDecide` plus unit tests in `Irie.Tests`: signed and unsigned boundaries, a
      range straddling zero under an unsigned predicate (undecided), `eq`/`ne` with disjoint and equal
      single-value ranges.
- [x] 2. `UnreachableBlockEliminationPass` plus lit test `UnreachableBlockElimination.irie`
      (`--run-pass UnreachableBlockElimination`): an orphan block removed, a chain of orphans removed, a
      loop reachable only from itself removed, reachable blocks untouched.
- [x] 3. `ConstantBranchFoldPass` plus lit test `ConstantBranchFold.irie`: constant true and false
      conditions, taken target with block arguments, a non-constant condition left alone.
- [ ] 4. `RangeFoldPass` plus lit test `RangeFold.irie`: `zext i8` against 256 (`ult` true, `uge` false),
      `eq` of disjoint ranges, a compare inside a loop that branch refinement decides, an unsigned
      predicate on a possibly negative range (stays), overlapping ranges (stay). Complete `CHECK`
      listings.
- [ ] 5. Register the three passes in `PipelineBuilder`; add an end-to-end lit test in
      `src/DotNetro.Compiler.Tests/Lit` (a byte compared against a constant it can never reach, with
      `DIFF: dotnet emulated`) so the folded program is proven to run the same.
- [ ] 6. Run `dotlit --regenerate` over `src/Irie.Tests/Lit` and `src/DotNetro.Compiler.Tests/Lit`
      (build the configuration first) and review every golden diff: changes are expected only where a
      range decides something. Run `dotnet test --solution src` in Debug and Release. Re-run
      `irie-report` and the benchmarks and record the instruction and byte deltas.
- [ ] 7. Docs: class remarks on each new pass, the pass-order summary in `src/Irie/AGENTS.md`, the
      pass-order line in `AGENTS.md`, `doc/irie/llvm-mos-comparison.md` if its Oscar64 table lists the
      gap, and tick the follow-up in [range-narrowing-plan.md](range-narrowing-plan.md).

## Risks

- **Signedness**: the classic bug. Unsigned predicates are decided only for non-negative ranges, and the
  step 1 unit tests pin the boundaries.
- **Stale CFG**: later passes read `Predecessors`/`Successors`. The elimination pass rebuilds them after
  removing blocks.
- **Cascades**: `RangeFold` decides on ranges computed before any folding. Removing a dead edge can
  tighten a later block parameter and decide another compare, which one run misses. Accepted for now;
  re-running the group is cheap once analyses can be shared and invalidated. State this in the pass
  remarks.
- **Soundness**: a wrong range silently miscompiles. The interpreter oracle and the emulator diff from
  the range-narrowing plan cover the new folds; do not skip them on any regenerated test.
