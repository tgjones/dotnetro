# Custom CoreLib plan

Goal: replace the host's `System.Private.CoreLib` (read today via `Assembly.Load("mscorlib")`) with a small, DotNetro-owned, 8-bit-friendly core library, kept out-of-tree. Later, selectively vendor pure-managed upstream files (`List<T>`, …) into it (the "hybrid"). Do **not** fork dotnet/runtime.

## Key decision: bind at DotNetro-compile time, not at Roslyn-compile time

User programs keep compiling with Roslyn against the **standard reference assemblies** (as `DotNetro.Compiler.Tests.CsCompiler` does now). `dnrc` then *retargets* every reference to a framework assembly (`mscorlib`, `System.Runtime`, `System.Console`, `System.Private.CoreLib`, `netstandard`, …) onto our corelib, resolving types and members by (namespace, name, signature). This is how NativeAOT's ILC works: the user assembly is built against the ref pack and the compiler is handed the target's implementation corelib.

Why not make users compile against our corelib with `NoStdLib`:
- The `DIFF: dotnet emulated` lit tests run the *same* DLL on the host runtime; a corelib-bound DLL cannot run there.
- `notes/nuget-onboarding-plan.md` has users add a `PackageReference` to an ordinary `net10.0` console project; they cannot reasonably swap their corelib.
- `notes/web-playground-plan.md` already compiles user code against real reference assemblies.

Consequence: our corelib's public members must match the reference assemblies' names and signatures for everything we implement (hard requirement, not a nicety). A user call to something we lack fails at DotNetro-compile time with "type/member not found in DotNetro CoreLib" rather than at Roslyn time. The allow-list/analyzer in the playground and NuGet plans is the user-facing mitigation.

## What I found in the repo (so this plan needs no re-exploration)

All paths under `src/`.

- **Core assembly resolution.** `DotNetro.Compiler/TypeSystem/TypeSystemContext.cs`: `CoreAssembly` = `ResolveAssembly(new AssemblyName("mscorlib"))`; `RuntimeAssembly` = `"DotNetro"`. `ResolveAssembly(AssemblyName)` does `Assembly.Load(fullName).Location` on the **host**, so `System.Runtime`/`System.Console` resolve to the host's facade DLLs and (through `EcmaAssembly.GetTypeImpl`, which already follows `ExportedType` → `AssemblyReference` forwarders) to host `System.Private.CoreLib`. No `IAssemblyResolver` exists yet (`notes/web-playground-plan.md` WP1 proposes one; do it here, compatibly). `WellKnownType` has only `ValueType`.
- **Type refs.** `EcmaAssembly.ResolveType(TypeReferenceHandle)` handles only `AssemblyReference` resolution scopes. `ResolveMethod` handles `MemberReference` only with a `TypeReference` parent (no `TypeSpec`, so no generic instantiations through member refs yet).
- **Primitives are not read from CoreLib.** Signatures use ELEMENT_TYPE codes mapped by `SignatureTypeProvider` → `PrimitiveType`; `IlToMirTranslator.ToIRType` (~line 575) maps them (`Int32` → i32, `Int16/Char/IntPtr` → i16, `String`/`Object` → `ptr`). Sizes come from `PrimitiveType.InstanceSize`, with `PointerSize = 2`.
- **Hard-coded `System.*` name checks** in `IlToMirTranslator.cs`: `ElementSize` (~2590) and `TranslateLdelem`/`ElementIRType` (~2650–2700) switch on `TypeReference` names (`Byte`, `Int32`, `Object`, `String`, …) because "primitive element types arrive as System.* type references, which have no useful layout of their own". These become ordinary type resolution once our corelib defines real primitive structs.
- **Console is special-cased by `UniqueName`** in `TranslateDirectCall` (~2328–2345): `System_Console_WriteLine_String` → `ConsoleHelper.WriteLineString`; `…_Int32` → `@WriteLineInt32`; `…_Boolean` → `@WriteLineBoolean`; `ReadLine` → `@ReadLine`; `Beep` → `@Beep` (hand-written MIR in `Irie/Target/MOS6502/runtime.irie`). The `System.Console` body is never translated.
- **`[MethodImpl(InternalCall)]`** (`EcmaMethod.IsInternalCall`) routes by *method name* to a `runtime.irie` function (`IsRuntimeFunction(callee.Name)`), so overloads would collide on name; give each InternalCall a unique name.
- **Strings are not objects.** `ldstr` → `RegisterStringGlobal` emits `string{token:X8}`: ASCII bytes + `0x0D` (CR) + `0x00`, and pushes a `ptr` to the bytes. The CR **is the `WriteLine` newline** baked into the constant, so the constant is only correct as a `WriteLine` argument. `ConsoleHelper.WriteLineString(byte*)` walks to NUL calling `osasci`. `ReadLine` returns a raw pointer. There is no `String.Length`/indexer/concat.
- **Objects.** Layout: 2-byte vtable pointer at `obj-2`, contents after (`ManagedHeap.Alloc`). Arrays: null vtable, 2-byte length, elements (`ArrayLengthSize`, `ManagedHeap.AllocArray`). `newobj` calls the ctor, including `System.Object::.ctor` translated from the **host's** IL body (a `ret`). `GetVtableLayout` stops at the root (`BaseType == null`): Object's `ToString`/`Equals`/`GetHashCode` get **no slots**, and overriding a BCL virtual throws "overriding a BCL virtual method is not supported yet".
- **Runtime library** `DotNetro/Runtime/{ConsoleHelper,ManagedHeap,MathHelper}.cs` is a normal `net10.0` project compiled against the host BCL (`DotNetro.csproj`), looked up by name from `dnrc` (`DotNetro.Compiler.Driver` references it so it lands beside `dnrc`). Types are `internal`; `MathHelper` is deliberately free of `* / %`.
- **Test harness.** 100 lit tests under `DotNetro.Compiler.Tests/Lit/`; only `Console.WriteLine` (770 uses), `ReadLine`, `Beep`, and `nint`/`nuint` touch the BCL. No test uses `string` members, `Math`, `Convert`, collections, or interpolation. So the initial corelib surface is tiny.
- **Unsupported IL that BCL-style code needs** (`doc/dotnetro/il-support.md`): `box`, `castclass`, `isinst`, `constrained`, `unbox.any`, all `.ovf`, exceptions (see `notes/exceptions-plan.md`). Generic instantiation machinery exists (`InstantiatedType`) but is not exercised by member refs.
- **Working tree is mid-change**: `IlToMirTranslator.cs` and `SignatureTypeProvider.cs` have uncommitted edits (calli/readonly/volatile work). Land or stash those before starting stage 1 to avoid conflicts.

