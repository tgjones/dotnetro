using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Cast;
using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Dialects.Mem;
using Irie.Mir;

namespace Irie.Tools.Interpreter;

/// <summary>
/// A reference interpreter for pre-pipeline (generic-dialect) MIR: the ground
/// truth that compiled code is checked against. It shares nothing with the
/// legalizer, instruction selector or register allocator; it only reads the
/// parsed MirModule, so a bug in those passes cannot hide in it.
/// </summary>
/// <remarks>
/// Values are the low `width` bits of a ulong (pointers are 16-bit addresses).
/// Operations with no defined result throw InterpreterException rather than
/// picking an answer.
/// </remarks>
public sealed class MirInterpreter
{
    /// <summary>
    /// Globals are laid out from here, in module order; the first page is left
    /// unmapped-by-convention so a null pointer never aliases a global.
    /// </summary>
    private const int GlobalsBase = 0x0100;

    private readonly MirModule _module;
    private readonly Dictionary<string, MirFunction> _functions = [];
    private readonly Dictionary<string, int> _globalAddresses = [];
    private readonly int _maxSteps;
    private int _steps;

    /// <summary>64 KiB flat address space, as on the 6502.</summary>
    public byte[] Memory { get; } = new byte[0x10000];

    /// <summary>
    /// maxSteps bounds the instructions executed across all calls since the last
    /// Reset, so a non-terminating program fails the check instead of hanging it.
    /// </summary>
    public MirInterpreter(MirModule module, int maxSteps = 10_000_000)
    {
        MirBootstrap.EnsureRegistered();
        _module = module;
        _maxSteps = maxSteps;

        foreach (var function in module.Functions)
            _functions[function.Name] = function;

        LayOutGlobals();
    }

    /// <summary>The address the interpreter gave a global.</summary>
    public int GetGlobalAddress(string name) =>
        _globalAddresses.TryGetValue(name, out var address)
            ? address
            : throw new InterpreterException($"Unknown global '@{name}'.");

    /// <summary>
    /// Calls `function` with the given arguments (each the value's bit pattern;
    /// high bits beyond the parameter's width are ignored). Returns the result
    /// bit pattern, or null for a void function.
    /// </summary>
    public ulong? Call(string function, params ulong[] arguments)
    {
        _steps = 0;
        return Invoke(Lookup(function), arguments);
    }

    private MirFunction Lookup(string name) =>
        _functions.TryGetValue(name, out var function)
            ? function
            : throw new InterpreterException($"Call to unknown function '@{name}'.");

    private void LayOutGlobals()
    {
        var next = GlobalsBase;
        foreach (var global in _module.Globals)
        {
            if (global.FixedAddress is { } fixedAddress)
            {
                _globalAddresses[global.SymbolName] = fixedAddress;
                continue;
            }

            _globalAddresses[global.SymbolName] = next;
            next += global.SizeInBytes;
            if (next > Memory.Length)
                throw new InterpreterException("Globals do not fit in the 64 KiB address space.");
        }

        foreach (var global in _module.Globals)
        {
            if (global.Initializer is null)
                continue;

            var address = _globalAddresses[global.SymbolName];
            foreach (var item in global.Initializer.Items)
            {
                switch (item)
                {
                    case DataBytes bytes:
                        bytes.Bytes.CopyTo(Memory, address);
                        address += bytes.Bytes.Length;
                        break;
                    case DataSymbolRef reference:
                        var target = GetGlobalAddress(reference.SymbolName);
                        Memory[address++] = (byte)target;
                        Memory[address++] = (byte)(target >> 8);
                        break;
                    default:
                        throw new InterpreterException($"Unsupported data item {item.GetType().Name}.");
                }
            }
        }
    }

    private ulong? Invoke(MirFunction function, ulong[] arguments)
    {
        if (arguments.Length != function.ParameterTypes.Length)
            throw new InterpreterException(
                $"@{function.Name} takes {function.ParameterTypes.Length} argument(s), got {arguments.Length}.");

        var values = new Dictionary<int, ulong>();
        var block = function.Blocks[0];
        if (block.Parameters.Count != arguments.Length)
            throw new InterpreterException($"@{function.Name}: entry block parameters do not match the signature.");
        for (var i = 0; i < arguments.Length; i++)
            values[block.Parameters[i]] = Truncate(arguments[i], function.ParameterTypes[i].SizeInBits);

        while (true)
        {
            var next = RunBlock(function, block, values, out var result);
            if (next is null)
                return result;
            block = next;
        }
    }

