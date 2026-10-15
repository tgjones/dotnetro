# Generics plan

Goal: C# generics (generic methods, classes, structs) compile to 6502 code, so that `List<T>`-style BCL code
(see `custom-corelib-plan.md`, stage 5) and user generics work.

## Decision: whole-program monomorphization first, sharing later (and probably never via dictionaries)

NativeAOT compiles one body per value-type instantiation and one *canonical* shared body (`__Canon`) per
reference-type instantiation, and pays for the sharing with runtime generic dictionaries. We don't want that
machinery yet:

- We are a whole-program compiler with a closed world: every instantiation is known at compile time.
- Reference types are all 2-byte pointers here, so a body translated for `List<Foo>` and for `List<Bar>` is
  *identical* MIR unless it names `T`-specific things (vtable symbol of T, `newobj T`, `castclass T`,
  `callvirt` on T). For the common case the identical-body sharing can be had **after the fact**: translate
  every instantiation, then fold bodies whose MIR is identical modulo the function name (identical code
  folding, as linkers do). No dictionaries, no `__Canon`, no change to the calling convention.
- Real canonical sharing (dictionaries, generic virtual methods, shared-code stack walking) is only worth it
  if ICF leaves too much duplication on a 32K machine. Decide with numbers from G5.

So: **every instantiation (value or reference type) is compiled separately; G5 adds ICF.**

## Where we are

Scaffolding exists but is stubs, and one piece is outright wrong:

- `TypeSystem/InstantiatedType`, `GenericParameter`, `Instantiation`, `Context.GetInstantiatedType/Generic*Parameter`
  exist; `InstanceSize`/`EncodedName` throw. `SignatureTypeProvider.GetTypeFromSpecification` throws;
  `GetGenericTypeParameter/MethodParameter` return a placeholder `GenericParameter`.
- `Instantiation` is `record Instantiation(TypeDescription[] …)`: **array reference equality**, so every
  `ConcurrentDictionary` keyed on it (`_instantiatedTypes`, `_generic*Parameters`) never hits. Needs value equality.
- `EcmaMethod` and `EcmaType` decode all signatures/fields/locals with `Instantiation.Empty` in their
  constructors; `EcmaAssembly.ResolveMethod` handles only `MethodDefinition` and `MemberReference` with a
  `TypeReference` parent (no `TypeSpec` parent, no `MethodSpecification`). `ResolveType` handles only
  `TypeDefinition`/`TypeReference` (no `TypeSpecification`).
- Translator state is keyed by `EcmaType`/`EcmaMethod` identity (`_visitedMethods`, `_usedTypes`,
  `_vtableLayouts`, `_vtableTypes`, `_staticFieldOwners`), and `GetVtableSlotIndex` uses
  `GetVtableLayout(...).IndexOf(callee)`, so method/type objects must be interned.
- Method symbol names are `UniqueName` = `<type>_<name>_<param EncodedNames>`; static fields and vtables are
  named from `EncodedName`. Instantiations must get distinct, assembler-safe names.
- Not yet supported and relevant: `constrained.` prefix, `ldtoken`, interface dispatch (`isinst`/`callvirt` on
  interfaces throw), `Delegate`/`ldvirtftn`. `Nullable<T>` is a layout stub only.

## Design

### 1. Decode with the *concrete* generic context (no substitution pass)

`ISignatureTypeProvider<TypeDescription, Instantiation>` already threads a generic context through every
`Decode*` call. Make that context `Instantiation(TypeArgs, MethodArgs)` (two immutable arrays, value-equal)
and have `GetGenericTypeParameter(ctx, i)` / `GetGenericMethodParameter(ctx, i)` **return `ctx.TypeArgs[i]` /
`ctx.MethodArgs[i]`** directly. Then decoding a field, local or signature of `List<int>.Add` with the right
context yields concrete types straight away; nothing downstream ever sees a `GenericParameter`. (Open
definitions are never compiled, so `GenericParameter` shrinks to a diagnostic-only type, or is deleted.)
`GetTypeFromSpecification` becomes `new SignatureDecoder(...).DecodeType(blob, ctx)`.

### 2. Make instantiation a property of `EcmaType` / `EcmaMethod`, interned in the context

Rather than a parallel `InstantiatedType` wrapper (which would force every `EcmaType`-typed use in the
translator to learn a second class), give `EcmaType` and `EcmaMethod` an `Instantiation` and intern them:

