# Compiler diagnostics with source locations

Goal: a proper list of diagnostics, with source locations, for a DotNetro compilation request. Useful for IDEs and the web playground.

Decisions:
- **PDBs are optional.** Without one, diagnostics carry a symbol-level location (method/type) only.
- **Multi-error collection (step 3) is deferred.** First cut is single-error diagnostics with locations.

## Current state

- `CompileDiagnostic(Severity, Message, Source)` in `src/DotNetro.Compiler/CompilerDriver.cs` has no location.
- `CompilerDriver.Compile(CompileRequest)` catches only `NotSupportedException` and turns it into a single diagnostic, so the first problem aborts the compile. About 25 `throw new NotSupportedException/InvalidOperationException` sites in `IlToMirTranslator.cs` carry the information as text (e.g. `"IL→MIR: opcode X is not supported yet (method Foo.Bar)"`).
- `src/DotNetro.WebPlayground/Playground.cs`: the JSON `Diagnostic(Severity, Message, Line, Column, Source)` already has line/column. `ToDiagnostic` fills them for Roslyn; target diagnostics pass `null, null`.
- `ClientApp/src/panels/problemsPanel.ts` already merges C# and target diagnostics and makes rows with a line clickable (`store.reveal`).
- `RoslynCompiler.cs` calls `compilation.Emit(stream)` with no PDB, so the DLL has no sequence points.
- `MirVerificationException` (Irie verifier) and other `InvalidOperationException`s are not caught and surface as crashes.

## Step 1 — Structured diagnostic model

- Extend `CompileDiagnostic` with a stable `Code` (e.g. `DNRO0001` unsupported opcode, `DNRO0002` unsupported BCL override, `DNRO0003` missing runtime function, `DNRO9xxx` internal errors), an optional `SourceSpan? Location` (file, line, column, end line, end column) and an optional `Symbol` (method/type). `Source` stays as the phase (`translator`, `irie`).
- Add `CompilerDiagnosticException` (subclass of `NotSupportedException`, so existing catches keep working) carrying code, message and optional IL context. Convert the throw sites; keep current message text.
- In the translator's per-opcode loop (`IlToMirTranslator.cs`, the `default:` case region around the opcode switch), wrap the loop body in `try/catch` that attaches `(method, ilOffset)` to the exception. Deeper sites (e.g. `GetVtableSlotIndex`) then need not know the position. Sites outside a method body (vtable layout, discovery) get symbol-level location only.
- Resolve locations in one place, `CompilerDriver`, so the translator has no PDB dependency.

## Step 2 — Optional portable PDB and location resolution

- `CompileRequest` gains `byte[]? DebugSymbols` (default null). `dnrc` on a bare DLL, and any caller without a PDB, simply gets symbol-level locations.
- New `SequencePointMap` in `DotNetro.Compiler`, built with `System.Reflection.Metadata` (`MetadataReader` over a portable PDB): `(MethodDefinitionHandle, ILOffset)` → `(file, startLine, startCol, endLine, endCol)`. Use the nearest preceding sequence point; skip hidden points (line `0xFEEDFEED`).
- Playground: `RoslynCompiler` emits a portable PDB (`compilation.Emit(peStream, pdbStream)` with `EmitOptions(debugInformationFormat: PortablePdb)`). `CSharpResult` returns it (base64) alongside the assembly, and `CompileForTarget` passes it into the request.
- Release (optimised) IL has fewer sequence points, so locations are coarser; consider Debug for the playground, or accept it.
- Playground `Diagnostic` gains `EndLine`/`EndColumn`; `Playground.cs` maps `CompileDiagnostic.Location` to them; TS `protocol.ts` and `problemsPanel.ts` follow.

## Step 3 — Multiple errors (deferred)

Not in the first cut. Notes for later:
- Catch the diagnostic exception per method in the `_methodsToVisit` drain loop, record it, skip the function, continue.
- Mark failed methods so callers do not produce cascading errors.
- Stop before the Irie passes if any translation errors exist; return all diagnostics and no image.
- Genuine compiler bugs (`InvalidOperationException`) stay fatal.

## Step 4 — Consumers

- Playground: Monaco squiggles via `monaco.editor.setModelMarkers`, with the diagnostic code shown.
- `dnrc`: print MSBuild-format lines (`file(line,col): error DNRO0001: …`) and return a non-zero exit code on errors.
- Catch internal errors (`InvalidOperationException`, `MirVerificationException`) in the driver/playground and report them as `DNRO9xxx` "internal compiler error" diagnostics instead of an unhandled exception in the worker.

## Step 5 — Back-end (Irie) errors (later)

- Verifier and pass failures have no direct source position. Attribute them to the function being processed (name → originating `EcmaMethod` → declaration location).
- Carrying a `DebugLoc` through MIR (LLVM-style) is a much larger change and not needed for a first version.

## Warnings (as they arise)

`DiagnosticSeverity.Warning`/`Info` and `Succeeded` already handle non-fatal diagnostics. Candidates: unsupported attributes, behaviour differences that still compile. No new infrastructure required.

## Testing

- Unit tests in `DotNetro.Compiler.Tests` for `SequencePointMap` using small Roslyn-compiled snippets with a PDB.
- Negative tests alongside `CompilerDriverTests.cs`: programs with an unsupported opcode / BCL override; assert code, symbol, and (with PDB) line and column; assert symbol-only location without a PDB.
- Possible later: a diagnostics lit flavour (`// CHECK: error DNRO0001` against `dnrc` output), reusing `dotlit --regenerate`.

## Order

1. Step 1 (model + exception + conversion of throw sites).
2. Step 2 (optional PDB, `SequencePointMap`, playground plumbing).
3. Step 4 (squiggles, `dnrc` output, internal-error handling).
4. Steps 3 and 5 when wanted.