    /// <summary>
    /// Runs one block; returns the successor, or null when the function returned
    /// (the result, if any, in `result`).
    /// </summary>
    private MirBlock? RunBlock(MirFunction function, MirBlock block, Dictionary<int, ulong> values, out ulong? result)
    {
        result = null;
        foreach (var instruction in block.Instructions)
        {
            if (++_steps > _maxSteps)
                throw new InterpreterException($"Exceeded {_maxSteps} steps in @{function.Name} (runaway loop?).");

            var dialect = instruction.Opcode.Dialect;
            var code = instruction.Opcode.Code;
            var operands = instruction.Operands;

            if (dialect == CfDialect.Id)
            {
                switch ((CfOp)code)
                {
                    case CfOp.Br:
                        return Branch(function, (BlockTarget)operands[0], values);
                    case CfOp.CondBr:
                        var condition = Read(values, operands[0]) != 0;
                        return Branch(function, (BlockTarget)operands[condition ? 1 : 2], values);
                }
            }
            else if (dialect == CoreDialect.Id && (CoreOp)code == CoreOp.Return)
            {
                if (operands.Length > 0)
                    result = Read(values, operands[0]);
                return null;
            }
            else
            {
                Execute(function, instruction, values);
            }
        }

        throw new InterpreterException($"@{function.Name}: block falls off the end without a terminator.");
    }

    private static MirBlock Branch(MirFunction function, BlockTarget target, Dictionary<int, ulong> values)
    {
        var parameters = target.Block.Parameters;
        if (target.Args.Length != parameters.Count)
            throw new InterpreterException($"@{function.Name}: branch passes {target.Args.Length} argument(s) to a block taking {parameters.Count}.");

        // Read every argument before writing any parameter: a loop back-edge may
        // pass the block's own parameters in rotated order.
        var incoming = new ulong[parameters.Count];
        for (var i = 0; i < incoming.Length; i++)
            incoming[i] = Read(values, target.Args[i]);
        for (var i = 0; i < incoming.Length; i++)
            values[parameters[i]] = incoming[i];
        return target.Block;
    }

    private void Execute(MirFunction function, MirInstruction instruction, Dictionary<int, ulong> values)
    {
        var dialect = instruction.Opcode.Dialect;
        var code = instruction.Opcode.Code;
        var operands = instruction.Operands;

        if (dialect == ArithDialect.Id)
            ExecuteArith(function, (ArithOp)code, operands, values);
        else if (dialect == CastDialect.Id)
            ExecuteCast(function, (CastOp)code, operands, values);
        else if (dialect == MemDialect.Id)
            ExecuteMem(function, (MemOp)code, operands, values);
        else if (dialect == CallDialect.Id && (CallOp)code == CallOp.Func)
            ExecuteCall(function, operands, values);
        else
            throw new InterpreterException(
                $"@{function.Name}: unsupported instruction {DialectRegistry.ById(dialect).GetOpName(code)} (the interpreter runs pre-pipeline MIR).");
    }

