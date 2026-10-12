# NuGet onboarding plan

Goal: add a `DotNetro` NuGet package to a console csproj, write `Console.WriteLine("Hello world")`, build, and find a `.ssd` in the output folder. `dotnet run` should then do something useful.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <DotNetroTargetMachine>BbcMicro</DotNetroTargetMachine> <!-- optional; default -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DotNetro" Version="…" />
  </ItemGroup>
</Project>
```

## What I found in the repo (so this plan needs no re-exploration)

- `src/DotNetro.Samples.HelloWorld/DotNetro.Samples.HelloWorld.csproj` already has the hand-written version of this: an `AfterTargets="Build"` target `Transpile` (with `Inputs`/`Outputs`, `FileWrites`) that `Exec`s `dotnet run --project …DotNetro.Compiler.Driver -- --assembly $(TargetPath) --output $(TargetName).ssd`, plus a `SetRunArguments` target overriding `RunCommand` (b-em on Windows, `open -a /Applications/b2.app` elsewhere). The package's targets are the productised form of these.
- `src/DotNetro.Compiler.Driver/Program.cs` (`dnrc`): options `--assembly`, `--output`, `--emit` only. No `--target`. Always calls `CompilerDriver.Compile(path, "Main")`. For `Executable` emit it also writes `.asm` and `.lst` next to the output (`.lst` is always empty).
- `src/DotNetro.Compiler/CompilerDriver.cs`: `Compile(path, entryPoint)` hard-codes `new MOS6502BbcMicroTarget()`. Errors are thrown as exceptions.
- `IlToMirTranslator.Translate` falls back from the entry point name to `<Main>$` (top-level statements), so Program.cs with top-level code already works.
- `Console.WriteLine` is special-cased by the translator and routed to `DotNetro.Runtime.ConsoleHelper`, so the user's project needs no reference to the runtime library for hello world. `src/DotNetro` (runtime library) is resolved by the compiler via the host runtime (`TypeSystemContext.ResolveAssembly(AssemblyName)`), so `dnrc` must ship `DotNetro.dll` beside it.
- `src/Directory.Build.props`: `DotNetVersion=net10.0`. `global.json`: SDK 10.0.100.
- `notes/web-playground-plan.md` also needs an allow-list of supported BCL APIs; the analyzer in stage 4 should share it.

## Design

### 1. `DotNetro` package = MSBuild glue + portable compiler

- New project (e.g. `src/DotNetro.Package`) that packs:
  - `build/DotNetro.props`, `build/DotNetro.targets`, auto-imported by `PackageReference`. Add `buildTransitive/` copies that import the same files.
  - `tools/net10.0/`: framework-dependent `dnrc.dll` with all dependencies (Compiler, Irie, `DotNetro.dll`, System.CommandLine). Portable, no per-RID packages; the user already has the .NET 10 SDK.
- Targets run `dotnet "$(MSBuildThisFileDirectory)../tools/net10.0/dnrc.dll" …` via `Exec`. Not an in-proc `Task`: loading System.Reflection.Metadata into MSBuild's process risks version conflicts.
- Build target: `AfterTargets="Build"`, `Inputs=$(TargetPath)`, `Outputs=$(DotNetroOutputPath)`, `FileWrites` entry so `dotnet clean` removes it. Also copy to the publish/`OutDir` as appropriate.
- `ParseExitCode` / `ConsoleToMSBuild` as needed so `dnrc` stderr in MSBuild canonical format reaches the error list.

### 2. Properties

| Property | Default | Notes |
|---|---|---|
| `DotNetroTargetMachine` | `BbcMicro` | Only valid value today; unknown values give a clear error (`DNRO0002`). |
| `DotNetroEntryPoint` | `Main` (with `<Main>$` fallback) | Optional. |
| `DotNetroOutputPath` | `$(OutDir)$(AssemblyName).ssd` | |
| `DotNetroEmulator` | unset | Used by `dotnet run`. |

Code changes: add `--target <name>` to `dnrc`; give `CompilerDriver.Compile` a target parameter (a small registry mapping `BbcMicro` → `MOS6502BbcMicroTarget`); pass `--entry-point` through.

### 3. IDE-friendly diagnostics

`dnrc` should catch the compiler's "unsupported" failures and print MSBuild canonical format, e.g.
`Program.cs(3,1): error DNRO0001: Unsupported IL opcode 'ldelem.r4' in Foo.Bar()`.
Today these are exceptions with stack traces. Source locations need portable PDB sequence points (best effort; fall back to just the method name). Introduce a `DotNetroException`/diagnostic type in `DotNetro.Compiler` for the translator to throw instead of generic exceptions, and map the existing "not implemented" throws onto it. This is probably the largest part of the experience given the small supported subset.

### 4. `dotnet run`

The package overrides `RunCommand`/`RunArguments` (otherwise `dotnet run` runs the .NET build on the host):
1. `DotNetroEmulator` if set.
2. Known emulators (b2, b-em, BeebEm) on PATH or in standard install locations.
3. Otherwise print the `.ssd` path and a link to the web playground, exit 0.

Remove the hard-coded paths from the sample csproj when it switches to the package.

### 5. Template

`DotNetro.Templates` package: `dotnet new dotnetro` produces the csproj above plus `Program.cs` with `Console.WriteLine("Hello world");`.

### 6. Later

- Roslyn analyzer shipped in the package that flags unsupported BCL APIs while typing (shares the web-playground allow-list).
- Headless `--run` using the Aemula-based emulator tool as a smoke test (no video/keyboard).

## Stages

1. **Compiler plumbing:** `--target`, `--entry-point`, target registry in `CompilerDriver`, `DotNetroException` + canonical-format diagnostics in `dnrc`. Lit/unit coverage for the error format.
2. **Package:** pack project, props/targets, `tools/` payload. Switch `DotNetro.Samples.HelloWorld` to consume the package (via a local feed) in place of its hand-written target.
3. **Integration test:** pack to a temp local feed, scaffold a project, `dotnet build` it with that feed, assert the `.ssd` exists, run it through `DotNetro.Compiler.Tests.Emulator` and compare output with the host .NET run. Run in CI (Debug and Release).
4. **`dotnet run` emulator discovery** and the template package.
5. **Versioning/publish:** MinVer from git tags; CI publish job on tag; README "Getting started" rewritten around `dotnet add package DotNetro`.
6. Analyzer and headless run, if wanted.

## Decisions (defaults, open to change)

- Package id `DotNetro`, not an MSBuild SDK (`<Project Sdk="DotNetro.Sdk/…">` is cleaner but isn't the requested workflow).
- Exec of a framework-dependent `dnrc.dll`, not an in-proc task.
- `BbcMicro` is the default target machine so the minimal csproj is just the package reference.

## Risks / open questions

- Package size: `dnrc` plus Irie plus dependencies; check it is reasonable.
- The user's own project compiles as ordinary .NET too, so IntelliSense will happily offer APIs the compiler can't translate; the analyzer (stage 6) is the mitigation, diagnostics (stage 1) the floor.
- Debug vs Release IL: the translator rejects some unoptimised patterns (see the `Optimize` comment in `src/DotNetro/DotNetro.csproj`). The package may need to default `Optimize=true` for user projects until that is fixed; verify in stage 3.
