using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// Shape selection: legalized generic MIR -> the unpinned byte operations of the
// mos6502 dialect (adc, sbc, and, ora, eor, shle, lshre, cmp_br, load, store,
// const), with the register pinning left out.
//
// It keeps what MOS6502InstructionSelector does well and drops the part that
// decides placement:
//   - arith.cmpi + cf.cond_br (and the lexicographic select tree of a wide compare)
//     fuse into cmp_br ops, one per byte, exiting on the flags of that one compare;
//   - memory accesses become load / store of a symbol, or of a pointer pair;
//   - constants and address bytes become const.
// Nothing here names A, X, Y, a zero-page register or an addressing mode: the
// vregs keep their types, and a placement picks a variant of MOS6502Variants for
// each operation (see MOS6502NaivePlacementPass).
//
// Everything the call lowering already selected (jsr.abs, the frame accesses of
// stack arguments, rts) and the copies to and from fixed registers pass through.
public sealed class MOS6502ShapeSelector : Irie.Target.InstructionSelector
{
    public override bool Select(MirInstruction instruction, MirBuilder builder)
    {
        var opcode = instruction.Opcode;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return (ArithOp)opcode.Code switch
            {
                ArithOp.Constant   => SelectConstant(instruction, builder),
                ArithOp.AddI       => SelectBareAddSub(instruction, builder, MOS6502Op.Adc, carryIn: 0),
                ArithOp.SubI       => SelectBareAddSub(instruction, builder, MOS6502Op.Sbc, carryIn: 1),
                ArithOp.AddICarry  => Replace(instruction, builder, MOS6502Op.Adc),
                ArithOp.SubIBorrow => Replace(instruction, builder, MOS6502Op.Sbc),
                ArithOp.And        => Replace(instruction, builder, MOS6502Op.And),
                ArithOp.Or         => Replace(instruction, builder, MOS6502Op.Ora),
                ArithOp.Xor        => Replace(instruction, builder, MOS6502Op.Eor),
                ArithOp.CmpI => SelectCompareValue(instruction, builder),
                // Consumed wholesale by the cf.cond_br that uses them.
                ArithOp.Select or ArithOp.Not => true,
                _ => false,
            };
        }

        if (opcode.Dialect == CfDialect.Id)
        {
            return (CfOp)opcode.Code switch
            {
                CfOp.Br     => true,
                CfOp.CondBr => SelectCondBr(instruction, builder),
                _ => false,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return (PseudoOp)opcode.Code switch
            {
                PseudoOp.Copy or PseudoOp.Merge => true,
                PseudoOp.Unmerge => SelectUnmerge(instruction, builder),
                PseudoOp.Return  => MOS6502InstructionSelector.SelectReturn(instruction, builder),
                _ => false,
            };
        }

        if (opcode.Dialect == MemDialect.Id)
        {
            return (MemOp)opcode.Code switch
            {
                MemOp.Symbol      => true,
                MemOp.LoadByteAt  => SelectLoad(instruction, builder),
                MemOp.StoreByteAt => SelectStore(instruction, builder),
                _ => false,
            };
        }

        // Already-selected target ops pass through (the shifts are unpinned already).
        return opcode.Dialect == MOS6502Dialect.Id;
    }

