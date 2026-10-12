# Minimal exception-handling support plan

## Goal and decisions (agreed)

The goal is **not** to support managed exceptions at runtime. It is to compile BCL (and user) methods that contain `try/finally`, `try/catch`, `using`, `foreach`, `lock`, `throw` without failing on unsupported IL opcodes. Normal-path cost matters, so nothing here may cost anything on a path that does not throw.

- **`throw` is fatal.** It halts the machine (printing a short marker). No unwinding, no unwind tables, no handler chain.
- **Handlers that can never run are not translated.** Because `throw` is fatal, no `catch`, `filter` or `fault` handler is ever entered. A `try/catch` compiles to just its `try` body. `rethrow` and `endfilter` only occur inside such handlers, so they are never reached.
- **`finally` is the only real work.** A `finally` body runs only on the normal path (fall out of the `try` through `leave`). It is implemented with a continuation index and a dispatch, built from existing constructs (see Design).
- **No flag in this work.** Real exceptions, if ever wanted, would be an opt-in `--exceptions` mode using the error-return design (a status check after each call that may throw; no unwinder). Out of scope here. Table-driven unwinding is rejected as too heavyweight for this target.
- Unsupported opcodes today: `throw`, `rethrow`, `leave`, `leave.s`, `endfinally`, `endfilter` are all ❌ in `doc/dotnetro/il-support.md`. The translator never reads `MethodBodyBlock.ExceptionRegions`.

## What I found (so this plan needs no re-exploration)

All in `src/DotNetro.Compiler/IlToMirTranslator.cs` (line numbers are from the working tree at the time of writing, so treat them as approximate), in the nested `MethodTranslator`:

- **`Translate()` (~line 664)** is a single linear pass over the IL. It pre-computes blocks (`DiscoverBlocks`), then walks the IL with one `switch (opCode)`. When the walk reaches an offset in `_ilOffsetToBlock` it closes the current block with a fall-through `EmitBranchTo` and calls `EnterBlock`. The `default` arm throws `NotSupportedException("opcode … is not supported yet")`.
- **There are four independent linear IL scans**, and each must learn about the new opcodes and about skipped handlers:
  1. `ScanAddressTakenLocals` (~2270): only looks for `ldloca.s`.
  2. `ComputeBlockLeaders` (~1030): finds leaders at offset 0, every branch target, and the instruction after every branch. Handles `br`, `brtrue`/`brfalse`, compare-branches and `switch`.
  3. `ComputeLocalLiveness` (~1120): per-block use-before-def and def sets of locals, plus successor edges, then a backwards fixpoint. Only locals live-in to a block become that block's parameters (`DiscoverBlocksLocalParams`). **A control-flow edge the liveness scan does not know about silently produces wrong code**, because locals won't be threaded along it as block arguments.
  4. The main translation loop.
- **`SkipOperand` (~1371)** is used by the scans to step over operands. It has no case for `leave`/`leave.s`, so a method containing one would be misparsed by the scans before it ever reached the "unsupported opcode" error. Add the operand sizes there: `leave.s` has an sbyte displacement and `leave` has an int32, the same as `br.s`/`br`.
- **Terminators and edges:** `EmitBranchTo(from, target)` emits `cf.br target(localArgs ++ stackArgs)`. `BlockHasTerminator` checks the last instruction. `BlockAt(offset)` resolves a leader.
- **Eval stack across blocks:** `_blockStackParams` carries stack values as block parameters. `leave` empties the eval stack, so no stack args cross a `leave`.
- **Locals** are SSA vregs threaded as block parameters (`_blockLocalParams`, `_locals`, `_localTypes`). Value-type and address-taken locals are "FrameSlot" locals living in memory (`_localIsFrameSlot`) and are never threaded.
- **Method discovery** is by queue (`EnqueueMethod`), driven from call translation. If handler IL is never translated, its callees are never enqueued. That is a benefit of skipping dead handlers, and it only works if they are really skipped in the translation loop.
- **Exception regions:** `EcmaMethodBody` (`src/DotNetro.Compiler/EcmaMethod.cs`) holds `MethodBodyBlock`. `MethodBodyBlock.ExceptionRegions` (an `ImmutableArray<ExceptionRegion>` with `Kind` ∈ {Catch, Filter, Finally, Fault}, `TryOffset`, `TryLength`, `HandlerOffset`, `HandlerLength`) is available and unused.
- **Runtime-provided helper pattern to copy:** `GetWriteLineStringMethod()` (~line 223) resolves a C# method in the `DotNetro` runtime assembly (`src/DotNetro/Runtime/ConsoleHelper.cs`), enqueues it, and pins its MIR name via `_emitNameOverrides`. `ConsoleHelper.WriteLineString(byte* p)` takes a raw `byte*` rather than a managed string, because indexing a `string` would emit `string::get_Chars`, which the translator doesn't support.
- **`ILogger`** (`ILogger.cs`) exists as a public interface, but `IlToMirTranslator` does not use one. There is no existing diagnostic channel for warnings.
- **No `unreachable` / noreturn terminator exists** in Irie's dialects (I grepped for unreachable/trap/halt/noreturn under `src/Irie`). A block that ends in a call to a never-returning function still needs some terminator.
- **Lit tests:** `src/DotNetro.Compiler.Tests/Lit/*.cs` are RUN/DIFF-only programs (see `VolatileFields.cs` for the header and a top-level-statements body). They run under the host runtime and the emulator and the outputs must match. Because `throw` is fatal on the 6502 and meaningful on the host, **tests must never actually execute a `throw`**, only compile it on a path that is not taken.

