# Interpolated strings plan

Goal: this compiles and runs on the 6502, printing the same text as on the host runtime:

```csharp
var i = 0;
Console.WriteLine($"Hello, World {i}!");
```

**Prerequisite: everything in `generics-plan.md` (G0–G4) is done first.** This plan builds on it: generic methods
(`MethodSpec` calls, monomorphization) and the `constrained.` prefix are assumed to work, and nothing here
touches the translator's generics machinery.

## Why it fails today

`dnrc` stops with `Could not find type definition System.Runtime.CompilerServices.DefaultInterpolatedStringHandler`
(thrown while decoding the entry method's locals). Roslyn lowers a non-constant interpolated string to the handler
pattern. For the example above the user assembly references:

| Member ref | Notes |
|---|---|
| `DefaultInterpolatedStringHandler::.ctor(int literalLength, int formattedCount)` | struct local, `ldloca` + `call instance` |
| `::AppendLiteral(string)` | one per literal chunk |
| `::AppendFormatted<int>(int)` | the only `MethodSpec`: a **generic method** (hence the generics prerequisite) |
| `::ToStringAndClear()` | yields the `string` handed to `Console.WriteLine(string)` |

Both the lit tests (`DotNetro.Compiler.Tests.CsCompiler`) and the playground (`RoslynCompiler`) compile against the
real reference assemblies, so we cannot change what Roslyn emits. The corelib has to provide a type with the
**real handler's signatures**. It is never loaded by the .NET runtime, so it only needs the members Roslyn picks,
not the whole BCL surface. Roslyn picks the generic `AppendFormatted<T>(T)` for `int`, `bool`, `char`, `short`,
`byte` and a non-generic `AppendFormatted(string)` for `string` holes.

## Scope

In: holes of type `int`, `string`, `bool`, `char`, `short`, `byte`; any number of holes and literals; null `string`
holes (append nothing, as the real handler does). Interpolations with no holes or only constant holes are folded
to a `ldstr` by Roslyn and already work.

Out (a clear "missing method" compile error is the expected result, not a silent miscompile):
- alignment / format-string holes (`{i,5}`, `{i:X}`), i.e. the `AppendFormatted<T>(T, int)`, `(T, string)` and
  `(T, int, string)` overloads;
- `ReadOnlySpan<char>`, `object` and the other primitives (`uint`, `long`, …) as holes; the last need only a
  `ToString` each and are a natural follow-up;
- `FormattableString`, `IFormatProvider`, `string.Format`, and `$"..."` converted to a custom handler type.

## Design

### 1. The handler: `System.Runtime.CompilerServices.DefaultInterpolatedStringHandler`

A plain `public struct` rather than the real `ref struct`: Roslyn only needs the type to exist with the right
members, and a plain struct avoids any `IsByRefLike` attribute dependency.
New file `src/DotNetro.CoreLib/System/Runtime/CompilerServices/DefaultInterpolatedStringHandler.cs`, shared by all
targets.

State: `string _buffer` (the destination, used as raw storage) and `int _count` (characters written so far).

- **ctor(literalLength, formattedCount)**: allocate once, `String.AllocateString(literalLength + 11 * formattedCount)`.
  11 is the longest `int` text (`-2147483648`); `short`/`byte`/`char`/`bool` are shorter, so the common case never
  regrows. A `string` hole of unknown length is the only thing that can overflow.
- **AppendLiteral(string)** and **AppendFormatted(string)**: copy the characters of the argument (null appends
  nothing), growing first if needed.
- **AppendFormatted&lt;T&gt;(T value)**: append `value.ToString()` (null result or null `value` appends nothing). This
  is a normal generic method in the corelib; with T = `int`/`bool`/`char`/`short`/`byte` the `constrained.` call
  from G3 resolves to the struct's own `ToString` with no boxing.
- **ToStringAndClear()**: trim the buffer to `_count` and return it; set `_buffer = null` so a second call or a
  stray append is empty, like the real type. Trimming means lowering the string's length header, so `String`
  gets an internal method for that (the slack bytes stay in the bump heap; the heap does not free anyway).
- **Growth**: allocate a new string of `max(2 * capacity, needed)`, copy, drop the old one. Only reachable via a
  long `string` hole.

Copying reuses the same pointer loop `String.Concat`/`Append` already use (`String.GetChars()`); factor the
existing private `String.Append` into an `internal` helper rather than duplicating it.

### 2. `ToString` on the hole types

Today only `Int32` and `Boolean` have `ToString` (`Primitives.cs`). Add:
- `Byte`, `Int16`: widen to `int` and reuse `Int32.ToString`'s digit logic (move that logic to an internal static
  helper on `Int32`/`String` taking an `int`, so there is one copy).