## Design

### Project and naming
- New project `DotNetro.CoreLib` (`src/DotNetro.CoreLib/`), **assembly name `System.Private.CoreLib`**. This matches the web-playground plan's end state (implementation corelib + `System.Runtime` facade) and keeps the hybrid path open.
- Built with `<NoStdLib>true</NoStdLib>`, `<NoConfig>true</NoConfig>`, `AllowUnsafeBlocks`, `Optimize=true` (as `DotNetro.csproj` does today, for the same reason: minimal IL), referencing nothing. It defines `System.Object` itself, so Roslyn compiles it as a bare-metal corelib; it must declare every well-known type Roslyn demands (compile errors name the missing ones).
- **Merge the existing runtime library into it** (`DotNetro.Runtime` namespace, `ConsoleHelper`/`ManagedHeap`/`MathHelper`). `Console` (in corelib) must call `ConsoleHelper`, and the helpers are `internal`; a separate assembly would force them public and create a cycle. `TypeSystemContext.RuntimeAssembly` then returns the corelib; retire the `DotNetro` project (the NuGet *package* name `DotNetro` is unrelated).
- Output is a build artefact the compiler locates via a resolver, not via `Assembly.Load`.

### Binding / retargeting (`TypeSystemContext`)
- Introduce `IAssemblyResolver { string/bytes Resolve(AssemblyName) }` (compatible with WP1 of the playground plan). Default impl: the shipped corelib file beside `dnrc`.
- `ResolveAssembly(AssemblyName)` first consults a **retarget set**: `mscorlib`, `netstandard`, `System.Runtime`, `System.Console`, `System.Private.CoreLib`, `System.Runtime.Extensions`, `System.Collections`, `System.Memory`, `System.Runtime.InteropServices` → the corelib. Any other framework assembly name → error naming the assembly (never silently load a host DLL). The user's own DLL and non-framework references load normally.
- Because lookup is by (namespace, name), no facade assembly is needed in v1. Add a real `System.Runtime` facade of type-forwards only if non-retargeted consumers (third-party DLLs resolved by the normal path) need it; `GetTypeImpl` already follows `ExportedType`.
- Types/members not found raise a single `NotSupportedException`-style diagnostic: `DotNetro CoreLib does not implement System.X.Y(sig)`. (Align the exception type with the diagnostic work in the NuGet plan §3.)
- Member lookup must match on **signature** (it already passes the decoded signature to `GetMethod`); for generic members this will need `TypeSpec` parents later.

