#!/usr/bin/env python3
"""Measures the placement search under a set of --tune settings.

    python3 -I src/Irie.Tools.Reference/sweep.py [--config Release] [--jobs N] "beam=8" "beam=16,lookahead=2" ...

For each setting (the empty string is the defaults) it compiles
  * the llvm-mos corpus with iriec (code bytes) and runs the RUN-exec lines of those
    cases with irie-exec (cycles of the functions under test), and
  * every C# lit program and benchmark with dnrc (code bytes, the sum of the placement
    cost the search reports) and runs it in the emulator (cycles of the whole program),
and prints one row with the totals and the wall time of the compiles. Build the tools in
the configuration first. The C# programs are compiled with Roslyn once and kept in
a cache directory (--cache).
"""
import argparse, concurrent.futures as cf, os, re, shlex, subprocess, sys, tempfile, time

SRC = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def tool(project, exe, config):
    return os.path.join(SRC, project, "bin", config, "net10.0", exe)


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, **kw)


def corpus_cases():
    root = os.path.join(SRC, "Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference")
    for d, _, files in os.walk(root):
        for f in sorted(files):
            if f.endswith(".irie"):
                yield os.path.join(d, f)


def corpus_one(path, tune, t):
    result = {"bytes": 0, "cycles": 0, "fail": 0, "cost": 0, "capped": 0}
    args = ["--tune", tune] if tune else []
    p = run([t["iriec"], path, "--target", "mos6502", "--emit=bin", "--origin=0x1900", "--stats", *args])
    stats = dict(re.findall(r"^(.+?): (\d+)$", p.stderr.decode(), re.M))
    result["cost"] = int(stats.get("placement.cost", 0))
    result["capped"] = int(stats.get("placement.capped-steps", 0))
    if p.returncode == 0:
        result["bytes"] = len(p.stdout)
    else:
        result["fail"] += 1
    for line in open(path):
        m = re.match(r";\s*RUN-exec:\s*(.*)", line)
        if not m:
            continue
        words = shlex.split(m.group(1))
        words = [path if w == "@file" else t["emulator"] if w == "@emulator" else t["exec"] if w == "@irie-exec" else w
                 for w in words]
        p = run([*words, *args])
        if p.returncode != 0:
            result["fail"] += 1
            print(f"FAIL {path}: {(p.stdout + p.stderr).decode()[:400]}", file=sys.stderr)
        for c in re.findall(r"(\d+) cycles", p.stdout.decode()):
            result["cycles"] += int(c)
    return result


def cs_programs(cache, t):
    lit = os.path.join(SRC, "DotNetro.Compiler.Tests/Lit")
    os.makedirs(cache, exist_ok=True)
    for d, _, files in os.walk(lit):
        for f in sorted(files):
            if not f.endswith(".cs"):
                continue
            dll = os.path.join(cache, os.path.relpath(os.path.join(d, f), lit).replace(os.sep, "_")[:-3] + ".dll")
            if not os.path.exists(dll):
                p = run([t["cs"], os.path.join(d, f)])
                if p.returncode != 0:
                    continue
                open(dll, "wb").write(p.stdout)
            yield dll, os.path.join(d, f)


def cs_one(dll, tune, t):
    with tempfile.NamedTemporaryFile(suffix=".bin") as out:
        args = ["--tune", tune] if tune else []
        start = time.time()
        p = run([t["dnrc"], "--assembly", dll, "--emit", "Program", "--stats", "--output", out.name, *args])
        elapsed = time.time() - start
        if p.returncode != 0:
            return None
        stats = dict(re.findall(r"^(.+?): (\d+)$", p.stderr.decode(), re.M))
        size = os.path.getsize(out.name)
        cycles = 0
        e = run([t["emulator"], out.name, "--measure", "2000", "--max-ticks", "300000000"])
        m = re.search(r"cycles (\d+)", e.stderr.decode())
        if e.returncode == 0 and m:
            cycles = int(m.group(1))
        return {"bytes": size, "cycles": cycles, "cost": int(stats.get("placement.cost", 0)),
                "capped": int(stats.get("placement.capped-steps", 0)), "time": elapsed}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="Release")
    ap.add_argument("--jobs", type=int, default=os.cpu_count())
    ap.add_argument("--cache", default=os.path.join(tempfile.gettempdir(), "dotnetro-sweep"))
    ap.add_argument("--corpus-only", action="store_true")
    ap.add_argument("settings", nargs="*", default=[""])
    a = ap.parse_args()
    t = {"iriec": tool("Irie.Tools.Compiler", "iriec", a.config),
         "exec": tool("Irie.Tools.Exec", "irie-exec", a.config),
         "dnrc": tool("DotNetro.Compiler.Driver", "dnrc", a.config),
         "cs": tool("DotNetro.Compiler.Tests.CsCompiler", "DotNetro.Compiler.Tests.CsCompiler", a.config),
         "emulator": tool("DotNetro.Compiler.Tests.Emulator", "DotNetro.Compiler.Tests.Emulator", a.config)}
    cases = list(corpus_cases())
    programs = [] if a.corpus_only else list(cs_programs(a.cache, t))
    print(f"{'setting':44} {'corp B':>7} {'corp cyc':>10} {'C# B':>8} {'C# cyc':>12} {'cost':>9} {'capped':>6} {'cmp s':>6} {'fail':>4}")
    with cf.ThreadPoolExecutor(a.jobs) as pool:
        for tune in a.settings:
            corpus = list(pool.map(lambda c: corpus_one(c, tune, t), cases))
            cs_all = list(pool.map(lambda p: cs_one(p[0], tune, t), programs))
            cs = [r for r in cs_all if r]
            fails = sum(r["fail"] for r in corpus) + cs_all.count(None)
            print(f"{tune or '(defaults)':44} {sum(r['bytes'] for r in corpus):7} {sum(r['cycles'] for r in corpus):10} "
                  f"{sum(r['bytes'] for r in cs):8} {sum(r['cycles'] for r in cs):12} {sum(r['cost'] for r in cs + corpus):9} "
                  f"{sum(r['capped'] for r in cs + corpus):6} {sum(r['time'] for r in cs):6.0f} {fails:4}", flush=True)


main()
