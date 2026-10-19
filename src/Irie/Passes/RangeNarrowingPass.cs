using Irie.Dialects.Arith;
using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

/// <summary>
/// Value-range analysis on generic SSA MIR: annotates each integer vreg with the interval of values it can
/// hold (<c>%5 : i32 in [0, 766] = ...</c>), so later steps can shrink the arithmetic to the bytes it needs.
/// </summary>
/// <remarks>
/// The analysis is <see cref="ValueRangeAnalysis"/>. Runs after <see cref="ArithSimplifyPass"/> (constants are
/// canonical) and before <see cref="SinglePredecessorBlockParamsPass"/>, which erases the block parameters that
/// carry per-edge ranges.
/// </remarks>
public sealed class RangeNarrowingPass : MirFunctionPass
{
    public override string Name => "RangeNarrowing";

    public override void Run(MirFunction function)
    {
        // A constant's range is its value, which its definition already prints.
        var constants = new HashSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Opcode == ArithDialect.OpRef(ArithOp.Constant)
                    && instruction.Operands[0] is VirtualReg def)
                    constants.Add(def.Id);

        foreach (var (vreg, range) in ValueRangeAnalysis.Compute(function))
            if (!constants.Contains(vreg))
                function.SetValueRange(vreg, range);
    }
}
