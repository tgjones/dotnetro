# Plan: make the greedy allocator the one-and-only RA

**Goal.** Retire to a single register allocator = `GreedyRegisterAllocator` (the
llvm-mos `RegAllocGreedy` port), matching or exceeding llvm-mos codegen quality,
then delete the graph-colourer and its scaffolding. This supersedes the briefly-
considered SSA-allocator end-goal (rejected: its homogeneous-k theory is the wrong
fit for the heterogeneous, cost-dominated 6502; gcra / puzzle-solving / PBQP likewise
solve FIT/aliasing, not the COST/splitting problem that is llvm-mos's actual edge).

## Why greedy (one-paragraph rationale)

The project is anchored to llvm-mos, which uses RegAllocGreedy; the 46-case corpus +
`doc/irie/llvm-mos-comparison.md` scoreboard are calibrated to its output, so greedy
is the only allocator whose design questions have a canonical "read the reference"
answer. The 6502's hard problem is COST (which of 3 scarce real regs vs costlier
zero-page; holding a carrier in a GPR across blocks), not FIT — and greedy's
live-range splitting + `getRegAllocationHints` copy-cost scoring is a cost mechanism
the colourer, SSA, gcra and PBQP all lack natively.

## Verified state (main, 2026-06-30)

- Greedy is reachable in the iriec pipeline via **`IRIE_GREEDY=1`** (selector added
  this session in `src/Irie.Tools.Compiler/Program.cs`; passes `useGreedy: true` to
  `RegisterAllocatorPass`). Unset = graph-colourer (default + validation oracle).
- SplitKit S1–S3 (value numbers, `SplitAtPoint`, relocation routed through it) and
  `RegisterCoalescerPass` (gated `IRIE_COALESCE=1`) have landed on main.
- Greedy converts the **same 17/47** corpus cases as the colourer — **no crashes /
  no coverage regression**.

### Baseline (reproduce: `IRIE_GREEDY=1 dotnet run --project src/Irie.Tools.Reference --no-build -- generate-report --out <scratch>`; omit the env for the colourer)

| metric | colourer (default) | greedy |
|---|---|---|
| aggregate vs llvm-mos | **315 / 344 = −8.4%** | **325 / 344 = −5.5%** |

Greedy is **+10 instructions worse than the colourer**, and the entire gap is **4
cross-block placement cases** (carrier parked in zp where llvm/colourer hold a GPR):

| case | llvm | colourer | greedy | greedy − colourer |
|---|---|---|---|---|
| memory/global-rw          | 10 | 10 | 13 | **+3** |
| control-flow/early-return | 28 | 29 | 33 | **+4** |
| realistic/fibonacci       | 50 | 51 | 52 | **+1** |
| control-flow/loop-counter | 65 | 40 | 42 | **+2** |

Everything else is byte-for-byte parity. Greedy still beats llvm-mos on aggregate;
the 4 cases above are where it is *worse than llvm* (global-rw 1.30, early-return
1.18) — so they are simultaneously the "beat the colourer" and the "match llvm" gap.

### Root cause

`GreedyRegisterAllocator.PreferenceOrder` applies the GPR-ahead-of-zp preference
ONLY to `_splitProducts` (instruction-local relocation temps). Cross-block CARRIERS
(a value live across blocks) miss it, so they fall to the `Anyi8` default order,
which puts the abundant zp pool ahead of the 3 scarce GPRs → carrier lands in zp
(`lda`/`sta`, 2B) instead of a GPR (`tay`/`tya`, 1B). The faithful llvm fix is the
`getRegAllocationHints` SUMMED copy-cost SCORE per candidate (score each candidate by
total move cost across the value's copies), not a binary "is-carrier → prefer GPR"
flag — that blunt version (C1) was tried and reverted as net-neutral (helped
early-return/loop-counter, hurt if-else/global-rw, total unchanged).

## Steps

1. **DONE (this session): re-establish the measurement loop.** `IRIE_GREEDY=1`
   selector wired; baseline captured above.

2. **Close the cross-block residual — faithful summed copy-cost hints.** In
   `GreedyRegisterAllocator.PreferenceOrder` (and/or `CopyHintColours`), score each
   candidate physreg by the total copy cost across the value's copies (GPR↔GPR =
   `tay`/`tya` cheap; GPR↔zp = `lda`/`sta` dearer), mirroring
   `MOSRegisterInfo::getRegAllocationHints`. Apply to cross-block carriers, not just
   split products. Re-run the report after each change; **target greedy ≤ 315 (≥
   colourer) and ideally ≤ llvm on the 4 cases.** Determinism: every tie-break on
   ascending vreg id.

3. **Investigate `memory/global-rw` (13 vs 10) specifically.** It is the load-fold /
   relocation case, not pure carrier placement — confirm whether the regression is
   placement or a lost `adc.abs`/`lda` fold once the value is relocated; fix at root
   (keep the relocated load result foldable) rather than papering with a hint.

4. **Flip the default + delete the colourer — only once greedy ≥ colourer on the
   corpus.** Make `useGreedy` the default (drop the gate), then in ONE change delete
   `GraphColouringAllocator`, the 3 isel load funnels, and
   `InsertRelocationCopiesForConstrainedDefs` (funnel removal is free once greedy
   splits the load results), plus the dead split-insertion kinds. Regenerate every
   pinned golden with `dotnet run --project src/DotLit -- --regenerate` and verify
   under `dotnet test` (both Debug and Release).

5. **Optional later (only if measured to help):** a real SlotIndex/segment SplitKit
   (llvm `SplitEditor` proper) → then `RegisterCoalescerPass` can finally be wired
   into the greedy path for the last few %. NOTE: do NOT wire the aggressive
   coalescer into greedy before that — it CRASHES greedy (the use-redirect splits
   can't separate a coalesced multi-role mega-vreg; falsified-hypothesis in memory).

## Guardrails

- Keep the colourer as the oracle until greedy provably wins on the corpus; only then
  delete it.
- Trust `dotnet test`, not a standalone `iriec` run (stale-build trap).
- The corpus needs the llvm `.s` files (`ext/llvm-mos-reference/build.sh`).
- 30/47 corpus cases are still "blocked" (no Irie test yet) — out of scope here, but
  they cap how much of the picture the scoreboard shows.

## References

- llvm-mos: `RegAllocGreedy.cpp` trySplit ladder, `SplitKit.{h,cpp}` SplitEditor,
  `MOSRegisterInfo::getRegAllocationHints`, `MOSInsertCopies.cpp`
  (at `/Users/timjones/Code/llvm-mos`).
- Code: `src/Irie/Passes/GreedyRegisterAllocator.cs` (`PreferenceOrder`,
  `CopyHintColours`, `_splitProducts`), `src/Irie/Passes/RegisterAllocatorPass.cs`
  (`useGreedy` dispatch + spill loop + `InsertRelocationCopiesForConstrainedDefs`),
  `src/Irie/Passes/SplitEditor.cs`.
- Memory: `project_greedy_ra_plan` (full greedy history), `project_ssa_ra_direction`
  (the end-goal decision + why SSA/gcra/puzzle/PBQP were rejected as primary),
  `project_ra_redesign_corpus` (corpus + scoreboard).
