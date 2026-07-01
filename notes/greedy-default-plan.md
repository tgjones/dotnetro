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
(`lda`/`sta`, 2B) instead of a GPR (`tay`/`tya`, 1B).

**The candidate-ordering fix does not work — measured, see step 2.** The
hypothesis here was that a faithful `getRegAllocationHints` SUMMED copy-cost SCORE
would close it. It was implemented faithfully and **regressed** the corpus (327
ungated / 416 cross-block-gated, both > the 325 no-hint baseline). The real cause of
the residual is the **absence of region splitting**: greedy places each value once,
whole, largest-first, so any GPR preference (blunt C1 flag *or* faithful summed
score) over-subscribes the 3 GPRs — non-carrier values grab them first and the
carrier spills. llvm avoids this via `tryRegionSplit` (EdgeBundles + SpillPlacement),
which decides GPR-vs-zp per REGION globally, plus rematerialization of constants.
So the lever is **step 5**, not a better hint.

## Steps

1. **DONE (this session): re-establish the measurement loop.** `IRIE_GREEDY=1`
   selector wired; baseline captured above.

2. **TRIED + REVERTED (2026-07-01): faithful summed copy-cost hints — measured
   NET-NEGATIVE, so the residual is NOT a candidate-ordering problem.** Implemented
   the full `MOSRegisterInfo::getRegAllocationHints` port faithfully: a
   `TargetRegisterInfo.CopyCost(dest, src)` hook (MOS6502 override = llvm-mos
   `copyCost` under the size-Average mode: $a↔$x/$y = 3, $x↔$y = 10, GPR↔zp = 5,
   zp↔zp = 14), and a `ScoredCopyHints` prefix in `PreferenceOrder` that scores each
   allowed candidate by summed `worstCost − thisCost` over the value's copies (exact
   copy partner wins; GPRs outrank zp for a value copied to/from a GPR), ties broken
   on the allowed order. **Two variants measured, both WORSE than the 325 no-hint
   greedy baseline:**
   - **Ungated** (score every value with a known-physreg copy partner): **327**.
     Wins loop-counter 42→**39** (now beats colourer 40 *and* llvm 65), fibonacci
     52→51, select 12→11 — but loses early-return 33→**37**, many-args 36→**38**,
     if-else 32→33. Net **+2**. Root cause of the losses: without llvm's global
     region-split to arbitrate GPR pressure, the score pulls **short-lived args and
     in-block chain temps** into the 3 scarce GPRs, starving the carrier that then
     spills to zp. `many-args` (a single-block straight-line i16 chain that was
     byte-identical to llvm at 36) flipped its whole accumulator from `tay/tax` GPRs
     to `sta/lda` zp — the exact opposite of the hint's intent — because non-carrier
     values grabbed the registers first.
   - **Gated to cross-block carriers** (`referenced in ≥2 blocks`): **416**
     (+20.9%), catastrophic — forcing *long* cross-block values into GPRs makes them
     interfere with the whole arithmetic chain and spill massively.

   **Conclusion: candidate ordering is not the lever.** This confirms + sharpens the
   earlier C1 "wash" finding ([[project_greedy_ra_plan]]): the wins are real (a
   loop-carried accumulator genuinely wants a durable GPR) but greedy without
   **region splitting** (`tryRegionSplit` / EdgeBundles / SpillPlacement) cannot
   place hints without over-subscribing 3 GPRs. The residual is a **splitting +
   rematerialization** problem (early-return's regression is a rematerializable
   `arith.constant 0` held across blocks that llvm re-emits as `ldy #0` / `cpx #0`
   at each use), i.e. **step 5**, not step 2. All step-2 code reverted; tree clean at
   greedy **325**. Do not re-attempt hint-ordering tweaks — go to step 5.

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
