#!/bin/zsh
# Per-case code size (and cycles for the corpus) of one placement strategy.
#
#   notes/measure-placement.sh <colouring|naive|beam> <output dir>
#
# Needs a Release build first (dotnet build src/DotNetro.slnx -c Release). Writes
#   <dir>/corpus.tsv   case, bytes, cycles, ok      (43 LlvmMosReference cases: bytes of
#                      `iriec --emit=bin`; cycles summed over the case's RUN-exec vectors)
#   <dir>/cs.tsv       program, bytes               (the C# lit programs and benchmark
#                      kernels: bytes of `dnrc --emit program`, runtime included)
# `ok` is 0 when a case failed to compile or run (it is then left out of the totals).
# Takes about 2.5 minutes.
P=$1; O=$2; mkdir -p $O
S=${0:A:h:h}/src
I=$S/Irie.Tests/bin/Release/net10.0
EMU=$S/DotNetro.Compiler.Tests.Emulator/bin/Release/net10.0/DotNetro.Compiler.Tests.Emulator
CS=$S/DotNetro.Compiler.Tests.CsCompiler/bin/Release/net10.0/DotNetro.Compiler.Tests.CsCompiler
DNRC=$S/DotNetro.Compiler.Driver/bin/Release/net10.0/dnrc
: > $O/corpus.tsv
for f in $S/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/*/*.irie; do
  n=$(basename $(dirname $f))/$(basename $f .irie)
  b=$($I/iriec --target mos6502 --emit=bin --origin 0x1000 --placement $P $f 2>/dev/null | wc -c | tr -d ' ')
  cyc=0; ok=1
  while IFS= read -r d; do
    r=$($I/irie-exec $f --target mos6502-bbcmicro --emulator $EMU --placement $P --exec "$d" 2>&1)
    c=$(echo "$r" | sed -n 's/.* \([0-9]*\) cycles.*/\1/p' | head -1)
    if [ -z "$c" ]; then ok=0; else cyc=$((cyc+c)); fi
  done < <(grep '^; RUN-exec' $f | sed -n 's/.*--exec "\(.*\)"$/\1/p')
  [ $b -eq 0 ] && ok=0
  echo "$n\t$b\t$cyc\t$ok" >> $O/corpus.tsv
done
: > $O/cs.tsv
for f in $S/DotNetro.Compiler.Tests/Lit/*.cs $S/DotNetro.Compiler.Tests/Lit/Benchmarks/*.cs; do
  n=${f#$S/DotNetro.Compiler.Tests/Lit/}
  tmp=$(mktemp -d)
  $CS $f > $tmp/a.dll 2>/dev/null
  b=$($DNRC --assembly $tmp/a.dll --emit program --placement $P 2>/dev/null | wc -c | tr -d ' ')
  echo "$n\t$b" >> $O/cs.tsv; rm -rf $tmp
done