## Design

### Dead handlers

Compute, once per method, the list of `ExceptionRegion`s and the set of **dead IL ranges**: `[HandlerOffset, HandlerOffset + HandlerLength)` for every Catch, Filter and Fault region (and the filter block `[FilterOffset, HandlerOffset)` for Filter). Every IL offset inside a dead range is skipped:

- In all four scans, jump the reader to the end of the dead range when it reaches its start, so no leaders, liveness edges, or `ldloca` are taken from dead code.
- In the main loop, same thing: advance the reader past the range without translating anything. Do not create blocks for it.
- Dead ranges nest correctly: a `try/finally` inside a dead catch is skipped as part of it.

`leave` out of a dead range is never seen. `rethrow` / `endfilter` are only legal inside dead ranges, so they can stay unsupported (still ❌ in `il-support.md`; they appear inside skipped code only).

### Live `leave` and `finally`

IL guarantees a `try` is only exited by `leave` (never `br`, `ret` or fall-through), and a `finally` is only entered from a `leave` (on the normal path) and left through `endfinally`. So the control flow can be rebuilt with existing constructs.

For each `Finally` region F that is live (not inside a dead range):

- Give F a **synthetic local** `contF` (an i8 index) beyond the real locals, and a **continuation table** listing every live `leave` site that exits F's try block.
- A `leave` at offset `o` with target `t` exits the *chain* of live Finally regions whose try range contains `o` but not `t`, innermost first: `F1, F2, …`. If the chain is empty (including `leave` to a target inside the same try, or leaving a try/catch region only), it is just `br t`.
- Otherwise `leave` becomes: store `k` into `contF1` (where `k` identifies this site/stage), then `br` to the start of `F1`'s handler.
- `endfinally` at the end of `F1` becomes a `switch` on `contF1` (or just a `br`, if there is only one continuation, which is the common case) to the per-site **continuation block**. A continuation block either stores into the next finally's index and branches to its handler start (when the chain continues), or branches to `t` (when the chain is done).
- A `leave` that the chain doesn't enter any `finally` for at all must still empty the eval stack (it does not carry stack values; `leave` clears the stack).

Why dispatch instead of cloning the `finally` body at each `leave`: the translator is one pass keyed by IL offset (`_ilOffsetToBlock`, `_blockLocalParams`, `_blockStackParams`), so a second translation of the same IL range would need its own offset→block map and local state, and the liveness scans would not model the copies. Dispatch keeps one copy of each body and reuses existing machinery. The synthetic local and the extra edges are the cost; the single-continuation case degenerates to a plain `br`.

### Liveness scan changes (the risky part)

`ComputeLocalLiveness` must model the new edges and the synthetic local, or locals live across a `finally` will be dropped:

- `leave` ends a block (like `br`): successor = the first finally's handler start (not the final target), and it **defs** `contF1`.
- `endfinally` ends a block: successors = every continuation block / target for F, and it **uses** `contF`.
- Continuation blocks are synthetic (no IL offset). Easiest is to treat the continuation as the leave's *final target* in the liveness successor list (over-approximation is harmless: it only makes more locals live), and keep the synthetic blocks purely a translation-time detail with no block-parameters of their own beyond those of their real target. Decide during implementation; if it gets hairy, simplify by routing every continuation to the real target's block via `EmitBranchTo`.
- Add the handler start of each live Finally to the leader set. The instruction after a `leave` is already a leader by the "instruction after a branch" rule.

### `throw`

- `throw` pops the exception object and discards it, then calls the runtime's fatal-exception helper, then ends the block.
- The helper is a C# method in the `DotNetro` runtime library (a new `ExceptionHelper.Throw()`, internal static, never returns) that prints a short marker via the same route `ConsoleHelper` uses (a `byte*` string), then spins (`while (true) { }`). Resolve it on first use with a `GetThrowHelperMethod()` modelled on `GetWriteLineStringMethod()`, enqueue it, and pin its emit name.
- **Block terminator after the call:** since there is no noreturn terminator, v1 ends the block with `cf.br` to a per-method synthetic **trap block** whose only instruction is `cf.br` to itself (no parameters, so nothing needs threading). This is a stopgap; it relies on the optimiser/RA/block placement coping with a parameter-less infinite-loop block. The principled alternative is a new `cf.unreachable` terminator carried through legalizer / isel / emitter, which costs more. CLAUDE.md says to check llvm-mos for "how is this lowered" questions first. I have **not** checked how llvm-mos lowers `unreachable` / noreturn, so do that before settling for the trap block.

