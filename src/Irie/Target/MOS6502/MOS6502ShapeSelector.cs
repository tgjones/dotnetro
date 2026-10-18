using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// Shape selection: legalized generic MIR -> the unpinned byte operations of the
// mos6502 dialect (adc, sbc, inc, dec, and, ora, eor, shle, lshre, cmp_br, load,
// store, const), with the register pinning left out.
//
// It selects only what does not decide placement:
//   - arith.cmpi + cf.cond_br (and the lexicographic select tree of a wide compare)
//     fuse into cmp_br ops, one per byte, exiting on the flags of that one compare;
//   - memory accesses become load / store of a symbol, or of a pointer pair;
//   - constants and address bytes become const.
// Nothing here names A, X, Y, a zero-page register or an addressing mode: the
// vregs keep their types, and a placement picks a variant of MOS6502Variants for
// each operation (see MOS6502BeamPlacementPass).
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
                PseudoOp.Return  => SelectReturn(instruction, builder),
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
    //
    // A step of one (x + 1, x - 1, x + 0xFF, x - 0xFF) is an inc or a dec instead: the
    // 6502 has INX, INY and INC for it, and a placement chooses among them.
    private static bool SelectBareAddSub(MirInstruction instr, MirBuilder builder, MOS6502Op op, int carryIn)
    {
        var function = builder.Function;
        builder.SetInsertionPointBefore(instr);

        if (TryGetIncDec(function, instr, op, out var step, out var source))
        {
            builder.BuildInstruction(MOS6502Dialect.OpRef(step), instr.Operands[0], source);
            builder.Remove(instr);
            return true;
        }

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

    // Whether `%r = addi|subi %a, %b` adds or subtracts one: the op to build and the
    // operand that is stepped. Adding is commutative, so the constant may be either.
    private static bool TryGetIncDec(
        MirFunction function, MirInstruction instr, MOS6502Op op, out MOS6502Op step, out MirOperand source)
    {
        step = default;
        source = instr.Operands[1];
        var isAdd = op == MOS6502Op.Adc;

        if (TryGetByteConstant(function, instr.Operands[2], out var value))
            source = instr.Operands[1];
        else if (isAdd && TryGetByteConstant(function, instr.Operands[1], out value))
            source = instr.Operands[2];
        else
            return false;

        if (source is not VirtualReg { IsDefinition: false }) return false;
        if (value is not (1 or 0xFF)) return false;
        step = (value == 1) == isAdd ? MOS6502Op.Inc : MOS6502Op.Dec;
        return true;
    }

    private static bool TryGetByteConstant(MirFunction function, MirOperand operand, out long value)
    {
        value = 0;
        if (operand is Immediate immediate)
        {
            value = immediate.Value & 0xFF;
            return true;
        }
        if (operand is not VirtualReg { IsDefinition: false } use
            || function.GetDefinition(use.Id) is not { } definition
            || definition.Operands.Length < 2
            || definition.Operands[1] is not Immediate constant)
            return false;

        var isConstant =
            (definition.Opcode.Dialect == ArithDialect.Id && (ArithOp)definition.Opcode.Code == ArithOp.Constant)
            || (definition.Opcode.Dialect == MOS6502Dialect.Id && (MOS6502Op)definition.Opcode.Code == MOS6502Op.Const);
        if (!isConstant) return false;
        value = constant.Value & 0xFF;
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
            var (predicate, bytes) = RecoverCmpTree(function, condId, toRemove);
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
    // legalizer's normalisation). The ladder:
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
        if (TryResolveSymbolAddress(function, addr.Id) is { } symbol)
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Load), def, new Symbol(symbol, (int)offset.Value));
        else
        {
            var (lo, hi) = ResolveRuntimePointerBytesOrThrow(function, addr.Id);
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
        if (TryResolveSymbolAddress(function, addr.Id) is { } symbol)
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.Store), value, new Symbol(symbol, (int)offset.Value));
        else
        {
            var (lo, hi) = ResolveRuntimePointerBytesOrThrow(function, addr.Id);
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
            || TryResolveSymbolAddress(function, source.Id) is not { } symbol)
            throw new NotSupportedException(
                "MOS6502ShapeSelector: a pseudo.unmerge that is not the two address bytes of a symbol survived legalization.");

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Const), lo, new Symbol(symbol), new Immediate(0));
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Const), hi, new Symbol(symbol), new Immediate(1));
        builder.Remove(instr);
        return true;
    }

    // pseudo.return → mos6502.rts. The return physregs are surfaced as
    // implicit uses on the rts, gathered by scanning backward from the
    // pseudo.return for a *contiguous* run of `$reg = pseudo.copy %v` (or
    // `$reg = pseudo.copy $reg`) instructions: AbiLowering.LowerReturn emits
    // exactly that run immediately before pseudo.return. Scanning only the
    // contiguous run avoids picking up unrelated physreg-target copies
    // emitted earlier in the block (e.g. the indirect-Y pointer setup that
    // mem.load/store lowering inserts).
    private static bool SelectReturn(MirInstruction instr, MirBuilder builder)
    {
        var block = instr.Parent!;
        var implicitUses = new List<MirOperand>();

        var index = block.Instructions.IndexOf(instr);
        for (var i = index - 1; i >= 0; i--)
        {
            var prev = block.Instructions[i];
            if (prev.Opcode.Dialect != PseudoDialect.Id) break;
            if ((PseudoOp)prev.Opcode.Code != PseudoOp.Copy) break;
            if (prev.Operands.Length == 0) break;
            if (prev.Operands[0] is not PhysicalReg phys || !phys.IsDefinition) break;
            implicitUses.Insert(0, new PhysicalReg(phys.Id, IsDefinition: false, IsImplicit: true));
        }

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Rts),
            implicitUses.ToArray());

        builder.Remove(instr);
        return true;
    }

    // Walk a wide-compare lexicographic tree rooted at `vreg`, returning the
    // overall canonical predicate and the per-byte operand vregs (index 0 = least
    // significant). Collects every tree instruction (selects, leaf cmpis, and the
    // arith.nots described below) into `treeInstrs` for the caller to erase.
    // Mirrors the shape built by MOS6502LegalizerInfo.BuildLexicographic:
    //   leaf:   %r = arith.cmpi <pred>, %a, %b
    //   select: %r = arith.select %eqHi, %rest, %cmpHi
    //           with %eqHi = cmpi eq, %aHi, %bHi  and  %cmpHi = cmpi <pred>, %aHi, %bHi
    //
    // The "rest" / lower-byte compares are built with the unsigned `ult`
    // predicate, which the legalizer's consumer-independent normalization then
    // rewrites to `uge` wrapped in an arith.not (exactly as llvm-mos does). The
    // ladder is driven entirely by the *overall* predicate (recovered from the
    // outermost high-byte compare) and the per-byte operands, so those per-byte
    // predicates and the nots wrapping them are irrelevant here — StripNots looks
    // through them, and the lower-level predicates are discarded.
    private static (ArithCmpPredicate predicate, List<(int a, int b)> bytes)
        RecoverCmpTree(MirFunction function, int vreg, List<MirInstruction> treeInstrs)
    {
        vreg = StripNots(function, vreg, treeInstrs);
        var def = function.GetDefinition(vreg)
            ?? throw new InvalidOperationException(
                $"MOS6502ShapeSelector: wide-compare tree vreg %{vreg} has no definition.");
        treeInstrs.Add(def);

        if (def.Opcode.Dialect == ArithDialect.Id && (ArithOp)def.Opcode.Code == ArithOp.CmpI)
        {
            // Leaf: the least-significant byte's compare.
            var leafPred = (ArithCmpPredicate)((Immediate)def.Operands[1]).Value;
            var a = ((VirtualReg)def.Operands[2]).Id;
            var b = ((VirtualReg)def.Operands[3]).Id;
            return (leafPred, [(a, b)]);
        }

        if (def.Opcode.Dialect == ArithDialect.Id && (ArithOp)def.Opcode.Code == ArithOp.Select)
        {
            // operands: [0]=def, [1]=cond(eqHi), [2]=true(rest), [3]=false(cmpHi)
            var restVreg = ((VirtualReg)def.Operands[2]).Id;
            var cmpHiVreg = StripNots(function, ((VirtualReg)def.Operands[3]).Id, treeInstrs);
            var eqHiVreg = StripNots(function, ((VirtualReg)def.Operands[1]).Id, treeInstrs);

            var cmpHiDef = function.GetDefinition(cmpHiVreg)!;
            var eqHiDef = function.GetDefinition(eqHiVreg)!;
            treeInstrs.Add(cmpHiDef);
            treeInstrs.Add(eqHiDef);

            // The overall predicate is this (outermost) level's high-byte compare;
            // lower levels use the unsigned "rest" predicate, which the ladder
            // derives itself, so the recursive predicate is discarded.
            var predicate = (ArithCmpPredicate)((Immediate)cmpHiDef.Operands[1]).Value;
            var aHi = ((VirtualReg)cmpHiDef.Operands[2]).Id;
            var bHi = ((VirtualReg)cmpHiDef.Operands[3]).Id;

            var (_, restBytes) = RecoverCmpTree(function, restVreg, treeInstrs);
            restBytes.Add((aHi, bHi));   // append the more-significant byte
            return (predicate, restBytes);
        }

        throw new NotSupportedException(
            $"MOS6502ShapeSelector: unexpected opcode in wide-compare tree at %{vreg}.");
    }

    // Follow a chain of `arith.not` definitions, collecting them into
    // `treeInstrs`, and return the underlying (non-not) vreg.
    private static int StripNots(MirFunction function, int vreg, List<MirInstruction> treeInstrs)
    {
        while (true)
        {
            var def = function.GetDefinition(vreg);
            if (def is null
                || def.Opcode.Dialect != ArithDialect.Id
                || (ArithOp)def.Opcode.Code != ArithOp.Not)
                return vreg;
            treeInstrs.Add(def);
            vreg = ((VirtualReg)def.Operands[1]).Id;
        }
    }

    private static string? TryResolveSymbolAddress(MirFunction function, int addrVreg)
    {
        var def = function.GetDefinition(addrVreg);
        if (def is not null
            && def.Opcode.Dialect == MemDialect.Id
            && (MemOp)def.Opcode.Code == MemOp.Symbol
            && def.Operands.Length >= 2
            && def.Operands[1] is Symbol symbolOperand)
        {
            return symbolOperand.Name;
        }
        return null;
    }

    // The two byte vregs (low, high) of a runtime i16 pointer. Throws if the
    // address vreg is not defined by a pseudo.merge of exactly two byte vregs —
    // other pointer shapes (frame slots, scalar i16 vregs that never went
    // through a merge) are not yet supported, so the gap stays visible.
    private static (int Lo, int Hi) ResolveRuntimePointerBytesOrThrow(MirFunction function, int addrVreg)
    {
        var def = function.GetDefinition(addrVreg);
        if (def is not null
            && def.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)def.Opcode.Code == PseudoOp.Merge)
        {
            var bytes = new List<int>();
            foreach (var op in def.Operands)
                if (op is VirtualReg v && !v.IsDefinition)
                    bytes.Add(v.Id);
            if (bytes.Count == 2)
                return (bytes[0], bytes[1]);
        }

        throw new NotSupportedException(
            "MOS6502ShapeSelector: runtime mem.load/store currently only supports " +
            "addresses produced by `mem.symbol @name` or a two-byte pseudo.merge (i16 " +
            "pointer). Other address sources (frame slots, scalar i16 vregs) land later.");
    }
}
