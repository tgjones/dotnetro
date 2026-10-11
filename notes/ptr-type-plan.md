# Plan: a real pointer type in Irie (`ptr`) with `ptradd`

Status: **done** (stages 1–3 and the docs of stage 4) — written 2026-10-11, after the 16-bit-integer work.
Deviations from the text below: the binary `TypeTag.Ptr` is 6 (5 was already `I64`); the front end
refers to the Irie type through a `using IRPointerType = Irie.Mir.PointerType` alias because
`DotNetro.Compiler.TypeSystem.PointerType` has the same name; `GenericMirVerifierPass` now derives
from `Pass` (it needs the module for the call-signature rule) and keeps a module-less
`Run(MirFunction)`. Stage 4.1 (`ptradd (symbol)` + constant fold) was skipped: by the time a load is
legalized the legalizer has already narrowed the `ptradd`, so the fold needs either a legalization
order change or symbol-plus-offset `mem.symbol` addresses carried through the selector.
Goal: replace `IRType.Pointer = I16` (an alias) with a distinct `ptr` type, add the few
ops needed to use it, and delete the translator's `StackValue.IsPointer` bridge flag.

## 1. Why

- Today a `short`/`ushort`/`char` and a pointer are both `i16`. The translator tells them
  apart with a side-channel flag (`StackValue.IsPointer`, `PromoteNarrow`, `IsPointerClr`
  in `src/DotNetro.Compiler/IlToMirTranslator.cs`). A missed flag-setting site silently turns
  pointer arithmetic into integer arithmetic. The IR itself cannot catch it.
- With a real type, the verifier can reject `mem.load` on an `i16`, `arith.addi` on a
  pointer, etc., and the backend can later key address-specific selection (zero-page
  indirect, `mem.symbol + const` folding) off the type.

## 2. Decisions (settled — do not re-litigate during execution)

1. **Opaque pointer.** One `ptr` type, no pointee type. Element/value type stays on the
   load/store op (as today: `mem.load.i8/i16/i32`). Same lesson LLVM learned moving off
   typed pointers.
2. **Width.** `PointerType : IRType` with `SizeInBits = 16` for now; `ptr` in text parses
   to the 16-bit pointer. Pointer width should become a target property (cf. LLVM's
   datalayout `p:16:8`) when a second target with a different width (e.g. 65816 24-bit)
   lands. Not part of this plan; keep the width a field of `PointerType` so it is possible.
3. **`ptr` is not an integer.** A `ptr` vreg may only be: copied/merged/unmerged, a block
   parameter/argument, a call argument/return/function parameter, loaded/stored as a value
   (`mem.load.ptr` / `mem.store.ptr`), used as an address operand, produced by
   `mem.symbol` / `mem.frame_addr`, offset by `mem.ptradd`, and converted by
   `cast.ptrtoint` / `cast.inttoptr`. **No `arith.*` op accepts a `ptr`** (not addi, not
   cmpi, not select, not constant). Pointer compare = `ptrtoint` both sides + integer
   `cmpi` (what GlobalISel does). Null pointer = `inttoptr (arith.constant 0)`.
   This keeps the legalizer surface small.
