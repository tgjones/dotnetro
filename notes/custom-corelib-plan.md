# Custom CoreLib plan

Goal: replace the host's `System.Private.CoreLib` (read today through `HostAssemblyResolver`, i.e. `Assembly.Load` on the host) with a small, DotNetro-owned, 8-bit-friendly core library, kept out-of-tree. Later, selectively vendor pure-managed upstream files (`List<T>`, …) into it (the "hybrid"). Do **not** fork dotnet/runtime.

## Key decision: bind at DotNetro-compile time, not at Roslyn-compile time

User programs keep compiling with Roslyn against the **standard reference assemblies** (as `DotNetro.Compiler.Tests.CsCompiler` does now). `dnrc` then *redirects* every framework assembly name (`System.Runtime`, `mscorlib`, `netstandard`, `System.Console`, `System.Private.CoreLib`, …) directly onto our corelib, `DotNetro.CoreLib`, resolving types and members by (namespace, name, signature). The host's facade and implementation DLLs are never loaded. This is how NativeAOT's ILC works: the user assembly is built against the ref pack and the compiler is handed the target's implementation corelib.

Why not make users compile against our corelib with `NoStdLib`:
- The `DIFF: dotnet emulated` lit tests run the *same* DLL on the host runtime; a corelib-bound DLL cannot run there.
- `notes/nuget-onboarding-plan.md` has users add a `PackageReference` to an ordinary `net10.0` console project; they cannot reasonably swap their corelib.
- `notes/web-playground-plan.md` already compiles user code against real reference assemblies.

Consequence: our corelib's public members must match the reference assemblies' names and signatures for everything we implement (hard requirement, not a nicety). A user call to something we lack fails at DotNetro-compile time with "type/member not found in DotNetro CoreLib" rather than at Roslyn time. The allow-list/analyzer in the playground and NuGet plans is the user-facing mitigation.

## What I found in the repo (so this plan needs no re-exploration)

All paths under `src/`.

- **Core assembly resolution.** `DotNetro.Compiler/TypeSystem/TypeSystemContext.cs`: `CoreAssembly` = `ResolveAssembly(new AssemblyName("System.Private.CoreLib"))`; `RuntimeAssembly` = `"DotNetro"`. `ResolveAssembly(AssemblyName)` delegates to an `IAssemblyResolver` (`AssemblyResolver.cs`, already present with `HostAssemblyResolver` and `InMemoryAssemblyResolver`). `HostAssemblyResolver` does `Assembly.Load(fullName).Location` on the **host**, so `System.Runtime` resolves to the host's facade DLL and (through `EcmaAssembly.GetTypeImpl`, which already follows `ExportedType` → `AssemblyReference` forwarders) to host `System.Private.CoreLib`; `System.Console` resolves to the host's real implementation DLL, which is not a facade (it is only avoided today because the translator special-cases `Console` calls). Assemblies are cached by reference name and deduplicated by the image's real identity (`_assembliesByIdentity`), so two reference names that resolve to the same image share one `EcmaAssembly`. `WellKnownType` has only `ValueType`.
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

## Design

### Project and naming
- New project `DotNetro.CoreLib` (`src/DotNetro.CoreLib/`), **assembly name `DotNetro.CoreLib`** (file `DotNetro.CoreLib.dll`), deliberately *not* `System.Private.CoreLib`, so ours can never be confused with the host's file or identity (any `System.Private.CoreLib` identity in the type-system context is then a bug and testable as such). The name does not matter to anything else: user DLLs compiled against the ref pack never reference `System.Private.CoreLib` (only the host's facades and implementation assemblies do, and we never load those; every framework name goes through the redirect map below), and vendoring upstream source (the hybrid path) is independent of the assembly name.
- Built with `<NoStdLib>true</NoStdLib>`, `<NoConfig>true</NoConfig>`, `AllowUnsafeBlocks`, `Optimize=true` (as `DotNetro.csproj` does today, for the same reason: minimal IL), referencing nothing. It defines `System.Object` itself, so Roslyn compiles it as a bare-metal corelib; it must declare every well-known type Roslyn demands (compile errors name the missing ones).
- **Merge the existing runtime library into it** (`DotNetro.Runtime` namespace, `ConsoleHelper`/`ManagedHeap`/`MathHelper`). `Console` (in corelib) must call `ConsoleHelper`, and the helpers are `internal`; a separate assembly would force them public and create a cycle. `TypeSystemContext.RuntimeAssembly` then returns `DotNetro.CoreLib`; retire the `DotNetro` project (the NuGet *package* name `DotNetro` is unrelated).
- Output is a build artefact the compiler locates via a resolver, not via `Assembly.Load`.