    private void ExecuteArith(MirFunction function, ArithOp op, MirOperand[] operands, Dictionary<int, ulong> values)
    {
        var bits = DefBits(function, operands[0]);

        ulong result;
        switch (op)
        {
            case ArithOp.Constant:
                result = (ulong)((Immediate)operands[1]).Value;
                break;
            case ArithOp.AddI:
                result = Read(values, operands[1]) + Read(values, operands[2]);
                break;
            case ArithOp.SubI:
                result = Read(values, operands[1]) - Read(values, operands[2]);
                break;
            case ArithOp.MulI:
                result = Read(values, operands[1]) * Read(values, operands[2]);
                break;
            case ArithOp.And:
                result = Read(values, operands[1]) & Read(values, operands[2]);
                break;
            case ArithOp.Or:
                result = Read(values, operands[1]) | Read(values, operands[2]);
                break;
            case ArithOp.Xor:
                result = Read(values, operands[1]) ^ Read(values, operands[2]);
                break;
            case ArithOp.Not:
                result = ~Read(values, operands[1]);
                break;
            case ArithOp.Shl:
            case ArithOp.LShr:
            case ArithOp.AShr:
            {
                var value = Read(values, operands[1]);
                var amount = Read(values, operands[2]);
                if (amount >= (ulong)bits)
                    throw new InterpreterException($"@{function.Name}: shift by {amount} of a {bits}-bit value.");
                result = op switch
                {
                    ArithOp.Shl => value << (int)amount,
                    ArithOp.LShr => value >> (int)amount,
                    _ => (ulong)(SignExtend(value, bits) >> (int)amount),
                };
                break;
            }
            case ArithOp.CmpI:
            {
                var predicate = (ArithCmpPredicate)((Immediate)operands[1]).Value;
                var a = Read(values, operands[2]);
                var b = Read(values, operands[3]);
                var operandBits = UseBits(function, operands[2]);
                result = Compare(predicate, a, b, operandBits) ? 1UL : 0UL;
                break;
            }
            case ArithOp.Select:
                result = Read(values, operands[1]) != 0 ? Read(values, operands[2]) : Read(values, operands[3]);
                break;
            // The narrow forms of multi-byte add and subtract: %r, %carry = op %a, %b,
            // %carry_in. The subtract follows the 6502 polarity (carry 1 = no borrow).
            case ArithOp.AddICarry:
            {
                var sum = Read(values, operands[2]) + Read(values, operands[3]) + Read(values, operands[4]);
                Write(values, operands[1], bits < 64 ? (sum >> bits) & 1 : 0);
                Write(values, operands[0], Truncate(sum, bits));
                return;
            }
            case ArithOp.SubIBorrow:
            {
                var a = Read(values, operands[2]);
                var subtrahend = Read(values, operands[3]) + (1 - Read(values, operands[4]));
                Write(values, operands[1], a >= subtrahend ? 1UL : 0UL);
                Write(values, operands[0], Truncate(a - subtrahend, bits));
                return;
            }
            case ArithOp.UDivI:
            case ArithOp.URemI:
            {
                var a = Read(values, operands[1]);
                var b = Read(values, operands[2]);
                if (b == 0)
                    throw new InterpreterException($"@{function.Name}: division by zero.");
                result = op == ArithOp.UDivI ? a / b : a % b;
                break;
            }
            case ArithOp.SDivI:
            case ArithOp.SRemI:
            {
                var a = SignExtend(Read(values, operands[1]), bits);
                var b = SignExtend(Read(values, operands[2]), bits);
                if (b == 0)
                    throw new InterpreterException($"@{function.Name}: division by zero.");
                if (b == -1 && a == MinSigned(bits))
                    throw new InterpreterException($"@{function.Name}: signed division overflow.");
                result = (ulong)(op == ArithOp.SDivI ? a / b : a % b);
                break;
            }
            default:
                throw new InterpreterException($"@{function.Name}: unsupported arith.{op} (the interpreter runs pre-pipeline MIR).");
        }

        Write(values, operands[0], Truncate(result, bits));
    }

    private static bool Compare(ArithCmpPredicate predicate, ulong a, ulong b, int bits)
    {
        var sa = SignExtend(a, bits);
        var sb = SignExtend(b, bits);
        return predicate switch
        {
            ArithCmpPredicate.Eq => a == b,
            ArithCmpPredicate.Ne => a != b,
            ArithCmpPredicate.Slt => sa < sb,
            ArithCmpPredicate.Sgt => sa > sb,
            ArithCmpPredicate.Sle => sa <= sb,
            ArithCmpPredicate.Sge => sa >= sb,
            ArithCmpPredicate.Ult => a < b,
            ArithCmpPredicate.Ugt => a > b,
            ArithCmpPredicate.Ule => a <= b,
            ArithCmpPredicate.Uge => a >= b,
            _ => throw new InterpreterException($"Unknown compare predicate {predicate}."),
        };
    }

    private void ExecuteCast(MirFunction function, CastOp op, MirOperand[] operands, Dictionary<int, ulong> values)
    {
        var toBits = DefBits(function, operands[0]);
        var fromBits = UseBits(function, operands[1]);
        var value = Read(values, operands[1]);

        var result = op switch
        {
            CastOp.Trunc or CastOp.Zext or CastOp.PtrToInt or CastOp.IntToPtr => value,
            CastOp.Sext => (ulong)SignExtend(value, fromBits),
            _ => throw new InterpreterException($"@{function.Name}: unsupported cast.{op}."),
        };

        Write(values, operands[0], Truncate(result, toBits));
    }

