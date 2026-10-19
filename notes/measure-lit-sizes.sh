#!/bin/zsh
# Program size of every C# lit program, as `dnrc --emit program` bytes (runtime included).
#
#   notes/measure-lit-sizes.sh <out.tsv> [baseline.tsv] [Debug|Release]
#
# Needs a build first (dotnet build src -c <config>). Writes `program<TAB>bytes` lines to
# <out.tsv> and prints the total. With a baseline, also prints each program whose size
# differs and the total difference.
OUT=$1; BASE=$2; CONFIG=${3:-Debug}
S=${0:A:h:h}/src
CS=$S/DotNetro.Compiler.Tests.CsCompiler/bin/$CONFIG/net10.0/DotNetro.Compiler.Tests.CsCompiler
DNRC=$S/DotNetro.Compiler.Driver/bin/$CONFIG/net10.0/dnrc
: > $OUT
for f in $S/DotNetro.Compiler.Tests/Lit/*.cs; do
  n=${f:t}
  tmp=$(mktemp -d)
  $CS $f > $tmp/a.dll 2>/dev/null
  b=$($DNRC --assembly $tmp/a.dll --emit program 2>/dev/null | wc -c | tr -d ' ')
  echo "$n\t$b" >> $OUT; rm -rf $tmp
done
awk -F'\t' '{t+=$2} END {print "total", t, "bytes in", NR, "programs"}' $OUT
if [ -n "$BASE" ]; then
  join -t$'\t' <(sort $BASE) <(sort $OUT) | awk -F'\t' '$2!=$3 {printf "%s\t%d -> %d (%+d)\n", $1, $2, $3, $3-$2; d+=$3-$2} END {print "difference", d+0}'
fi
