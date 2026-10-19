# Interprocedural ranges: argument and return ranges for directly called functions

Status: proposed. Written 2026-10-20 after surveying the lit corpus, llvm-mos and Oscar64. Self-contained.
Follow-up of `notes/range-narrowing-plan.md` ("Interprocedural return and argument ranges for
non-escaping static methods").

## Goal

`ValueRangeAnalysis` works one function at a time: block-0 parameters are `Full`, and a `call.func`
result is `Full`. For a function that is only ever called directly, the compiler sees every call site
(a closed world), so it can compute the range of each argument (join over call sites) and of each
return value (join over `core.return` operands), and feed them back into the analysis. That narrows
arithmetic and folds branches inside callees, and (phase 2) shrinks the signature so call sites pass
fewer bytes.

Success is measured in bytes with `notes/measure-lit-sizes.sh`: **the total goes down and no lit
program grows.** Because the corpus is biased (see below), also report the result per program, and
do not treat a corpus win as a real-world win.

## Evidence (survey of all 116 lit programs, translated MIR)

- Sub-word parameters are already `i8`/`i16` in the MIR; only `int`/`uint`/`long` and the corelib's
  `nint` parameters and returns are candidates.
- 417 wider-than-8-bit parameters of directly called functions: 144 have only constant arguments at
  every call site, 28 mixed, 245 non-constant (some of those are small computed values the survey did
  not trace).
- Likely beneficiaries: tiny helpers called with small constants (`PrintParameter(16)`,
  `MethodWithParameterAndReturnValue(44)`, `OptWhileLoop.Count/CountDown`, `ShortCircuitAsValue`
  helpers, `Fib(60)`, `Factorial(20)`); corelib `ManagedHeap.Alloc`/`AllocArray` (`nint` size, 0..16
  in about 25 programs, but any non-constant `new T[n]` widens it back); `Console.WriteLine(int)`
  (108 programs call it; 8 pass only non-negative constants, so the `value < 0` branch, the `-`
  print and the 32-bit negate would fold).
- Will not benefit: helpers deliberately called with extreme values (`MulInt32.Mul`,
  `DivRemInt32.Check`, `Int64Compare.Check`, `Int64*.PrintLong`), and `Console.WriteDigits`
  (full-range first call; `quotient < value` is relational).
- Expect small corpus wins and little in real programs, where one non-constant caller widens the
  whole parameter. Inlining tiny helpers would capture most of the same cases.

## Precedent

- **llvm-mos**: stock LLVM IPSCCP (`SCCPSolver`, enabled in `PassBuilderPipelines.cpp`) tracks
  `ConstantRange` lattice values for arguments and returns of functions whose address is not taken
  and writes them back as `range` attributes. The MOS backend adds nothing; facts are dropped before MIR.
- **Oscar64**: `GlobalOptimizer.cpp` (~1511-1553) tracks min/max per parameter across call sites,
  only when every argument is a constant, and sets `DTF_FPARAM_RANGE_LIMITED` (used in
  `GlobalAnalyzer.cpp`). No return ranges.
- **Here**: `CallGraph` (`AddressTaken`, SCCs), `ReentrancyAnalysis` (a module-level analysis) and
  `--choose-conventions` (callee-chosen conventions for direct-only functions) are the existing
  interprocedural machinery.

## Design

### Which functions qualify

Not `AddressTaken` in `CallGraph` (no vtable or `mem.symbol` reference), has at least one direct call
site, and is not `__entry` or a runtime routine. Everything else keeps `Full` parameters. Returns
need no address-taken exclusion for callers' benefit, but an address-taken function can be called
indirectly with unknown arguments, so keep both directions to qualifying functions for simplicity.

### Phase 1: seed the analysis (no signature change)

1. `ValueRangeAnalysis.Compute(function, seeds)` takes optional ranges for block-0 parameters
   (replacing the `Full` seed at `ValueRangeAnalysis.cs:43-45`) and for call results
   (callee's return range), and also exposes the join of the `core.return` operand ranges.
2. A module pass `InterproceduralRangePass` (a `Pass`, not a `MirFunctionPass`) runs just before
   `RangeNarrowingPass`. It computes the call graph, then iterates: for each qualifying function,
   argument range = join over call sites of the argument's range in the caller; return range = join
   over returns. Order callee-first for returns and caller-first for arguments, repeat to a
   fixpoint. Recursion (SCCs) starts optimistic and widens to `Full` after a few rounds, as the
   per-function analysis does for loops; cap the rounds and fall back to `Full`.
3. `RangeNarrowingPass` then reads the seeds (stored on the function, e.g. `ParameterRanges`,
   `ReturnRange`, next to `SetValueRange`) and annotates block-0 parameters, so the existing
   interpreter oracle checks them at run time with no new machinery.

Effect: narrower arithmetic and folded branches inside callees; the ABI is unchanged, so call sites
still pass full-width arguments.

### Phase 2: narrow the signature (only if phase 1 measures well)

For a qualifying function whose parameter or return range fits M < N bits unsigned: change
`ParameterTypes` / `ReturnType`, insert a `cast.trunc` at each call site and a `cast.zext` at callee
entry (or `zext` the call result), and let the existing cast cleanup fold them. Keep consistent:
the generic verifier's call-signature check, `FunctionConvention` and `--choose-conventions`, and
`AbiLowering`. Signed-negative ranges stay wide, as in `RangeNarrowingPass`.

## Steps

1. [ ] Add seed parameters to `ValueRangeAnalysis.Compute` and expose the return-range join; unit tests.
2. [ ] `InterproceduralRangePass` with the fixpoint and widening; place it before `RangeNarrowing` in
   `PipelineBuilder.AddStandardPipeline`; document the order in `src/Irie/AGENTS.md`.
3. [ ] `.irie` lit tests (`--run-pass`): constant-argument callee, mixed call sites (stays `Full`),
   address-taken callee (stays `Full`), recursion (widens), return range feeding a caller's compare.
4. [ ] Run both lit suites; confirm the oracle accepts the annotated parameters. Regenerate goldens
   with `dotlit --regenerate` and read every diff.
5. [ ] Measure with `notes/measure-lit-sizes.sh`; record the result here. **Decision point:** continue
   to phase 2 only if phase 1 shrinks the corpus meaningfully.
6. [ ] Phase 2 (above), with its own tests and measurement.

## Risks

- **Soundness**: a wrong argument range silently miscompiles. A missed call site (an unrecognised
  call form, an address-taken function) is the main hazard; reuse `CallGraph` rather than scanning
  again, and treat any unrecognised reference as address-taken. The interpreter oracle covers
  parameters once they are annotated.
- **Corpus bias**: lit programs call helpers with constants; real programs do not. Do not justify the
  pass by corpus bytes alone.
- **Test intent**: specialising a helper to its few constant arguments weakens the tests that exist
  to exercise full-width codegen. Check that the affected lit tests still cover the codegen they were
  written for (add a non-constant call site where they do not).
- **Phase 2 ABI churn**: touches conventions, the verifier and `--choose-conventions`.