    private void ExecuteMem(MirFunction function, MemOp op, MirOperand[] operands, Dictionary<int, ulong> values)
    {
        switch (op)
        {
            case MemOp.Symbol:
                Write(values, operands[0], (ulong)GetGlobalAddress(((Symbol)operands[1]).Name));
                break;

            case MemOp.PtrAdd:
                Write(values, operands[0], Truncate(Read(values, operands[1]) + Read(values, operands[2]), 16));
                break;

            case MemOp.LoadI8:
            case MemOp.LoadI16:
            case MemOp.LoadI32:
            case MemOp.LoadI64:
            case MemOp.LoadPtr:
            {
                var bytes = AccessBytes(function, operands[0]);
                Write(values, operands[0], LoadFrom(function, Read(values, operands[1]), bytes));
                break;
            }

            case MemOp.StoreI8:
            case MemOp.StoreI16:
            case MemOp.StoreI32:
            case MemOp.StoreI64:
            case MemOp.StorePtr:
            {
                var bytes = AccessBytes(function, operands[1]);
                StoreTo(function, Read(values, operands[0]), Read(values, operands[1]), bytes);
                break;
            }

            case MemOp.Fill:
            {
                var address = Read(values, operands[0]);
                var pattern = (byte)Read(values, operands[1]);
                var count = (int)((Immediate)operands[2]).Value;
                for (var i = 0; i < count; i++)
                    StoreTo(function, address + (ulong)i, pattern, 1);
                break;
            }

            default:
                throw new InterpreterException($"@{function.Name}: unsupported mem.{op} (the interpreter runs pre-pipeline MIR).");
        }
    }

    private void ExecuteCall(MirFunction function, MirOperand[] operands, Dictionary<int, ulong> values)
    {
        var defCount = 0;
        while (defCount < operands.Length && operands[defCount] is VirtualReg { IsDefinition: true })
            defCount++;

        var callee = Lookup(((Symbol)operands[defCount]).Name);
        var argStart = defCount + 1;
        if (operands.Length > argStart && operands[argStart] is Immediate convention)
            throw new InterpreterException($"@{function.Name}: call to @{callee.Name} uses calling convention {convention.Value}, which is not interpretable.");

        var arguments = new ulong[operands.Length - argStart];
        for (var i = 0; i < arguments.Length; i++)
            arguments[i] = Read(values, operands[argStart + i]);

        var result = Invoke(callee, arguments);

        if (defCount > 1)
            throw new InterpreterException($"@{function.Name}: calls with several results are not supported.");
        if (defCount == 1)
            Write(values, operands[0], result ?? throw new InterpreterException($"@{callee.Name} returned no value."));
    }

    private ulong LoadFrom(MirFunction function, ulong address, int bytes)
    {
        CheckAccess(function, address, bytes);
        ulong value = 0;
        for (var i = 0; i < bytes; i++)
            value |= (ulong)Memory[address + (ulong)i] << (8 * i);
        return value;
    }

    private void StoreTo(MirFunction function, ulong address, ulong value, int bytes)
    {
        CheckAccess(function, address, bytes);
        for (var i = 0; i < bytes; i++)
            Memory[address + (ulong)i] = (byte)(value >> (8 * i));
    }

    private void CheckAccess(MirFunction function, ulong address, int bytes)
    {
        if (address + (ulong)bytes > (ulong)Memory.Length)
            throw new InterpreterException($"@{function.Name}: access of {bytes} byte(s) at ${address:X} is outside the address space.");
    }

    /// <summary>Bytes moved by a load/store, from the type of the value operand.</summary>
    private static int AccessBytes(MirFunction function, MirOperand valueOperand) =>
        UseBits(function, valueOperand) / 8;

    private static int DefBits(MirFunction function, MirOperand operand) => UseBits(function, operand);

    private static int UseBits(MirFunction function, MirOperand operand)
    {
        var id = ((VirtualReg)operand).Id;
        return function.GetVRegAnnotation(id) is TypedVReg typed
            ? typed.Type.SizeInBits
            : throw new InterpreterException($"@{function.Name}: %{id} is not a typed vreg (the interpreter runs pre-pipeline MIR).");
    }

    private static ulong Read(Dictionary<int, ulong> values, MirOperand operand)
    {
        var id = ((VirtualReg)operand).Id;
        return values.TryGetValue(id, out var value)
            ? value
            : throw new InterpreterException($"%{id} is read before it is defined.");
    }

    private static void Write(Dictionary<int, ulong> values, MirOperand operand, ulong value) =>
        values[((VirtualReg)operand).Id] = value;

    private static ulong Truncate(ulong value, int bits) =>
        bits >= 64 ? value : value & ((1UL << bits) - 1);

    private static long SignExtend(ulong value, int bits)
    {
        if (bits >= 64)
            return (long)value;
        var shift = 64 - bits;
        return (long)(value << shift) >> shift;
    }

    private static long MinSigned(int bits) => bits >= 64 ? long.MinValue : -(1L << (bits - 1));
}