4. **Pointer arithmetic is `ptradd`** — matches the direction of both references:
   - LLVM IR: `getelementptr` is being canonicalised to the byte-offset `getelementptr i8`
     form, which the tree calls "ptradd" (`IRBuilder::CreatePtrAdd`, `ConstantExpr::
     getGetElementPtr(DL, …)` "canonical ptradd form"). A dedicated `ptradd` instruction
     has not landed in `/Users/timjones/Code/llvm-mos` (0f031168a7cc); only the canonical
     form and the builder helper.
   - GlobalISel (which Irie models): `G_PTR_ADD` (ptr + integer offset), `G_PTRTOINT`,
     `G_INTTOPTR`, `G_PTRMASK`. llvm-mos folds `G_PTR_ADD (GLOBAL_VALUE @x + c1), c2`
     (`llvm/lib/Target/MOS/MOSCombiner.cpp` ~L82/L148/L169) — the model for our
     `mem.symbol` + constant folding.
   - Irie spelling: `%q : ptr = mem.ptradd %p, %off` — byte offset, `%off : i16`
     (pointer-width integer, two's complement, so `p - n` = `ptradd p, (0 - n)`).
     Lives in the **Mem** dialect (it is address computation, next to `symbol`/`frame_addr`).
     No element-size scaling in the op — the front end multiplies (as `EmitScaledIndex`
     does for arrays). Offset widths other than the pointer width are not accepted.
5. **Casts** in the **Cast** dialect: `cast.ptrtoint %p : ptr -> i16`,
   `cast.inttoptr %x : i16 -> ptr` (same width only; widen/narrow with `zext`/`trunc` first).
6. **Typed pointer load/store:** `mem.load.ptr` / `mem.store.ptr` (value type `ptr`),
   in addition to `load/store.i8/i16/i32`. Used for vtable pointers, function-pointer
   slots, reference-typed fields/statics/array elements.
7. **Native int is an integer.** In the C# front end `IntPtr`/`UIntPtr`/`nint`/`nuint` and
   `conv.i`/`conv.u` results are `i16` integers. References, `T*`, `ref T`, strings, arrays
   and object refs are `ptr`. Where IL uses a native int as an address (`ldind`, `stind`,
   `ldfld` base, …) the translator inserts `inttoptr`; where it needs a pointer's bits
   (`conv.i`/`conv.u` of a pointer, ptr-ptr, compares) it inserts `ptrtoint`. IL `add` of
   (ptr, int) → `mem.ptradd`; (ptr, ptr) is invalid; `sub` (ptr, ptr) → ptrtoint both then
   `arith.subi`; (ptr, int) → `ptradd` of the negated offset.
8. **Runtime signatures carry pointer-ness.** Change `DotNetro/Runtime/ManagedHeap.cs` and
   friends so pointer-valued parameters/returns are C# pointers (`byte* Alloc(nint size,
   void* vtable)`, etc.), so the MIR signature says `ptr` without casts. Likewise
   `ConsoleHelper` already takes `byte*`. Hand-written MIR in
   `src/Irie/Target/MOS6502/runtime.irie` must be retyped to match (`(ptr)` not `(i16)`).

## 3. Current state (what the work touches)

Irie core (`src/Irie`):
- `Mir/IRType.cs` — `IRType.Pointer = I16`; `IntegerType`, `FloatType`, `LabelType`, `VoidType`.
- `Mir/Parsing/MirParser.cs` — `ParseTypeName` (~L215) and `IsTypeName` (~L225) list
  `void i1 i8 i16 i32`; globals use `type.SizeInBits / 8` (~L69).
- `Mir/Writing/MirWriter.cs` — `FormatType` uses `DisplayName`.
- `Mir/Binary/MirBinaryFormat.cs` `TypeTag` (Void=0…I32=4), `MirBinaryWriter.WriteType`
  (~L207), `MirBinaryReader.ReadType` (~L179). Add `Ptr = 5` (additive; old files stay valid).
- `Mir/MirBuilder.cs` — `BuildMemSymbol`/`BuildFrameAddr` create `IRType.Pointer` vregs
  (~L250/L263); `BuildCallIndirect(int targetPtrVreg …)`.
- `Dialects/Mem/MemOp.cs`, `MemDialect.cs` — op list, `TryParseOp` name table, `GetInstructionInfo`
  (`TypeOperandIndex` for stores/fill), `IsSideEffectFree`, `IsLegalImmediateOperand`.
- `Dialects/Cast/CastOp.cs` (+`CastDialect.cs`) — `Trunc`, `Zext`, `Sext`.
- `Passes/GenericMirVerifierPass.cs` — **only** checks inline-immediate placement today;
  no type checking at all. (Dialect prefix set `core arith cast cf call mem pseudo`.)
- `Passes/LegalizerPass.cs` — `LegalizeInstruction` keys the action off the type of the
  first def vreg or `TypeOperandIndex`; `LegalizeNarrowScalar` / `BuildMergeInto` rebuild
  wide vregs from `i8` parts; artifact combiner folds `merge`/`unmerge`.
- `Target/MOS6502/MOS6502LegalizerInfo.cs` — `GetAction` begins with
  `if (type is not IntegerType) return Unsupported;` then dispatches per dialect; Mem
  branch (~L108): `Symbol when SizeInBits==16 → Legal`, `Load/Store{I8,I16,I32} → Custom`,
  `LowerWideLoad/LowerWideStore/LowerMemFill`; Pseudo copy/merge/unmerge → Legal; Cast
  trunc/zext/sext → Custom.
- `Target/MOS6502/MOS6502InstructionSelector.cs` — `MemOp.Symbol => true`,
  `SelectMemLoadByteAt/StoreByteAt` (~L118–126); folds of `mem.symbol` at ~L1520, ~L1717.
- ABI: `Target/MOS6502/MOS6502CallLowering.cs` (`ByteCount = SizeInBits/8`, `BuildUnmerge(I8,
  targetPtrVreg, 2)` for `call.indirect`), `Passes/AbiLoweringPass.cs`.
- `Passes/ArithSimplifyPass.cs` matches `TypedVReg { Type: IntegerType }` — pointers are
  naturally excluded.

Front end (`src/DotNetro.Compiler/IlToMirTranslator.cs`):
- `ToIRType` returns `I16` for refs/pointers/IntPtr; `IsPointerClr`, `PushTyped`,
  `StackValue(…, IsPointer)`, `StackValue.Ptr`, `PromoteNarrow`, `TranslateConv(…, pointer:)`,
  pointer-vs-integer branch in `TranslateIntegerBinary`, `TranslateLdind/Ldelem(…, pointer:)`.
- Address computation sites that currently emit `arith.addi i16`/`subi`: `EmitFieldAddress`,
  `EmitPointerSub`, `PopElementAddress`/`EmitScaledIndex` (arrays), `EmitStructCopy` (via
  `EmitFieldAddress`), vtable slot addressing in the callvirt path, `TranslateNewobj`,
  `TranslateInitobj` (`mem.fill`).
- Loads/stores of pointer-valued things that today use `mem.load/store.i16`: vtable pointer
  (`callvirt`), function-pointer slot, reference-typed `ldfld/stfld/ldsfld/stsfld`,
  `ldind.ref/stind.ref`, `ldelem.ref/stelem.ref`, `ldind.i/stind.i` (these are *native int* →
  stay `.i16`, with `inttoptr` on the address).

Tests that mention `mem.*` or `call.indirect` as MIR input (non-`bin/` copies; each needs its
pointer-typed vregs/params retyped from `i16` to `ptr` in Stage 2):
`src/Irie.Tests/Lit/CodeGen/MOS6502/`: `CastTruncI32ToI16-Legalizer`, `CastTruncI32ToI8-Legalizer`,
`FrameSlotStructRoundtrip-MachineCode`, `MemGlobalAbsolute-Binary`, `MemGlobalAdcFold-Binary`,
`MemLoadStoreI16-MachineCode`, `MemLoadStoreI16-Legalizer`, `MemLoadStoreI8-MachineCode`,
`MemLoadStoreI8-InstructionSelector`, `FrameSlot-Lowering`, `AddressTakenFrameSlot-MachineCode`,
`WriteLineString-MachineCode`, `WriteLineBoolean-MachineCode`, `StaticFrameAllocOverflow-MachineCode`,
`StaticFrameAllocRecursive-MachineCode`, `StaticFrameAllocChain-MachineCode`,
`FramePlacementLate-MIR`, `RuntimePointerLoad-InstructionSelector`, `RuntimePointerLoadStore-MachineCode`,
`MemFill-Legalizer`, `CallFuncSymbolArg-AbiLowering`, `MirGlobal-RoundTrip`,
`CallIndirect-MachineCode`, `CallIndirectTrampoline-Binary`,
`LlvmMosReference/memory/global-rw`, `LlvmMosReference/control-flow/common-subexpr`
(+ any corpus case with pointer params — grep `: i16` params used as addresses).
C# unit tests that build MIR directly: `src/Irie.Tests/Mir/MirRoundTripTests.cs`,
`MirBinaryRoundTripTests.cs`, `Passes/GenericMirVerifierTests.cs`, `Passes/RegisterAllocatorTests.cs`.

## 4. Stages (each stage ends with `dotnet test --solution src` green, Debug and Release)

### Stage 1 — add the type and ops, change no behaviour
`IRType.Pointer` is still `I16` here; nothing existing changes.
1. `IRType.cs`: `public sealed record PointerType(int SizeInBits) : IRType(SizeInBits)`,
   `DisplayName => "ptr"`; add a separate static (e.g. `IRType.Ptr16`) for the new type —
   *do not* repoint `IRType.Pointer` yet.
2. Parser: `ptr` in `ParseTypeName` / `IsTypeName`. Writer: nothing (DisplayName).
   Binary: `TypeTag.Ptr = 5` in format/writer/reader. Round-trip tests in
   `MirRoundTripTests` / `MirBinaryRoundTripTests` incl. `(ptr, i16) -> ptr` signatures,
   a `global @p : ptr`, block params of type `ptr`.
3. New ops: `MemOp.PtrAdd` (`ptradd`), `MemOp.LoadPtr` (`load.ptr`), `MemOp.StorePtr`
   (`store.ptr`); `CastOp.PtrToInt` (`ptrtoint`), `CastOp.IntToPtr` (`inttoptr`). Update
   `TryParseOp`, `GetOpName`, `GetInstructionInfo` (`StorePtr` like `StoreI8Info`;
   `PtrAdd` result is the query type), `IsSideEffectFree` (ptradd/casts pure).
4. Legalizer (`MOS6502LegalizerInfo.GetAction`): replace the early `type is not IntegerType →
   Unsupported` with per-case handling that also accepts `PointerType` for exactly:
   `Pseudo.{Copy,Merge,Unmerge}` Legal; `Mem.Symbol` Legal when `ptr`; `Mem.LoadPtr/StorePtr`
   Custom (reuse `LowerWideLoad/Store` with the I16 path); `Mem.PtrAdd` Custom (unmerge ptr →
   2×i8, unmerge offset, `addi_with_carry` chain, merge result into the `ptr` def — mirror
   `LegalizeNarrowScalar`'s add chain); `Cast.PtrToInt/IntToPtr` Custom (unmerge to bytes,
   merge as the other type; the artifact combiner folds back-to-back casts). Everything else
   with a `ptr` type stays `Unsupported` (that is the rule from §2.3).
5. Verify `LegalizerPass.LegalizeNarrowScalar`/`BuildMergeInto` keep the *original* def type
   when re-merging (a `ptr` def must stay `ptr`); fix if they assume `IntegerType`.
6. New hand-written lit tests (`src/Irie.Tests/Lit/CodeGen/MOS6502/`): `PtrAdd-Legalizer`,
   `PtrAdd-MachineCode`, `PtrToIntIntToPtr-Legalizer`, `MemLoadStorePtr-MachineCode`,
   `PtrRoundTrip.irie`. Generate CHECKs with `dotlit --regenerate` and review.
   Exit: all existing tests untouched and green; new tests green.

### Stage 2 — flip the alias; retype Irie and the front end
1. `IRType.Pointer = new PointerType(16)` (drop the temporary static). `BuildMemSymbol`/
   `BuildFrameAddr` now produce `ptr`. `mem.load/store.*`, `byte_at`, `fill`, `frame_addr`,
   `symbol`, `call.indirect` target: address operands are `ptr`. Update `MemOp.cs` comments.
2. Fix every `type is IntegerType` assumption that a pointer reaches (ABI: `AbiLoweringPass.
   GetVregType`, `MOS6502CallLowering` use `SizeInBits` — should just work; check
   `MOS6502InstructionSelector` `Symbol` handling and the two `mem.symbol` folds).
3. Retype all MIR test inputs listed in §3 (`: i16` → `: ptr` for address vregs, params and
   returns; `global @x : i16` stays an integer global unless it stores a pointer). Update
   `runtime.irie` signatures (`WriteLineString : (ptr) -> void`, etc.).
   Refresh goldens: `dotnet build src && dotnet run --project src/DotLit -- --regenerate
   src/Irie.Tests/Lit` then eyeball the diff (should be type spellings only).
4. Front end, in this order so the build stays green:
   a. `ToIRType`: refs, arrays, `T*`, `ref T`, strings, objects → `IRType.Pointer`;
      `IntPtr`/`UIntPtr`/`Int16`/`UInt16`/`Char` stay `I16`.
   b. `StackValue.IsPointer` becomes derived (`Type is PointerType`); delete the field,
      `StackValue.Ptr`, `IsPointerClr`, and the `pointer:` parameters; `PushTyped` just pushes.
   c. Add helpers `AsAddress(StackValue)` (ptr → as is; i16 → `inttoptr`; i32 → trunc then
      `inttoptr`) and `AsInteger(StackValue)` (`ptrtoint`). Use `AsAddress` for every address
      operand: `ldind/stind`, `ldfld/stfld` base, `ldelem` array, `initobj`, `ldlen`, etc.
   d. `TranslateIntegerBinary`: if exactly one operand is `ptr`: `add` → `mem.ptradd` (offset
      resized to i16), `sub` ptr−int → `ptradd` with `0 - offset`; ptr−ptr → `ptrtoint` both
      then `subi`; any other op on a `ptr` → throw `NotSupportedException`. Compares
      (`ceq/clt/…`, `beq/…`): `ptrtoint` any `ptr` operand first.
   e. `EmitFieldAddress`, `EmitPointerSub`, `PopElementAddress`, vtable slot addressing →
      `mem.ptradd`. `ldnull` → `inttoptr (arith.constant i16 0)`.
   f. Loads/stores of pointer-valued data → `mem.load.ptr`/`mem.store.ptr`: vtable pointer and
      function-pointer slot (`callvirt`), reference-typed fields/statics/array elements,
      `ldind.ref/stind.ref`, `ldelem.ref/stelem.ref`. `ldind.i/stind.i` stay `.i16`.
   g. `conv.i`/`conv.u`: result is an `i16` integer; operand `ptr` → `ptrtoint` first. `conv.i4`
      etc. of a `ptr` likewise go through `ptrtoint`.
   h. Runtime C# signatures (§2.8): `ManagedHeap.Alloc`/`AllocArray` return/take `byte*`/`void*`;
      update `GetAllocMethod`/`GetAllocArrayMethod` signatures and the `newobj`/`newarr` call
      sites (`BuildCall(…, [IRType.Pointer], …)` now matches the callee).
   i. Remove `PromoteNarrow`'s pointer test (`!IsPointer` → nothing: pointers never reach it
      because arith on `ptr` is rejected earlier).
   Exit: all `.cs` lit tests (emulator-executed) and all Irie tests green. The 20-ish array,
   pointer, int16 and ternary tests added recently are the regression net.

### Stage 3 — verify types
1. Extend `GenericMirVerifierPass` (it currently only checks immediates) with type rules,
   run at pipeline start (and consider re-running after the legalizer for sanity):
   - address operands of `mem.load.*/store.*/byte_at/fill` and `call.indirect` target are `ptr`;
   - `mem.symbol`/`frame_addr` define `ptr`; `ptradd`: `(ptr, i16) -> ptr`;
   - `ptrtoint: ptr -> i16`, `inttoptr: i16 -> ptr`; `load.ptr` defs `ptr`, `store.ptr` stores `ptr`;
   - no `arith.*` operand or result is `ptr`;
   - call arguments/returns match the callee's declared signature when the callee is in the
     module (catches `runtime.irie` / C# runtime signature drift, which is otherwise silent).
2. Negative tests in `GenericMirVerifierTests.cs` (one per rule).
   Exit: green; deliberately mis-typed MIR is rejected with a clear message.

### Stage 4 — fold and document
1. Combiner/selector: fold `mem.ptradd (mem.symbol @x), const` into a symbol+offset operand
   (the `MOSCombiner` model, §2.4) where the selector already folds absolute loads
   (`SelectMemLoadByteAt`, the `adc.abs @sym` fold). Only if it shrinks a golden; otherwise skip.
2. Docs: `CLAUDE.md` (MIR operand/dialect descriptions: `mem.symbol` etc. are `ptr`; new ops;
   remove any statement that pointers are `i16`), `doc/irie/*`, `src/Irie/Dialects/*/…Op.cs`
   comments, and mark this plan done.

## 5. Risks and notes
- **Golden churn.** Stage 2 rewrites many goldens at once; keep it to type spellings and review
  the diff — a changed instruction sequence there is a real regression, not churn.
- **Silent signature drift** between C# runtime, `runtime.irie`, and translator-built calls is
  only caught in Stage 3 (call-signature check). Until then, check the pairs by hand.
- **`IsPowerOfTwoConstant`/`ArithSimplify`** assume integer operands; offsets given to
  `ptradd` are integers, so scaling still simplifies. Re-check after Stage 2e.
- **Register allocation** only ever sees post-legalization `i8` bytes and the `mem.symbol`
  result, so it should be unaffected; `ClassedVReg` assignment for a `ptr`-typed
  `mem.symbol` (`ReclassifyVirtualRegister`) is the one place to confirm.
- **Not in scope:** per-target pointer width/datalayout, address spaces, `G_PTRMASK`
  (alignment masking: `ptrtoint`/`and`/`inttoptr` for now), typed/`inbounds` ptradd flags.
- **Why not `arith.ptradd`:** address computation sits with `symbol`/`frame_addr` in `mem`;
  `arith` stays integer-only (§2.3).