### Per-target customisation: one corelib build per target
Targets differ in how they talk to the machine (BBC Micro `osasci`, C64 `CHROUT`, …), so the corelib is **compiled once per target** from a shared source set plus per-target files. No runtime/`IsSupported` gating and no compiler dead-code pruning: the target-specific code is simply the code that was compiled in.
- **Per-class, per-target files**, named `<Class>.<Target>.cs` next to the shared `<Class>.cs`, e.g. `Console.cs` (the public `System.Console` surface, identical for every target) and `Console.BbcMicro.cs` / `Console.C64.cs` (the platform half). The shared file declares what it needs from the platform as `partial` methods (`private static partial void PutChar(byte c);`), which each target's file implements. A target that forgets one fails at C# compile time. Platform code is *not* centralised in one `Platform` class.
- **Selection rule:** a file whose name has an extra dot-suffix (`*.<Target>.cs`) belongs only to that target's project; a plain `*.cs` file is shared. Each target's csproj includes the shared files plus `**/*.<Target>.cs`, so adding a target does not touch the others.
- **Projects:** the shared sources live in one folder; each target has a small csproj (`DotNetro.CoreLib.BbcMicro`, …) with the same properties as today (`NoStdLib`, …) and **the same assembly name `DotNetro.CoreLib`**, so the redirect map is unchanged. Each emits its DLL to a per-target location (e.g. `corelib/bbcmicro/DotNetro.CoreLib.dll`).
- **Resolver is told the target:** `HostAssemblyResolver` (and `AssemblySet.CreateResolver(target)` in the playground, which already takes the target) supplies that target's `DotNetro.CoreLib.dll`. `TargetDescriptor` does not need to know about assembly files.
- **Public API shape is the same across targets** for everything under `System.*` (users compile once against the ref pack). Target-only public APIs (e.g. a mode-7 screen API) are future work: they would live in a user-facing `DotNetro.Runtime` reference assembly that is a facade forwarding to `DotNetro.CoreLib`, as `System.Runtime` does for the BCL. Not part of this plan, and it will **not** get an `IsSupported`-style API: users who want per-target code multi-compile their program, exactly as we do for the corelib.
- Initially the platform half still calls `[MethodImpl(InternalCall)]` externs routed to `runtime.irie` by name (the existing mechanism). Stage 6 replaces them with C#.

### Binding / retargeting (`TypeSystemContext`)
- `IAssemblyResolver` already exists (`AssemblyResolver.cs`); keep it as the byte supplier. The redirect policy lives in `TypeSystemContext.ResolveAssembly(AssemblyName)`, **before** `resolver.Resolve`, so `HostAssemblyResolver` can never be asked for the host's CoreLib. The resolver supplies `DotNetro.CoreLib.dll` (beside `dnrc` by default; embedded/in-memory for the playground).
- **Redirect map** (reference name → `DotNetro.CoreLib`), one mechanism for all framework names: `mscorlib`, `netstandard`, `System.Runtime`, `System.Private.CoreLib`, `System.Console`, `System.Runtime.Extensions`, `System.Collections`, `System.Memory`, `System.Runtime.InteropServices`, … Any other framework assembly name → error naming the assembly (never silently load a host DLL). The user's own DLL and non-framework references load normally.
- **Host facades are not used.** Redirecting names directly means no facade DLL is loaded or bundled: the playground ships only the ref pack (for Roslyn) and `DotNetro.CoreLib`, and behaviour does not depend on the host's .NET version. (`System.Console` is a real implementation assembly, not a facade, so it could never have been reused anyway; today it only works because the translator special-cases `Console` calls without translating the body.) `GetTypeImpl`'s `ExportedType` following remains for non-framework assemblies.
- Trade-off accepted: we maintain the name list, and a BCL type we lack reports "not found in DotNetro.CoreLib" without saying where the real one lives (reusing facades would have named the forwarded-to assembly).
- Types/members not found raise a single `NotSupportedException`-style diagnostic: `DotNetro CoreLib does not implement System.X.Y(sig)`. (Align the exception type with the diagnostic work in the NuGet plan §3.)
- Member lookup must match on **signature** (it already passes the decoded signature to `GetMethod`); for generic members this will need `TypeSpec` parents later.

### Foundational types (hand-written, compiler-known layout)
`Object`, `ValueType`, `Enum`, `Void`, `Boolean`, `Byte`, `SByte`, `Int16`, `UInt16`, `Char`, `Int32`, `UInt32`, `Int64`, `UInt64`, `IntPtr`, `UIntPtr`, `String`, `Array`, `Attribute`, `Nullable<T>`, `Delegate`/`MulticastDelegate` (as calli work requires), `Exception` (empty hierarchy until the exceptions plan), `MethodImplAttribute`/`MethodImplOptions`, `IDisposable`, `Console`.
- Primitive structs only need to *exist* with the right names and the arithmetic/`ToString` members user code calls; the translator keeps mapping the ELEMENT_TYPE codes. The `ElementSize`/`ldelem` name switches become ordinary type resolution (stage 1 cleanup).
- Keep `int` = 32-bit, `long` = 64-bit, `char` = 16-bit (language semantics); `IntPtr`/`nint` = 16-bit (`PointerSize`).
- Where we implement a member, copy upstream's **name, accessibility and signature exactly** (ref-assembly-visible surface). Internals are free to differ.

