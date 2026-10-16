# irie-report

Compares Irie's MOS6502 codegen against the llvm-mos reference corpus. Two
subcommands:

```bash
# Build iriec + this tool, then:

# Score the whole corpus → markdown scoreboard:
dotnet run --project src/Irie.Tools.Reference -- generate-report
# → writes doc/irie/llvm-mos-comparison.md, prints the aggregate to stdout

# Diff one case's pipeline, stage by stage → HTML artifact:
dotnet run --project src/Irie.Tools.Reference -- generate-diff ext/llvm-mos-reference/basics/add-i8.c
# → writes artifacts/irie-diff/basics-add-i8.html (uncommitted)
```

## `generate-report`

Scores Irie's MOS6502 codegen against the whole corpus and writes a markdown
scoreboard. The generated
[`doc/irie/llvm-mos-comparison.md`](../../doc/irie/llvm-mos-comparison.md) is
checked in to git — regenerate it after codegen changes and commit the diff.

For each case in [`ext/llvm-mos-reference/`](../../ext/llvm-mos-reference) it pairs
the committed llvm-mos final assembly (`.s`) with the matching Irie test (same
basename) under
[`Lit/CodeGen/MOS6502/LlvmMosReference/`](../Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference),
runs `iriec --target mos6502 --emit=asm` on the test, counts **real instructions**
on each side (skipping directives, labels, comments), and reports:

- a **headline aggregate** — total Irie vs total llvm-mos instructions, as a ratio
  and percentage, over the paired cases;
- a **table** — per-case llvm-mos count, Irie count, Δ, ratio, and status
  (`converted` / `blocked` / `error`), sorted worst-first;
- **collapsible per-case assembly** (`<details>`) — the llvm-mos and Irie listings
  for each case.

Options (all optional):

| Flag | Default |
|------|---------|
| `--corpus` | `ext/llvm-mos-reference` |
| `--tests`  | `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference` |
| `--iriec`  | the `iriec` built alongside this tool |
| `--out`    | `doc/irie/llvm-mos-comparison.md` |

Beyond instruction count the report gives **code bytes**: the llvm-mos `.s` and
Irie's `--emit=asm` output are each assembled with the llvm-mos toolchain
(`clang --target=mos -c`, then `llvm-size -A`, summing the `.text*` sections), so
both sides go through the same encoder and data/`.bss` are excluded. The toolchain
is found via `--llvm-mos-bin`, else `$LLVM_MOS_BIN`; without it the byte columns
show `—`.

A test can exclude its case from the headline totals with a header line (the case
stays in the table, marked 🚫):

- `; Duplicate-of: <category>/<name>` — the MIR is the same shape as another case,
  so counting both would double-weight it (the four `coalescing/` cases);
- `; Incomparable: <reason>` — the two outputs do different work (`loop-counter`,
  where llvm-mos emits a closed-form `__mulsi3` call).

Instruction count is the headline metric — apples-to-apples without assembling
llvm-mos. Every `.irie` pairing is a **faithful mirror** of its corpus `.c` —
adapting the input to dodge an Irie limitation is not allowed (see the suite
README's *Fidelity rule*). A few deltas are still apples-to-oranges, but only
because of *output*-level optimizations Irie's pipeline does not perform (e.g.
loop strength reduction in `loop-counter`) — never because the input was
simplified; see the suite README's pairing caveat.

## `generate-diff`

Lays one case's two pipelines side by side, stage by stage, as a self-contained
HTML artifact (written under `artifacts/` — gitignored). The left column is the
committed llvm-mos `-print-changed` IR dump (`<case>.txt`); the right column is
the Irie MIR captured from `iriec --print-changed`, one panel per pass.

Stages are aligned at the shared GlobalISel-style backbone (start, ABI/arg
lowering, legalization, instruction selection, PHI elimination, two-address,
register allocation, copy cleanup, pseudo expansion). Stages with no counterpart
on one side — llvm's LLVM-IR-level optimization and analysis passes, Irie's
target-specific passes — render single-sided, and that's expected. Passes that
ran without changing the IR are shown dimmed.

Pass the `.c`, `.txt`, or any sibling of a corpus case as the argument; the
`.txt` (llvm dump) and `.c` (source) are derived. Options:

| Flag | Default |
|------|---------|
| `--tests` | `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference` |
| `--iriec` | the `iriec` built alongside this tool |
| `--out`   | `artifacts/irie-diff/<category>-<name>.html` |

See [`ext/llvm-mos-reference/README.md`](../../ext/llvm-mos-reference/README.md)
for the improvement workflow this scoreboard drives.

## Reading the totals

Whole-corpus totals mix output that does different work, so read them with care.
With 36 cases in the totals (1918 Irie vs 1885 llvm-mos instructions, 3168 vs 3517
bytes), the aggregate looks near parity, but:

| Cases | llvm-mos | Irie | Why |
| --- | --: | --: | --- |
| `many-calls`, `stack-args` | 178 + 122 | 134 + 92 | Different frame strategy: llvm-mos spills through a soft-stack pointer (`__rc0/__rc1` adjusted in the prologue/epilogue and reached via `(__rc0),y`) and a static stack; Irie saves callee-saved registers with `PHA`/`PLA`. Neither is obviously better — they trade instruction count against cycles and stack safety. |
| everything else (34 cases) | 1585 | 1692 | Irie is about 7% worse; the worst ratios are the loop, pointer and boolean cases (`crc8`, `array-index`, `gcd`, `shift-const`, `two-pointer-copy`, `loop-sum-array`, `compare-flags`). |

Instruction count and bytes diverge (Irie is ahead on bytes): Irie leans on
zero-page operands (2-byte encodings) where llvm-mos emits the same number of
instructions with absolute or indexed forms and extra pointer set-up. Use the
per-case ratios (worst-first) to pick what to improve, and compare designs on the
same case set before and after a change rather than against the absolute llvm-mos
total. The figures quoted in `notes/status-2026-10-07.md` (568 vs 676) predate the
corpus growing from 23 to 43 converted cases.