- `Char`: a one-character string (`AllocateString(1)`, store the low byte; storage is Latin-1, matching `ldstr`'s
  `?` replacement for anything above 0xFF: do the same here so host and emulated output agree for the tested range).
- `SByte`/`UInt16`/`UInt32`/`Int64`… are deliberately not added now.

### 3. Compiler changes

Expected: **none** beyond what generics already provide. The translator treats the handler like any user struct,
and an unknown member resolves through the existing `TypeSystemContext` redirect to `DotNetro.CoreLib`. If a
generics stage turns out to leave a gap that this exercises (e.g. a generic method instantiated *from corelib
code reached via a struct instance call*), fix it in the generics machinery, not with a special case here.

## Stages

- [x] **I0 – failing test.** Add `src/DotNetro.Compiler.Tests/Lit/InterpolatedString.cs` with the standard
      `RUN-dotnet` / `RUN-emulated` / `RUN-emulated-c64` and the two `DIFF` lines. Start with the issue's example
      (`i = 0`); confirm it fails with the error above before any change.
- [x] **I1 – `ToString` for `Byte`, `Int16`, `Char`.** Shared digit helper on `Int32`; a small lit test
      (`ToStringPrimitives.cs`) calling `.ToString()` on each of int/short/byte/char/bool, incl. negatives and
      min/max values (`short.MinValue`, `byte.MaxValue`).
- [x] **I2 – handler skeleton.** `String` trim and shared-copy helpers; the handler type with ctor,
      `AppendLiteral`, `AppendFormatted(string)`, `ToStringAndClear`, no generic yet. Test: string-only
      interpolation (`$"a{s}b{t}"`).
- [x] **I3 – generic `AppendFormatted<T>`.** The issue's example passes; then int (positive, negative, zero,
      `int.MinValue`), bool, char, short, byte holes.
- [x] **I4 – edge cases.** Null `string` hole; several holes in a row and holes at the very start/end; a hole whose
      value is a method call or expression; an empty literal; a result longer than the size hint (a long `string`
      hole forces the growth path); the same interpolation in a loop (heap use grows per iteration, no GC: keep
      the iteration count small); an interpolated string passed as an argument (`Print($"…")`).
- [x] **I5 – verify and document.**
      - `dotnet test --solution src` in Debug and Release. The lit tests run Roslyn in Debug and optimised
        modes, so both IL shapes (the Release `constrained.` form in particular) are covered.
      - Size check with `notes/measure-lit-sizes.sh`: `HelloWorld` and every existing test must not grow (the
        handler is only linked when used).
      - Try the example in the playground (`RoslynCompiler` uses the same reference set, so no change expected).
      - Regenerate `doc/dotnetro/il-support.md` only if opcode coverage changed (it should not).
      - Mention interpolated strings and the supported hole types where the corelib's supported surface is
        documented, if such a page exists.

## Risks

- **Null check on `T`.** `AppendFormatted<T>` must not call `ToString` on a null reference-type `value`; the
  natural C# (`value is null` / `value?.ToString()`) boxes value-type `T` just to test it. Check the IL Roslyn
  emits for the corelib body and what G3 does with `box !!0` + `brtrue`; if it allocates for value types, decide
  between accepting that or restructuring (e.g. handle `string` through its own overload only, and call
  `ToString` unguarded for `T`, since every supported generic `T` here is a value type). Only the tests with
  `-0`/`MinValue` ints would reveal a heap-use regression, so also measure heap use for a loop.
- **Size hint too small.** `11 * formattedCount` is exact for the supported value types; the regrow path is only
  for `string` holes and must be covered by a test, since it is the only copy-and-replace path.
- **No GC.** Each interpolation allocates its buffer (plus a regrow buffer in the worst case) and the final string
  is the trimmed buffer, so nothing extra is retained; but a loop of interpolations still grows the heap until
  `gc-plan.md` lands. Keep the loop test small.
- **Char storage is Latin-1.** `char` holes above 0xFF print `?` on the emulator but the real character on the
  host; keep test values within 0–0xFF.

## Verification

`dotnet test --project src/DotNetro.Compiler.Tests` for the new lit tests as they land, then
`dotnet test --solution src` in Debug and Release at the end (CI does both). The emulator-executed lit tests are
the only ones that run generated code, so every behavioural claim above is covered by an `InterpolatedString*.cs`
test with `DIFF: dotnet emulated` (and `emulated-c64`).
