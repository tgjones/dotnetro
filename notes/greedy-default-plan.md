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

3. **INVESTIGATED (2026-07-01): `memory/global-rw` (13 vs 10) is NOT a lost fold —
   it is the same placement-arbitration problem as step 2.** The `adc.abs` /
   `adc.abs @counter+1` load-folds are BOTH intact in the greedy output; the fold
   hypothesis is falsified. The 3 extra instructions are pure register placement.
   Single-block. The result `%3` is used twice (`st.abs @counter` + `core.return`),
   so its two byte-results relocate off `$a` (the adc reg) into `%30` (low) and
   `%31` (high). The return needs low in `$a` and high in `$x`. `%30`'s downstream
   copy target is `$a` (return-low, excluded — it is the adc register), so `%30` has
   no valid GPR copy-hint and falls to the blind `_shortRangeGprs` order `[$x,$y,$a]`
   → grabs **`$x`**. `%31`'s downstream copy target genuinely IS `$x` (return-high),
   but `%30` (lower id, processed first) already took it, so `%31` → `$y`, and the
   return then needs a `$y→$x` move with no direct path → `sty $02 ; ldx $02` bounce
   (+2), plus the high arg `%5` gets spilled to zp (`stx $02 ; lda $02`, +1 vs the
   colourer's `txa`). llvm gets low→`$y` / high→`$x` because its
   `getRegAllocationHints` + assignment order lets the value that genuinely wants
   `$x` (`%31`) claim it and pushes the hintless value (`%30`) to `$y`. The reverted
   step-2 scoring did NOT fix this (global-rw stayed 13): `%30` still processes first
   and its only copy partner `$a` is excluded, so it still grabs `$x`. **Root cause =
   greedy assigns largest/lowest-id first with no global view of which register a
   *later* interfering value needs more — the region-split / SpillPlacement lever
   (step 5), not a fold fix.** So step 3 folds into step 5; there is nothing
   independent to fix here.

   **Also TRIED + REVERTED (2026-07-01): missed-hint eviction (`RAGreedy::tryAssign`
   + `canEvictHintInterference`).** Added the llvm path where a value that finds a
   free non-hint register instead evicts an EQUAL-weight interference off its copy
   hint (the normal evict rung needs strictly lighter), gated on the victim being
   spillable / older-cascade / reassignable / not holding that reg as its own
   satisfied hint. Measured: global-rw 13→**11** (the `%31`-reclaims-`$x` fix worked)
   but loop-counter 42→**46** — net **327**, +2 worse. AND it is UNFAITHFUL: llvm's
   `canEvictInterferenceBasedOnCost` REFUSES hint-eviction when evictor and victim
   are both single-block (`!MaxCost.isMax() && IsLocal && intervalIsInOneMBB(Intf)`,
   `EnableLocalReassign` off by default) — global-rw is single-block, so llvm does
   NOT use hint-eviction there; it gets that result from `getRegAllocationHints` +
   assignment order. Adding the faithful local guard loses the global-rw win and
   keeps the loop-counter loss = strictly worse. Reverted; tree clean at 325.

   **CONSOLIDATED CONCLUSION (2026-07-01, after 3 measured attempts): individual
   faithful greedy mechanisms wash out in isolation.** Copy-cost hints (327/416),
   hint-eviction (327) each help 1–2 corpus cases and hurt 1–2 others — because the
   mechanisms are coupled and llvm's quality comes from ALL of them running together
   WITH region split and the exact cost model (lexicographic `EvictionCost`,
   local-range guards, accurate `BlockFrequency` weights, and constant
   rematerialization). There is no cheap subset. Do not keep bolting on single
   mechanisms — the next real move is step 5 as a coherent whole, or accept the +10
   residual and NOT flip the default.

4. **BLOCKED (greedy is +10 behind the colourer: 325 vs 315).** Flip the default +
   delete the colourer — only once greedy ≥ colourer on the corpus. Steps 2–3 did
   not close the gap (they fold into step 5), so this stays blocked until step 5
   lands. Make `useGreedy` the default (drop the gate), then in ONE change delete
   `GraphColouringAllocator`, the 3 isel load funnels, and
   `InsertRelocationCopiesForConstrainedDefs` (funnel removal is free once greedy
   splits the load results), plus the dead split-insertion kinds. Regenerate every
   pinned golden with `dotnet run --project src/DotLit -- --regenerate` and verify
   under `dotnet test` (both Debug and Release).

5. **THE CONFIRMED LEVER (promoted from "optional" 2026-07-01).** Steps 2–3 proved
   the whole residual is here. Region split as a coherent whole:
   - **`getRegAllocationHints` + hint-driven assignment/eviction** with llvm's EXACT
     cost model (lexicographic `EvictionCost`; the single-block local-range guard;
     accurate `BlockFrequency`-weighted spill weights) — NOT the piecemeal versions
     tried in steps 2–3, which regress because they lack the guards.
   - **`tryRegionSplit`** (EdgeBundles + SpillPlacement): decide GPR-vs-zp PER REGION
     globally instead of per-value greedily — the thing that lets a carrier hold a
     GPR across blocks without over-subscribing the 3 GPRs.
   - **Rematerialization of constants** in the spill path so a cross-block
     `arith.constant 0` is re-emitted (`ldy #0` / `cpx #0`) instead of parked in a
     register/zp (early-return).
   - a real SlotIndex/segment SplitKit (llvm `SplitEditor` proper) → then
     `RegisterCoalescerPass` can finally be wired into the greedy path for the last
     few %. NOTE: do NOT wire the aggressive coalescer into greedy before that — it
     CRASHES greedy (the use-redirect splits can't separate a coalesced multi-role
     mega-vreg; falsified-hypothesis in memory).

   This is a substantial, coherent piece — scope it as its own plan, not incremental
   bolt-ons (the lesson of steps 2–3).

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