- `EcmaType` = (`TypeDefinitionHandle`, type args). `Instantiation.Empty` for non-generic types, so all
  existing behaviour is unchanged. Fields, base type (`class D : B<int>` decodes `TypeDefinition.BaseType`
  — a `TypeSpec` — with the type's own context), `InstanceSize`, `IsValueType`, `EncodedName`
  (`List_1__Int32`), static-constructor lookup all use that context.
- `EcmaMethod` = (`MethodDefinitionHandle`, declaring `EcmaType` with its type args, method args). Signature,
  locals and body decode with `Instantiation(declaring.TypeArgs, methodArgs)`. `UniqueName` includes both
  argument lists' `EncodedName`s.
- Interning key is `(definition handle, Instantiation)`, owned by `TypeSystemContext` (types) and by the
  declaring `EcmaType` (methods) — preserving reference equality that the translator's sets/dictionaries and
  `IndexOf` rely on. Remove the unused `InstantiatedType`.
- `GetFullName` for diagnostics should print `List<Int32>`; `EncodedName` must be symbol-safe and unique
  (guard against `Foo<A,B>` vs a type literally named `Foo_2__A_B`; use a separator that cannot appear in
  an identifier).

### 3. Resolve every token under the current method's context

`EcmaAssembly.ResolveMethod/ResolveType/ResolveField` gain an `Instantiation context` argument (the *calling
method's* `Instantiation`), and handle the missing handle kinds:

| Token | Resolution |
|---|---|
| `TypeSpecification` | decode blob with context → `EcmaType` (generic instance) / `SZArrayType` / pointer, … |
| `MemberReference`, parent `TypeSpecification` | decode parent with context → instantiated declaring type, then find the member by name + **instantiated** signature |
| `MethodSpecification` | resolve `.Method` (def or memberref), decode `.Signature` blob (method args) with context → instantiated `EcmaMethod` |
| `MethodDefinition` / `FieldDefinition` | as today, declaring type is the generic *definition* only if inside that definition's own body, which is already instantiated by the context (a `ldfld` token inside `List<int>.Add` names a `FieldDefinition` of `List<T>`; its owner must be mapped to the instantiation from the context) |

That last row is the subtle one: inside an instantiated body, tokens referring to the generic definition's
own members (`MethodDefinition`/`FieldDefinition` of the enclosing generic type, or of nested types) must be
re-instantiated with the *body's* type args. Implement as: if the definition's declaring type is generic,
instantiate it with the context's `TypeArgs` (a nested type's args are its enclosing types' args concatenated,
per ECMA-335 II.9; handle that when computing a nested type's arity).

`MethodTranslator` already routes every token through `_method.DeclaringType.Assembly.Resolve*`; change those
call sites (a mechanical sweep) to pass `_method.Instantiation`. `MethodSignatureUtility.AreCompatible` must
compare instantiated signatures (it already compares `TypeDescription`s; with interning, reference equality
suffices for generic instances).

### 4. Translator knock-ons

- Discovery: `EnqueueMethod` / `_usedTypes` / `_staticFieldOwners` now see instantiated objects; works unchanged
  once interned. Add an **instantiation depth/count limit** with a clear diagnostic (polymorphic recursion such
  as `void F<T>() => F<List<T>>()` otherwise diverges).
- Vtables: one per instantiated type (`Vtable_<EncodedName>`); `GetVtableLayout` /
  `FindOverriddenMethod` work on instantiated methods; a base `B<int>` vtable chain falls out. Overrides of
  a generic base's virtual with a differently-spelled but equal-after-substitution signature (`Foo : B<int>`
  overriding `Get(T)` as `Get(int)`) are found because both sides are decoded concretely.
- Static fields/cctors: per instantiation (`List<int>.s_empty` ≠ `List<byte>.s_empty`) — falls out of naming
  by `EncodedName`; the cctor list already runs "all touched types' cctors at startup".
- Type tests / `box` / `unbox.any` / `sizeof` / `initobj` / `newarr` / `ldelem` tokens: already take a type
  token; with context-aware `ResolveType` they handle `!T` (a `TypeSpec` of a generic param). `box T` for a
  reference `T` is already a no-op; `unbox.any T` for reference `T` already becomes `castclass`.
- Value-type generics: struct layout is per instantiation (`Pair<int,byte>`); scalar-vs-aggregate detection
  (`TryGetScalar` keys on core-lib names) is correct because a generic struct is never one of the named scalars.
- **`constrained.`** (G3): in a monomorphized body the constraint type is concrete, so
  `constrained. T; callvirt M` is resolved statically: reference `T` → plain `callvirt`; value `T` overriding
  `M` → direct call on the address (no boxing); value `T` not overriding → box and `callvirt` the base
  method. Prefix currently unsupported (listed in `il-support.md`).
- **Enums as type arguments:** `SignatureTypeProvider.Underlying` replaces an enum by its underlying integer
  in every signature, including inside generic arguments, so `List<MyEnum>` collapses to `List<int>`.
  That is consistent and shares code, but `box !T` inside it boxes an `Int32`, not a `MyEnum` (visible only in
  `ToString`/type tests). Accept and document; revisit if it bites.
- Not supported initially, with explicit `NotSupported` diagnostics: generic virtual methods (a vtable slot
  per instantiation of a virtual generic method — needs a closed-world fixpoint or dictionaries),
  `ldtoken`/`typeof(T)`, static abstract interface members, variance (`IEnumerable<out T>` conversions only
  matter with interface dispatch), `Nullable<T>` lifting operators beyond what C# lowers to plain calls.

### 5. Interface dispatch is out of scope (decided)

Generic *algorithms* only need G0–G3. `List<T> : IList<T>`, `foreach`, `IEquatable<T>`,
`EqualityComparer<T>.Default` and LINQ-shaped code need interface dispatch (`isinst`/`callvirt` on interfaces
currently throw); that gets its own plan, after G3. Generics make it more tractable because constraints
(`T : IEquatable<T>`) resolve statically via `constrained.`.

## Decisions (agreed)

- Interface dispatch: separate plan, not part of this one.
- Enums as type arguments collapse to their underlying integer for now (see §4); revisit later.
- Generic virtual methods, `ldtoken`/`typeof(T)` and variance: unsupported, with explicit diagnostics.

## Stages (each lands with lit tests: `RUN-dotnet` vs `RUN-emulated`, `DIFF: dotnet emulated`)

- [ ] **G0 – plumbing.** `Instantiation` value equality (+ unit test that equal instantiations intern to one
      `EcmaType`); type args on `EcmaType`, type+method args on `EcmaMethod`; context-aware decode in
      `SignatureTypeProvider`; `Resolve*` take a context and handle `TypeSpec` / `MethodSpec` / memberref-
      with-typespec; naming; depth limit. Non-generic code must be byte-identical (run the full suites; the
      goldens must not change).
- [ ] **G1 – generic methods.** `static T Max<T>(T a, T b)` with `int`/`byte`/`long`/ref types; `Swap<T>(ref T, ref T)`;
      generic method calling another generic method with its own `T`; generic method over a struct.
      Tests: `GenericMethodInt`, `GenericMethodRef`, `GenericSwap`, `GenericMethodNested`.
- [ ] **G2 – generic classes/structs.** `Box<T>`, `Pair<A,B>` (class and struct), `T[]` fields, static fields and
      cctor per instantiation, virtual methods and `class D : B<int>` overrides, generic nested use
      (`Box<Pair<int,byte>>`), `Nullable<int>` construction + `.Value`/`.HasValue`. Tests: `GenericClass`,
      `GenericStruct`, `GenericStatics`, `GenericInheritance`, `GenericNested`, `NullableInt`.
- [ ] **G3 – generic-aware ops.** `constrained.` call; `default(T)`/`initobj !T`; `isinst`/`castclass`/`box`/
      `unbox.any` with `T` and with generic instances (`o is Box<int>` vs `o is Box<byte>` distinguishes vtables);
      `sizeof(T)`. Update `il-support.md` (`dotnet run --project src/DotNetro.Tools.ILSupport`).
- [ ] **G4 – corelib uptake.** Switch `Nullable<T>` from stub to real; remove "no generic instantiation"
      notes from `custom-corelib-plan.md`. (`List<T>`/`Stack<T>` that don't need interfaces can follow here;
      the rest waits for the interface-dispatch plan.)
- [ ] **G5 – code size.** Count instantiated bodies on a `List<T>`-heavy sample; implement MIR-level ICF
      (hash the printed body with its own name normalised; redirect call sites/vtable slots/`ldftn` to the
      survivor); only consider canonical sharing if the numbers say ICF isn't enough.

## Risks

- **Symbol blow-up**: unshared instantiations multiply code on a 32K machine. Mitigation is G5 ICF plus the
  existing whole-program reachability (only used instantiations are translated).
- **Nested-type arity** and re-instantiating definition tokens inside generic bodies (§3) are the easiest place
  to be silently wrong; give them dedicated tests (a generic type with a nested non-generic type using `T`).
- **Interning discipline**: anything that constructs an `EcmaType`/`EcmaMethod` outside the context caches
  breaks `IndexOf`-based vtable slots and the visited-sets. Make constructors `internal` to the cache.

## Verification

`dotnet test --solution src` in Debug and Release (CI does both). Non-generic lit goldens must be unchanged by
G0. New behaviour is covered by emulator-executed lit tests (the only tests that run generated code).