### Dropped-catch diagnostic

Silently dropping a catch handler changes program behaviour relative to desktop .NET, and the translator has no logger. Open question for review: emit a warning (needs a diagnostic channel threaded into `IlToMirTranslator`), or document the behaviour and leave it silent. Not required for the first slice.

## Work items

1. **Plumbing:** expose `ExceptionRegions` from `EcmaMethodBody` (or read them from `MethodBodyBlock` inside `MethodTranslator`). Compute dead ranges, live finally regions, and per-`leave` chains once per method, before `DiscoverBlocks`.
2. **`SkipOperand`:** add `Leave_s` (sbyte) and `Leave` (int32).
3. **Scans:** teach `ScanAddressTakenLocals`, `ComputeBlockLeaders` and `ComputeLocalLiveness` to skip dead ranges, treat `leave`/`leave.s`/`endfinally` as terminators with the edges above, and add the synthetic `contF` locals to the liveness model. Extend `_localTypes`, `_locals`, `_localIsFrameSlot` to cover the synthetic locals (not FrameSlot).
4. **Main loop:** skip dead ranges; translate `leave`/`leave.s`, `endfinally`, `throw`; make `ret`-less handler code well-formed (no fall-through out of a `finally`).
5. **Runtime:** `ExceptionHelper.Throw()` in `src/DotNetro/Runtime`, plus the resolver in `IlToMirTranslator` modelled on `GetWriteLineStringMethod`.
6. **Docs:** regenerate `doc/dotnetro/il-support.md` (`dotnet run --project src/DotNetro.Tools.ILSupport`), and add a short section to `doc/dotnetro/` or `CLAUDE.md` describing "exceptions are fatal; handlers are not translated".

## Tests (lit, `src/DotNetro.Compiler.Tests/Lit/`)

Each with the standard RUN/RUN/DIFF header, never executing a `throw`:

- `TryFinallyNormal.cs`: a `finally` that prints, entered via fall-out of the `try`.
- `TryFinallyMultipleLeaves.cs`: `break` / `continue` / early `return` inside `try` in a loop, each going through the same `finally` (multi-continuation dispatch).
- `TryFinallyNested.cs`: a `leave` through two nested `finally` blocks, checking order of side effects.
- `TryFinallyLocals.cs`: locals modified in `try` and in `finally`, read after (the liveness case).
- `UsingDispose.cs`: `using` over an `IDisposable` class and struct, and `foreach` over a custom enumerator with `Dispose`.
- `TryCatchNotThrown.cs`: `try/catch` where the `try` completes normally; the catch body calls something distinctive that must not be reachable (also a quick way to see in the asm that it is not emitted).
- `ThrowNotTaken.cs`: a method with `throw new Exception()` on a guarded path that is not taken. Confirms `throw` compiles and the helper links.
- Also run `dotnet test --solution src` in Debug and Release, and regenerate the IL-support doc.

## Risks and open questions

1. **`newobj` before `throw` drags code in.** `throw new FooException(msg)` still evaluates the `newobj`, so the exception type, its constructors and anything they reference (message strings, resource lookups) enter the discovery queue. A BCL method with many throw sites could pull in a lot. **First step before committing: measure on a real BCL method** (a `ThrowHelper`-style method and a method with an inline throw). Possible mitigations (not decided): recognise `newobj` of an `Exception`-derived type feeding a `throw` and skip the construction; or stub `ThrowHelper.Throw*` methods.
2. **Terminator after `throw`:** trap block vs `cf.unreachable` (above). Check llvm-mos first.
3. **Liveness correctness:** the continuation/dispatch edges are the most error-prone part. The `TryFinallyLocals` and multi-leave tests exist to catch this; also inspect the MIR with `--emit mir` for one of them.
4. **Code size:** the dispatch `switch` for many continuations. Fine for BCL-sized `finally` blocks; revisit only if it shows up in practice.
5. **Dropped-catch warning:** see above.
6. **Implicit exceptions** (null deref, bounds, divide-by-zero, `checked` arithmetic overflow) are unchanged: they remain undefined behaviour, as today. This plan doesn't add checks.

## Out of scope

Real `try/catch` semantics, `rethrow`, filters, stack traces, an `Exception` hierarchy beyond what BCL code references, `--exceptions` opt-in mode, implicit-exception checks, unwind tables.
