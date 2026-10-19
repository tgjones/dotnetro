using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Mir;

namespace Irie.Passes;

/// <summary>
/// Replaces a <c>cf.cond_br</c> whose condition is a constant with a <c>cf.br</c> to the target it always
/// takes, on generic SSA MIR.
/// </summary>
/// <remarks>
/// The branch keeps the arguments of the taken edge. The other edge, with its arguments, disappears; the block
/// it led to may now be unreachable, which <see cref="UnreachableBlockEliminationPass"/> cleans up. A condition
/// constant that nothing uses afterwards is removed.
///
/// This needs only constants, not ranges: it folds constants from the front end and from
/// <see cref="ArithSimplifyPass"/> as well as the compares <c>RangeFoldPass</c> decides.
/// </remarks>
public sealed class ConstantBranchFoldPass : MirFunctionPass
{
    public override string Name => "ConstantBranchFold";

    public override void Run(MirFunction function)
    {
        var constants = new Dictionary<int, MirInstruction>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Opcode == ArithDialect.OpRef(ArithOp.Constant)
                    && instruction.Operands is [VirtualReg { IsDefinition: true } def, Immediate])
                    constants[def.Id] = instruction;

        var builder = new MirBuilder(function);
        var folded = new List<MirInstruction>();
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions.ToArray())
            {
                if (instruction.Opcode != CfDialect.OpRef(CfOp.CondBr)
                    || instruction.Operands is not [VirtualReg { IsDefinition: false } condition,
                                                    BlockTarget whenTrue, BlockTarget whenFalse]
                    || !constants.TryGetValue(condition.Id, out var constant))
                    continue;

                var taken = (((Immediate)constant.Operands[1]).Value & 1) != 0 ? whenTrue : whenFalse;
                builder.SetInsertionPointBefore(instruction);
                builder.BuildInstruction(CfDialect.OpRef(CfOp.Br), taken);
                builder.Remove(instruction);
                folded.Add(constant);
            }
        }

        foreach (var constant in folded.Distinct())
            if (constant.Operands[0] is VirtualReg def && function.GetUseCount(def.Id) == 0)
                builder.Remove(constant);

        if (folded.Count > 0)
            function.RebuildCfg();
    }
}