    // What selection left dead: merges whose pointer was resolved to its byte
    // vregs, symbol addresses used only to address memory.
    public override void EndFunction(MirFunction function, MirBuilder builder)
    {
        bool removed;
        do
        {
            removed = false;
            var uses = new Dictionary<int, int>();
            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions)
                    foreach (var operand in instr.Operands)
                        if (operand is VirtualReg { IsDefinition: false } use)
                            uses[use.Id] = uses.GetValueOrDefault(use.Id) + 1;

            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions.ToList())
                {
                    var isCandidate =
                        (instr.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)instr.Opcode.Code == PseudoOp.Merge)
                        || (instr.Opcode.Dialect == MemDialect.Id && (MemOp)instr.Opcode.Code == MemOp.Symbol);
                    if (!isCandidate || instr.Operands[0] is not VirtualReg { IsDefinition: true } def) continue;
                    if (uses.GetValueOrDefault(def.Id) != 0) continue;
                    builder.Remove(instr);
                    removed = true;
                }
        } while (removed);
    }

    // The same operands under a new opcode (adc / sbc / and / ora / eor take the
    // layout of the generic op they replace).
    private static bool Replace(MirInstruction instr, MirBuilder builder, MOS6502Op op)
    {
        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(op), instr.Operands);
        builder.Remove(instr);
        return true;
    }

    // arith.constant of an i1 or i8 -> mos6502.const, defining the same vreg.
    private static bool SelectConstant(MirInstruction instr, MirBuilder builder)
    {
        var def = (VirtualReg)instr.Operands[0];
        var value = (Immediate)instr.Operands[1];
        var type = (builder.Function.GetVRegAnnotation(def.Id) as TypedVReg)?.Type;
        if (type != IRType.I1 && type != IRType.I8)
            throw new NotSupportedException(
                $"MOS6502ShapeSelector: arith.constant of {type?.DisplayName ?? "a classed vreg"} is not supported.");

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Const), def, value);
        builder.Remove(instr);
        return true;
    }

    // arith.addi / arith.subi on bytes: the carry-less form is an adc / sbc whose
    // carry in is a constant (0 for add, 1 for sub: the 6502 "no borrow") and whose
    // carry out nobody reads.
    private static bool SelectBareAddSub(MirInstruction instr, MirBuilder builder, MOS6502Op op, int carryIn)
    {
        var function = builder.Function;
        builder.SetInsertionPointBefore(instr);
        var carryInVreg = function.CreateVirtualRegister(IRType.I1);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Const),
            new VirtualReg(carryInVreg, IsDefinition: true),
            new Immediate(carryIn));

        var carryOut = function.CreateVirtualRegister(IRType.I1);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(op),
            instr.Operands[0],
            new VirtualReg(carryOut, IsDefinition: true),
            instr.Operands[1],
            instr.Operands[2],
            new VirtualReg(carryInVreg, IsDefinition: false));
        builder.Remove(instr);
        return true;
    }

    // ------------------------------------------------------------------
    // Compare and branch
    // ------------------------------------------------------------------

    // An arith.cmpi is normally consumed by the cf.cond_br above it (and left for
    // SelectCondBr). One whose i1 result is a value — the sign seed of an
    // arithmetic shift right, `cmpi uge x, 0x80`, feeding a rotate's carry — is the
    // carry a CMP leaves: mos6502.cmp.
    private static bool SelectCompareValue(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        var def = (VirtualReg)instr.Operands[0];
        var usedAsValue = function.Blocks.SelectMany(b => b.Instructions).Any(user =>
            user.Operands.Any(o => o is VirtualReg { IsDefinition: false } use && use.Id == def.Id)
            && !IsCompareConsumer(user));
        if (!usedAsValue) return true;

        var predicate = (ArithCmpPredicate)((Immediate)instr.Operands[1]).Value;
        if (predicate != ArithCmpPredicate.Uge)
            throw new NotSupportedException(
                $"MOS6502ShapeSelector: an arith.cmpi {ArithCmpPredicateNames.ToText(predicate)} whose result is a value " +
                "(not a branch condition) is not supported; only uge, the carry of a CMP, is.");

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Cmp), def, instr.Operands[2], instr.Operands[3]);
        builder.Remove(instr);
        return true;
    }

    // The ops a compare result is consumed by when it only decides a branch.
    private static bool IsCompareConsumer(MirInstruction user) =>
        (user.Opcode.Dialect == CfDialect.Id && (CfOp)user.Opcode.Code == CfOp.CondBr)
        || (user.Opcode.Dialect == ArithDialect.Id && (ArithOp)user.Opcode.Code is ArithOp.Not or ArithOp.Select);

    private bool SelectCondBr(MirInstruction condBr, MirBuilder builder)
    {
        var function = builder.Function;

        if (condBr.Operands.Length != 3
            || condBr.Operands[0] is not VirtualReg cond || cond.IsDefinition
            || condBr.Operands[1] is not BlockTarget t0
            || condBr.Operands[2] is not BlockTarget f0)
            throw new InvalidOperationException("MOS6502ShapeSelector: malformed cf.cond_br operand shape.");

        var toRemove = new List<MirInstruction>();
        var condId = cond.Id;
        var tTarget = t0;
        var fTarget = f0;

        // Strip arith.not wrappers (the legalizer's normalised-away inverse
        // predicate), swapping the targets for each.
        while (function.GetDefinition(condId) is { } def
               && def.Opcode.Dialect == ArithDialect.Id && (ArithOp)def.Opcode.Code == ArithOp.Not)
        {
            toRemove.Add(def);
            condId = ((VirtualReg)def.Operands[1]).Id;
            (tTarget, fTarget) = (fTarget, tTarget);
        }

        var rootDef = function.GetDefinition(condId)
            ?? throw new InvalidOperationException(
                $"MOS6502ShapeSelector: cf.cond_br condition %{condId} has no definition.");

        builder.SetInsertionPointBefore(condBr);

        if (rootDef.Opcode.Dialect == ArithDialect.Id && (ArithOp)rootDef.Opcode.Code == ArithOp.Select)
        {
            var (predicate, bytes) = MOS6502InstructionSelector.RecoverCmpTree(function, condId, toRemove);
            EmitLadder(builder, predicate,
                [.. bytes.Select(p => (MirOperand)new VirtualReg(p.a, IsDefinition: false))],
                [.. bytes.Select(p => (MirOperand)new VirtualReg(p.b, IsDefinition: false))],
                tTarget, fTarget);
        }
        else if (rootDef.Opcode.Dialect == ArithDialect.Id && (ArithOp)rootDef.Opcode.Code == ArithOp.CmpI)
        {
            var predicate = (ArithCmpPredicate)((Immediate)rootDef.Operands[1]).Value;
            EmitLadder(builder, predicate,
                [rootDef.Operands[2]], [rootDef.Operands[3]], tTarget, fTarget);
            toRemove.Add(rootDef);
        }
        else
        {
            throw new NotSupportedException(
                "MOS6502ShapeSelector: cf.cond_br whose condition is not a compare or a " +
                "wide-compare select tree is not supported.");
        }

        builder.Remove(condBr);
        foreach (var dead in toRemove)
            if (dead.Parent != null)
                builder.Remove(dead);
        return true;
    }

    // The compare of an N-byte value (index 0 least significant), one cmp_br per
    // byte from the top down, for a canonical predicate (eq, uge, slt after the
    // legalizer's normalisation). Same ladder as the colouring path's selector:
    //   eq:   each byte exits to F on ne; equal to the end falls to T
    //   ult:  each byte exits to T on cc and to F on ne; the last has only the cc exit
    //   uge:  each byte exits to F on cc and to T on ne; the last exits to T on cs
    // A signed slt runs the unsigned ult ladder after flipping bit 7 of the top
    // byte of both operands (a <s b  ==  (a ^ 0x80) <u (b ^ 0x80)), except a sign
    // test of the top byte against zero (an immediate 0), which is just mi.
    private static void EmitLadder(
        MirBuilder builder, ArithCmpPredicate predicate,
        MirOperand[] aBytes, MirOperand[] bBytes, BlockTarget tTarget, BlockTarget fTarget)
    {
        if (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Uge or ArithCmpPredicate.Slt))
            throw new NotSupportedException(
                $"MOS6502ShapeSelector: compare predicate '{ArithCmpPredicateNames.ToText(predicate)}' reached the " +
                "selector; after legalizer normalisation only eq, uge, slt are expected.");

        var count = aBytes.Length;
        var a = (MirOperand[])aBytes.Clone();
        var b = (MirOperand[])bBytes.Clone();
        var chain = predicate;

        if (predicate == ArithCmpPredicate.Slt)
        {
            var top = count - 1;
            if (count == 1 && b[top] is Immediate { Value: 0 })
            {
                EmitCmpBr(builder, a[top], b[top], [(CmpBrCondition.Mi, tTarget)]);
                EmitBr(builder, fTarget);
                return;
            }
            a[top] = EorWithImmediate(builder, a[top], 0x80);
            b[top] = EorWithImmediate(builder, b[top], 0x80);
            chain = ArithCmpPredicate.Ult;
        }

        for (var pos = count - 1; pos >= 0; pos--)
        {
            var isLast = pos == 0;
            (CmpBrCondition, BlockTarget)[] exits = chain switch
            {
                ArithCmpPredicate.Eq => [(CmpBrCondition.Ne, fTarget)],
                ArithCmpPredicate.Ult when isLast => [(CmpBrCondition.Cc, tTarget)],
                ArithCmpPredicate.Ult => [(CmpBrCondition.Cc, tTarget), (CmpBrCondition.Ne, fTarget)],
                ArithCmpPredicate.Uge when isLast => [(CmpBrCondition.Cs, tTarget)],
                _ => [(CmpBrCondition.Cc, fTarget), (CmpBrCondition.Ne, tTarget)],
            };
            EmitCmpBr(builder, a[pos], b[pos], exits);
        }

        // Falling off the last compare: equal for eq, "not taken" for the others.
        EmitBr(builder, chain == ArithCmpPredicate.Eq ? tTarget : fTarget);
    }

    private static void EmitCmpBr(
        MirBuilder builder, MirOperand a, MirOperand b, (CmpBrCondition Condition, BlockTarget Target)[] exits)
    {
        var operands = new List<MirOperand> { a, b };
        foreach (var (condition, target) in exits)
        {
            operands.Add(new Immediate((long)condition));
            operands.Add(target);
        }
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.CmpBr), [.. operands]);
    }

    private static void EmitBr(MirBuilder builder, BlockTarget target) =>
        builder.BuildInstruction(CfDialect.OpRef(CfOp.Br), target);

    // %r = eor %byte, 0x80 (a fresh i8 vreg), for the signed-compare bias.
    private static VirtualReg EorWithImmediate(MirBuilder builder, MirOperand byteOperand, long imm)
    {
        var function = builder.Function;
        var constVreg = function.CreateVirtualRegister(IRType.I8);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Const), new VirtualReg(constVreg, IsDefinition: true), new Immediate(imm));
        var result = function.CreateVirtualRegister(IRType.I8);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Eor),
            new VirtualReg(result, IsDefinition: true),
            byteOperand,
            new VirtualReg(constVreg, IsDefinition: false));
        return new VirtualReg(result, IsDefinition: false);
    }

    // ------------------------------------------------------------------
    // Memory
    // ------------------------------------------------------------------

    // %v = mem.load.byte_at %p, off  ->  %v = mos6502.load @sym+off   (a symbol address)
    //                                    %v = mos6502.load %lo, %hi, off  (a pointer)
    private static bool SelectLoad(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } def
            || instr.Operands[1] is not VirtualReg { IsDefinition: false } addr
            || instr.Operands[2] is not Immediate offset)
            throw new InvalidOperationException(
                "MOS6502ShapeSelector: mem.load.byte_at must have shape `%def : i8 = mem.load.byte_at %addr, <offset>`.");

        builder.SetInsertionPointBefore(instr);
        if (MOS6502InstructionSelector.TryResolveSymbolAddress(function, addr.Id) is { } symbol)
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Load), def, new Symbol(symbol, (int)offset.Value));
        else
        {
            var (lo, hi) = MOS6502InstructionSelector.ResolveRuntimePointerBytesOrThrow(function, addr.Id);
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Load),
                def, new VirtualReg(lo, IsDefinition: false), new VirtualReg(hi, IsDefinition: false), offset);
        }
        builder.Remove(instr);
        return true;
    }

    // mem.store.byte_at %p, off, %v  ->  mos6502.store %v, @sym+off | %v, %lo, %hi, off
    private static bool SelectStore(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg { IsDefinition: false } addr
            || instr.Operands[1] is not Immediate offset
            || instr.Operands[2] is not VirtualReg { IsDefinition: false } value)
            throw new InvalidOperationException(
                "MOS6502ShapeSelector: mem.store.byte_at must have shape `mem.store.byte_at %addr, <offset>, %val`.");

        builder.SetInsertionPointBefore(instr);
        if (MOS6502InstructionSelector.TryResolveSymbolAddress(function, addr.Id) is { } symbol)
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Store), value, new Symbol(symbol, (int)offset.Value));
        else
        {
            var (lo, hi) = MOS6502InstructionSelector.ResolveRuntimePointerBytesOrThrow(function, addr.Id);
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Store),
                value, new VirtualReg(lo, IsDefinition: false), new VirtualReg(hi, IsDefinition: false), offset);
        }
        builder.Remove(instr);
        return true;
    }

    // pseudo.unmerge of a `mem.symbol @name` (an address used as a value, e.g. a
    // string passed to a call) -> the two address bytes as mos6502.const @name, 0|1.
    private static bool SelectUnmerge(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } lo
            || instr.Operands[1] is not VirtualReg { IsDefinition: true } hi
            || instr.Operands[2] is not VirtualReg { IsDefinition: false } source
            || MOS6502InstructionSelector.TryResolveSymbolAddress(function, source.Id) is not { } symbol)
            throw new NotSupportedException(
                "MOS6502ShapeSelector: a pseudo.unmerge that is not the two address bytes of a symbol survived legalization.");

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Const), lo, new Symbol(symbol), new Immediate(0));
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Const), hi, new Symbol(symbol), new Immediate(1));
        builder.Remove(instr);
        return true;
    }
}
