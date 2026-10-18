# Beam placement: choose a join's entry state from all of its incoming edges

Status: not started. **Do this after the register-allocator design
([register-allocator-design.md](../doc/irie/register-allocator-design.md)) is finished**,
including S4.5 (making Beam the default and deleting the colouring allocator). Written
2026-10-18, after the `pressure-high` / `early-return` look at the within-block burn-down
([placement-burndown-plan.md](placement-burndown-plan.md)).
Self-contained: the design doc is background only (§3.5, "Placement across blocks").

## The problem

A join (a block with several incoming edges, a loop header included) starts from the
registers its **primary** edge left; every other edge reconciles to that state. The primary
is the incoming edge that comes first in reverse post-order (`BeamPlacer.FindInheriting`,
`_primary`), and its candidate states are priced by `SuccessorCost`, which places the join's
first stretch. That price sees the primary edge and the join, **not what the other edges
will pay to match**: they are placed later and their exit states do not exist yet.

When two candidate states cost the same on the primary edge, the choice is arbitrary
(in practice, whatever is already in place), and the difference shows up on the edges
placed afterwards.

### The case that shows it: `control-flow/early-return`

Colouring 26 instructions, beam 27 (bytes 39 vs 42). Four edges reach the merged return
block `bb7(lo, hi)`, whose tail copies `lo` to `$a` and `hi` to `$x`:

| Edge | Colouring (join state `Y=lo, X=hi`) | Beam (join state `Y=lo, A=hi`) |
|---|---|---|
| `a + b + c` (primary, first in RPO) | `TAY` after the last `ADC` | nothing |
| `b < 0` returns `a` | `BMI .bb4` directly (`a` was repositioned at entry) | `TAY / TXA / JMP` |
| `return 0` | `LDY #0 / LDX #0 / JMP` | `LDY #0 / TYA / JMP` |
| tail | `TYA / RTS` | `TAX / TYA / RTS` |

On the primary edge the two shapes cost the same (`TAX` moves from the tail into the edge),
so the search keeps the one where the values already sit. The other edges then pay a
`TXA` that the other shape would not need. Forcing each exit mode (`ExitMode` 0–6) gives the
same output, so the enumeration of modes is not the cause.

The same shape should recur wherever a join's parameters feed copies into fixed registers:
return values (`$a`/`$x`), call arguments, and the loop-carried values of a loop whose
header feeds a call. How often it costs anything is **not measured**; step 1 below does that.

## Why not a tie-break

"Prefer the register the value is finally copied into" fixes this case and is cheap, but it
is a proxy for the missing information, not a statement of it. It is right when the
consumer is a fixed-register copy and could be wrong when it is an `ADC` operand or a
loop-carried value that a later block wants elsewhere. Adding it would also be a rule whose
only justification is this case (see `AGENTS.md`, "Don't accrete workarounds"). The plan is
to give the choice the information it lacks.

## Two designs to evaluate

**A. Demand-driven preferences (backward).** Per join parameter (and per live-in value),
compute the register its consumers want, by walking the uses: a copy into a fixed register
wants that register; an operand of `ADC` / `AND` / compare wants A; an indexed access
wants X or Y; a value passed to a further join takes that join's preference
(a small fixpoint over the CFG, as for liveness). The primary's candidate states are then
generated from, and priced with, the demanded registers; **every** edge reconciles towards
the same target, so the primary pays its move explicitly instead of getting it for free by
accident. Cheap to compute; the risk is a wrong preference (two consumers that disagree,
a register that is never free), so it needs a weight and a fallback to today's candidates.

**B. Price a candidate against all predecessors.** Place the predecessors first (or
estimate their exit states), then evaluate each candidate join state by the sum of
reconcile costs over all incoming edges, weighted by edge frequency. Exact where it
applies, but the predecessors of a join are placed in order and the join's state must be
known when the primary is placed, so this means a two-pass scheme (place, choose, replace)
or an estimate of exit states; that is the architectural part. This is the same
"choose the entry state once, reconcile the rest" structure the loop-header search (S4.3b)
uses with the latch as the second edge, so B is the generalisation of that mechanism from
one back edge to every edge.

Recommended order: measure (step 1), try A as the cheaper experiment, and move to B only
if A leaves a measurable gap or its preferences are often wrong.

## Steps

1. **Measure first.** For the corpus and the C# set, find joins where an edge's reconcile
   moves (`SatisfyEntry`) emit transfers or reloads (`TAX/TXA/TAY/TYA/LDA home`) for a
   register the join's own consumers could have had for free. A debug counter behind an
   environment variable is enough; do not commit it. Report how many joins and how many
   instructions. **Stop and report if the total is under ~10 instructions over the
   whole corpus**: then record the reason for `early-return` in the design doc and drop
   the plan.
2. Prototype A: the demand analysis (a pass-level helper beside `MOS6502HomeClasses.cs`),
   fed to `DefineEntry` so it generates the candidate that moves a parameter or live-in to
   its demanded register, in addition to today's candidates. Keep today's candidates so
   the search can still decide on cost.
3. Re-measure with `notes/measure-placement.sh beam <dir>` against a freshly measured
   post-S4.5 baseline (colouring is gone by then: compare with the numbers at the end of
   S4.5). Accept only if the corpus and C# totals do not get worse and no case gets worse
   without a written reason.
4. If A is not enough, evaluate B on a branch before committing to it; it is the part that
   needs the maintainer's agreement (a new phase in the placer).
5. Regenerate goldens that change (`dotlit --regenerate`) and read every diff; keep CHECK
   blocks strict. `PlacementChecker` must stay green. Both lit suites, Debug and Release.
6. One commit per step; comments self-contained (no references to plans or design docs).
   Update the design doc's §3.5 and the S4 progress with the result.

## Out of scope

N/Z across edges (late `cmp #0` only looks inside one block), range narrowing,
known-zero bytes, and the layout of `JMP`s between merged return blocks (the extra `JMP`
in beam's `early-return` is a consequence of the state mismatch, not a separate cause;
re-check after this work).

## Done when

`early-return` is within its colouring size or the remaining difference has a written reason,
no corpus case or C# program is worse than before the change, and the design doc describes
how a join's entry state is chosen.