### String
- **Decision: `char` stays 16-bit; `String` *storage* is one byte per character.** The `char` type is unchanged (the IL has no way to tell `char` from `ushort`/`short` at `ldind.u2`/`ldelem.u2`, and host/target semantics must match), but a string stores each character in a single byte (Latin-1/ASCII). It is a heap object using the array layout: `[vtable ptr][u16 length][bytes…][NUL]`; the indexer widens the byte to a 16-bit `char` and `Concat`/`Equals`/etc. work on bytes. This halves string RAM on a 64 KB machine and makes output a straight byte copy. Reuses `AllocArray`-style allocation; written in C# with unsafe pointer arithmetic over the object.
- **Known divergence from the host:** a character above 0xFF cannot be stored. `ldstr` of such text is replaced with `?` at DotNetro-compile time (as the current `Encoding.ASCII` code does), so e.g. `s[i] == '€'` is true on the host and false on the target. Stores of a computed `char` above 0xFF into a string (via `Concat`/builders, later) truncate to the low byte. Document it; do not try to emulate UTF-16.
- **Expected usage:** we don't expect users to lean on `string` much; keep it simple and correct rather than clever.
- `ldstr` emits a static string **object** (vtable ptr + length + bytes + NUL) and pushes a pointer to it, and the CR hack goes away: `WriteLine(string)` prints the bytes then a newline (`osasci(13)`, which already expands CR → CR LF). Existing goldens/emulator outputs should be unchanged since the emulator output is what `DIFF` compares.
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
1. Create `src/DotNetro.CoreLib` (assembly `DotNetro.CoreLib`, NoStdLib) with the foundational types above. `Console` is a stub whose members are `[MethodImpl(InternalCall)]`/forwarders matching today's behaviour; keep the existing `UniqueName` special-cases working by name (`System_Console_*` is unchanged because the namespace/type/method names are identical).
2. Add the redirect map to `TypeSystemContext.ResolveAssembly(AssemblyName)`; point `CoreAssembly` and `RuntimeAssembly` at `DotNetro.CoreLib`. Make an unmapped framework assembly an error (this is how you prove nothing still reads host CoreLib).
3. Replace the typeref-name switches in `ElementSize`/`TranslateLdelem`/`ElementIRType` with resolved corelib types (`EcmaType.IsValueType`, `.Size`; primitives need `Size` from `PrimitiveTypeCode` by name since their layout is compiler-defined — add a small name→`PrimitiveTypeCode` map used by `ResolveType`).
4. `DotNetro.Compiler.Driver` references the corelib project (copy-to-output beside `dnrc`); delete `src/DotNetro/` once merged (move `Runtime/*.cs`).
5. Gate: all 100 lit tests pass; `Assembly.Load` no longer reachable for framework names (add a unit test that compiling `HelloWorld` with the host's CoreLib path poisoned still works).

### Stage 2 — Console as ordinary C#
First split the corelib build per target (see "Per-target customisation"): turn the single `DotNetro.CoreLib` project into shared sources plus a `DotNetro.CoreLib.BbcMicro` project, have the resolver pick the DLL by target, and update `dnrc`/tests/playground to copy or embed the BBC Micro build.
Then implement `Console.WriteLine(string|int|bool)`, `ReadLine`, `Beep` as C#: public surface in `Console.cs`, platform half in `Console.BbcMicro.cs` calling uniquely named, neutrally named `InternalCall` externs (`PutChar` rather than `osasci`, `WriteLineInt32`, …). Delete the `System_Console_*` `UniqueName` switch in `TranslateDirectCall` and `GetWriteLineStringMethod`'s special pinning where no longer needed. `WriteLineInt32`/`Boolean` may stay hand-written MIR initially; `Int32.ToString` in C# is a later optional step (needs `MathHelper` division).

### Stage 3 — Real `String` (byte-per-char)
Implement the string layout, `ldstr` emitting string objects, `Length`/indexer/`Concat`/`Equals`/`==`, `ReadLine` returning a heap string, and the `WriteLine` newline change. Add lit tests: `string.Length`, indexer, `==`, concat, `ReadLine` round-trip (extend `--input`), and a string held in a field/array (reference-type element).

### Stage 4 — Object virtuals, `Enum`, `Nullable<T>`, `box`
Add real `Object` virtual slots; implement `box`/`unbox.any`/`castclass`/`isinst` in the translator against corelib types; `Enum`, `Nullable<T>` shapes. New lit tests per opcode (they appear in `il-support.md` after regenerating it with `dotnet run --project src/DotNetro.Tools.ILSupport`).

### Stage 5 — First vendored type (spike, then commit)
Vendor `List<T>`. Measure: how much code it pulls in, what translator features it needs. Write down the result in this file and decide whether `Dictionary` is worth trimming or hand-writing.

### Stage 6 — Move `runtime.irie` to C# (later; Irie work first)
Goal: target code such as the OS-call wrappers, `ReadLine`, `Beep`, `WriteLineBoolean`/`WriteLineInt32` becomes ordinary C# in the per-target corelib files, leaving only compiler-internal MIR (the `calli` trampoline). `WriteLineInt32`'s MIR header says it is hand-written because the allocator was spill-free; spilling now exists, so try the C# version and compare size/speed. The one thing C# cannot express today is calling a ROM address with arguments in specific registers. Plan:
1. **Per-function calling conventions in Irie.** Today there is one convention per target (`Target.CallLowering`, CC_MOS). Make the convention a property of the function and of each call site: `MirFunction` and `call.func`/`call.indirect` carry a convention id (printed and parsed in MIR text); `Target` exposes the conventions it supports and `AbiLoweringPass` and the call lowering pick the right one. The existing abstract `CallLowering` is already the right shape, so each convention is an instance of it (renaming it `CallingConvention` is optional): `MOS6502CallLowering` stays CC_MOS, and the BBC Micro target adds a `BbcMicroOs` convention (A/X/Y in; A/X/Y/carry out; the clobber set the ROM actually has) next to the BBC-specific code, not in the chip-generic code. Clobber/preserve sets must come from the convention so the register allocator is correct across ROM calls.
2. **Fixed-address calls.** Lower a `call.indirect` whose target is `inttoptr(constant)` to `jsr.abs $addr` instead of going through the trampoline.
3. **C# surface:** function pointers with a custom calling-convention modifier, e.g. `((delegate* unmanaged[BbcMicroOs]<byte, void>)0xFFE3)(c)`. The `CallConv*` marker types live in the corelib (to verify: Roslyn should accept any `System.Runtime.CompilerServices.CallConv<Name>`); the translator maps the modifier to the Irie convention id.
4. Port wrappers one at a time, deleting each from `runtime.irie`, with lit tests (emulator output) unchanged.

## Open questions

1. ~~String storage~~ **Resolved:** one byte per character (halves RAM, matches the 8-bit target); the `char` type stays 16-bit. Characters above 0xFF are not representable: `ldstr` replaces them with `?` and the divergence from the host is documented (see String).
2. **Merge vs. keep `DotNetro` assembly** (recommended: merge, see above). The cost is that the NuGet `dnrc` tools folder ships one fewer DLL; the benefit is `internal` access and no cycle.
3. ~~Assembly name~~ **Resolved:** `DotNetro.CoreLib`, with `System.Private.CoreLib` (and `System.Console`, …) redirected to it at DotNetro-compile time.
4. ~~Facades: reuse vs. direct redirect~~ **Resolved:** direct redirect of all framework names, no host facades loaded or bundled (see Binding).
5. **Interaction with `notes/web-playground-plan.md`:** its "future direction" is compiling real stock CoreLib bodies and a *per-target* CoreLib "eventually". This plan chooses the custom corelib now and makes stock-CoreLib compilation the thing we *don't* do. Update that plan (and its WP2 spike) once this one is accepted.
6. **Diagnostics type** for "not in DotNetro CoreLib" (share with NuGet plan §3's `DNRO` codes).

## Risks

- **Silent behavioural drift from the host.** The same user DLL runs on the host (real BCL) and on the 6502 (our corelib). Anything where our `String`/`Console` semantics diverge is a latent DIFF failure; the lit suite is the guard, so every implemented member needs a lit test.
- **Signature-matching burden.** Retargeting depends on exact signature equality with the reference assemblies; generics, `ref`/`in` parameters, and overload resolution through `modreq`s will bite. Mitigate by generating a list of the *reference-assembly* signatures for each type we implement and diffing it against our corelib in a unit test.
- **Roslyn-synthesised helpers.** String interpolation, `foreach` over arrays/spans, `switch` on strings and `params` all emit calls to corelib helpers (`DefaultInterpolatedStringHandler`, `string.op_Equality`, `RuntimeHelpers.InitializeArray`, `ComputeStringHash`). They are out of scope until a lit test needs them; each will surface as a "not in DotNetro CoreLib" error, which is the intended failure mode.