### Foundational types (hand-written, compiler-known layout)
`Object`, `ValueType`, `Enum`, `Void`, `Boolean`, `Byte`, `SByte`, `Int16`, `UInt16`, `Char`, `Int32`, `UInt32`, `Int64`, `UInt64`, `IntPtr`, `UIntPtr`, `String`, `Array`, `Attribute`, `Nullable<T>`, `Delegate`/`MulticastDelegate` (as calli work requires), `Exception` (empty hierarchy until the exceptions plan), `MethodImplAttribute`/`MethodImplOptions`, `IDisposable`, `Console`.
- Primitive structs only need to *exist* with the right names and the arithmetic/`ToString` members user code calls; the translator keeps mapping the ELEMENT_TYPE codes. The `ElementSize`/`ldelem` name switches become ordinary type resolution (stage 1 cleanup).
- Keep `int` = 32-bit, `long` = 64-bit, `char` = 16-bit (language semantics); `IntPtr`/`nint` = 16-bit (`PointerSize`).
- Where we implement a member, copy upstream's **name, accessibility and signature exactly** (ref-assembly-visible surface). Internals are free to differ.

### String
- Decision (to confirm, see Open questions): `String` is a real heap object using the array layout: `[vtable ptr][u16 length][bytes…][NUL]`, one **byte per char** (Latin-1/ASCII) in storage; the indexer widens to 16-bit `char`. Reuses `AllocArray`-style allocation; `Length`, indexer, `Concat`, `Equals` written in C# with unsafe pointer arithmetic over the object.
- `ldstr` emits a static string **object** (vtable ptr + length + bytes + NUL) and pushes a pointer to it, and the CR hack goes away: `WriteLine(string)` prints the chars then a newline (`osasci(13)`, which already expands CR → CR LF). Existing goldens/emulator outputs should be unchanged since the emulator output is what `DIFF` compares.
- `RegisterStringGlobal` callers (`True_String`/`False_String`, runtime.irie's `@WriteLineBoolean`) must move to the new constant format in the same stage.

### Object virtuals
Initially `Object` declares only `.ctor`. Add `ToString`/`Equals`/`GetHashCode` later as real vtable slots (slot indices stay a pure function of metadata, as now). Needed before any vendored `List<T>`/`Dictionary` (`EqualityComparer<T>`, `ToString`). Removing the "stop at root" special case then also removes the "overriding a BCL virtual" failure.

### Hybrid path (later; keep open)
- Vendor pure-managed upstream files into `DotNetro.CoreLib/Vendored/`, **copied (MIT) and pinned to a runtime tag with a `README` recording the tag and any local patches**, not a submodule fork. Keep namespaces and member signatures identical so a hand-written type can be swapped for the vendored file with no user-code change.
- Order by dependency weight: `List<T>` (few deps: `ThrowHelper`, `Array.Copy`, `EqualityComparer<T>` in `Contains`/`IndexOf`) → `Stack<T>`/`Queue<T>` → a trimmed `Dictionary` (upstream's `EqualityComparer<T>.Default` uses reflection to pick a comparer; stub with a `default(T)`-aware generic comparer).
- Gated by translator features, not by the corelib: generic instantiation through `TypeSpec` member refs, `box`/`unbox.any`/`constrained`, interface dispatch, `castclass`/`isinst`, exceptions-plan `try/finally` (for `foreach`/`using`). Each translator feature unlocks more of the BCL.

## Stages

Each stage ends with `dotnet test --solution src` green in Debug and Release (`dotnet test --project src/DotNetro.Compiler.Tests` is ~12 s). Lit goldens are strict; if emitted code legitimately changes, regenerate with `dotnet run --project src/DotLit -- --regenerate <path>` and eyeball the diff.

### Stage 1 — Corelib seam, no behaviour change
1. Create `src/DotNetro.CoreLib` (assembly `System.Private.CoreLib`, NoStdLib) with the foundational types above. `Console` is a stub whose members are `[MethodImpl(InternalCall)]`/forwarders matching today's behaviour; keep the existing `UniqueName` special-cases working by name (`System_Console_*` is unchanged because the namespace/type/method names are identical).
2. Add `IAssemblyResolver` and the retarget set to `TypeSystemContext`; point `CoreAssembly` and `RuntimeAssembly` at the corelib. Make an unmapped framework assembly an error (this is how you prove nothing still reads host CoreLib).
3. Replace the typeref-name switches in `ElementSize`/`TranslateLdelem`/`ElementIRType` with resolved corelib types (`EcmaType.IsValueType`, `.Size`; primitives need `Size` from `PrimitiveTypeCode` by name since their layout is compiler-defined — add a small name→`PrimitiveTypeCode` map used by `ResolveType`).
4. `DotNetro.Compiler.Driver` references the corelib project (copy-to-output beside `dnrc`); delete `src/DotNetro/` once merged (move `Runtime/*.cs`).
5. Gate: all 100 lit tests pass; `Assembly.Load` no longer reachable for framework names (add a unit test that compiling `HelloWorld` with the host's CoreLib path poisoned still works).

### Stage 2 — Console as ordinary C#
Implement `Console.WriteLine(string|int|bool)`, `ReadLine`, `Beep` as C# in the corelib calling uniquely named `InternalCall` externs (`osasci`, `WriteLineInt32`, …). Delete the `System_Console_*` `UniqueName` switch in `TranslateDirectCall` and `GetWriteLineStringMethod`'s special pinning where no longer needed. `WriteLineInt32`/`Boolean` may stay hand-written MIR initially; `Int32.ToString` in C# is a later optional step (needs `MathHelper` division).

### Stage 3 — Real `String`
Implement the string layout, `ldstr` emitting string objects, `Length`/indexer/`Concat`/`Equals`/`==`, `ReadLine` returning a heap string, and the `WriteLine` newline change. Add lit tests: `string.Length`, indexer, `==`, concat, `ReadLine` round-trip (extend `--input`), and a string held in a field/array (reference-type element).

### Stage 4 — Object virtuals, `Enum`, `Nullable<T>`, `box`
Add real `Object` virtual slots; implement `box`/`unbox.any`/`castclass`/`isinst` in the translator against corelib types; `Enum`, `Nullable<T>` shapes. New lit tests per opcode (they appear in `il-support.md` after regenerating it with `dotnet run --project src/DotNetro.Tools.ILSupport`).

### Stage 5 — First vendored type (spike, then commit)
Vendor `List<T>`. Measure: how much code it pulls in, what translator features it needs. Write down the result in this file and decide whether `Dictionary` is worth trimming or hand-writing.

## Open questions

1. **String storage:** byte-per-char (recommended: halves RAM, matches the 8-bit target, no UTF-16 on a 64 KB machine) vs. real UTF-16. Byte-per-char breaks `char` values > 255 silently; decide whether to throw/replace on `ldstr` of non-Latin-1 text (current code uses `Encoding.ASCII`, which replaces with `?`).
2. **Merge vs. keep `DotNetro` assembly** (recommended: merge, see above). The cost is that the NuGet `dnrc` tools folder ships one fewer DLL; the benefit is `internal` access and no cycle.
3. **Assembly name:** `System.Private.CoreLib` (recommended; aligns with the playground plan and the hybrid) vs. a DotNetro-specific name with a retarget map only. A DotNetro-specific name is cheaper if third-party DLLs are never expected to bind to it directly.
4. **Facade for non-retargeted references** (third-party DLLs): defer until a real case appears.
5. **Interaction with `notes/web-playground-plan.md`:** its "future direction" is compiling real stock CoreLib bodies and a *per-target* CoreLib "eventually". This plan chooses the custom corelib now and makes stock-CoreLib compilation the thing we *don't* do. Update that plan (and its WP2 spike) once this one is accepted.
6. **Diagnostics type** for "not in DotNetro CoreLib" (share with NuGet plan §3's `DNRO` codes).

## Risks

- **Silent behavioural drift from the host.** The same user DLL runs on the host (real BCL) and on the 6502 (our corelib). Anything where our `String`/`Console` semantics diverge is a latent DIFF failure; the lit suite is the guard, so every implemented member needs a lit test.
- **Signature-matching burden.** Retargeting depends on exact signature equality with the reference assemblies; generics, `ref`/`in` parameters, and overload resolution through `modreq`s will bite. Mitigate by generating a list of the *reference-assembly* signatures for each type we implement and diffing it against our corelib in a unit test.
- **Roslyn-synthesised helpers.** String interpolation, `foreach` over arrays/spans, `switch` on strings and `params` all emit calls to corelib helpers (`DefaultInterpolatedStringHandler`, `string.op_Equality`, `RuntimeHelpers.InitializeArray`, `ComputeStringHash`). They are out of scope until a lit test needs them; each will surface as a "not in DotNetro CoreLib" error, which is the intended failure mode.
