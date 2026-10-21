using Irie.Mir;

namespace Irie.Dialects.Mem;

/// <summary>
/// Address and memory ops.
/// </summary>
/// <remarks>
/// `ptr` is a distinct, opaque 16-bit type (PointerType; no pointee, the value type
/// lives on the load/store op), not an integer: no arith op takes or produces one.
///   - mem.symbol @g and mem.frame_addr N produce a ptr;
///   - mem.ptradd %p, %off ((ptr, i16) -&gt; ptr, byte offset, no scaling) is address
///     arithmetic;
///   - mem.load.{i8,i16,i32,i64}, mem.store.* and mem.fill take a ptr address;
///   - mem.load.ptr / mem.store.ptr move a pointer-valued datum;
///   - mem.load.byte_at / mem.store.byte_at are the post-legalization byte forms.
/// GenericMirVerifierPass enforces these typing rules.
/// </remarks>
public sealed class MemDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "mem";

    public static OpcodeRef OpRef(MemOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((MemOp)code) switch
    {
        MemOp.Symbol      => "symbol",
        MemOp.LoadI8      => "load.i8",
        MemOp.LoadI16     => "load.i16",
        MemOp.LoadI32     => "load.i32",
        MemOp.StoreI8     => "store.i8",
        MemOp.StoreI16    => "store.i16",
        MemOp.StoreI32    => "store.i32",
        MemOp.LoadByteAt  => "load.byte_at",
        MemOp.StoreByteAt => "store.byte_at",
        MemOp.FrameAddr   => "frame_addr",
        MemOp.Fill        => "fill",
        MemOp.LoadI64     => "load.i64",
        MemOp.StoreI64    => "store.i64",
        MemOp.PtrAdd      => "ptradd",
        MemOp.LoadPtr     => "load.ptr",
        MemOp.StorePtr    => "store.ptr",
        MemOp.Copy        => "copy",
        MemOp.Set         => "set",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown mem opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "symbol":        code = (ushort)MemOp.Symbol;      return true;
            case "load.i8":       code = (ushort)MemOp.LoadI8;      return true;
            case "load.i16":      code = (ushort)MemOp.LoadI16;     return true;
            case "load.i32":      code = (ushort)MemOp.LoadI32;     return true;
            case "store.i8":      code = (ushort)MemOp.StoreI8;     return true;
            case "store.i16":     code = (ushort)MemOp.StoreI16;    return true;
            case "store.i32":     code = (ushort)MemOp.StoreI32;    return true;
            case "load.byte_at":  code = (ushort)MemOp.LoadByteAt;  return true;
            case "store.byte_at": code = (ushort)MemOp.StoreByteAt; return true;
            case "frame_addr":    code = (ushort)MemOp.FrameAddr;   return true;
            case "fill":          code = (ushort)MemOp.Fill;        return true;
            case "load.i64":      code = (ushort)MemOp.LoadI64;     return true;
            case "store.i64":     code = (ushort)MemOp.StoreI64;    return true;
            case "ptradd":        code = (ushort)MemOp.PtrAdd;      return true;
            case "load.ptr":      code = (ushort)MemOp.LoadPtr;     return true;
            case "store.ptr":     code = (ushort)MemOp.StorePtr;    return true;
            case "copy":          code = (ushort)MemOp.Copy;        return true;
            case "set":           code = (ushort)MemOp.Set;         return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        ((MemOp)code) switch
        {
            // mem.store.* has no def operand; point the legalizer at the
            // value operand so it can query the value's IRType. For the
            // wide forms (use[1] = value), and for the byte form
            // (use[2] = value after use[0]=addr, use[1]=offset).
            MemOp.StoreI8     => StoreI8Info,
            MemOp.StoreI16    => StoreI8Info,
            MemOp.StoreI32    => StoreI8Info,
            MemOp.StoreI64    => StoreI8Info,
            MemOp.StorePtr    => StoreI8Info,
            MemOp.StoreByteAt => StoreByteAtInfo,
            // mem.fill's pattern byte (i8) is the legality-driving operand;
            // the count is an Immediate that the Custom legalizer unrolls.
            MemOp.Fill        => FillInfo,
            // mem.copy's byte count (i16) is the legality-driving operand.
            MemOp.Copy        => CopyInfo,
            MemOp.Set         => CopyInfo,
            _ => DialectInstructionInfo.Empty,
        };

    private static readonly DialectInstructionInfo StoreI8Info     = new(TypeOperandIndex: 1);
    private static readonly DialectInstructionInfo StoreByteAtInfo = new(TypeOperandIndex: 2);
    private static readonly DialectInstructionInfo FillInfo        = new(TypeOperandIndex: 1);
    private static readonly DialectInstructionInfo CopyInfo        = new(TypeOperandIndex: 2);

    /// <summary>
    /// mem.symbol / mem.frame_addr are pure (they return a constant pointer
    /// value). Loads, stores, and fills observe or modify memory, so they have
    /// side effects.
    /// </summary>
    public override bool IsSideEffectFree(ushort code) => ((MemOp)code) switch
    {
        MemOp.Symbol    => true,
        MemOp.FrameAddr => true,
        MemOp.PtrAdd    => true,
        _ => false,
    };

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    /// <summary>
    /// Legal inline immediates: structural attributes (offsets / counts), not
    /// value literals. mem.fill's count (use 2), mem.load.byte_at's offset (use 1),
    /// mem.store.byte_at's offset (use 1). mem.frame_addr's slot index (use 0) is
    /// also a structural attribute, not a value literal.
    /// </summary>
    public override bool IsLegalImmediateOperand(ushort code, int useIndex) =>
        (MemOp)code switch
        {
            MemOp.Fill        => useIndex == 2,
            MemOp.LoadByteAt  => useIndex == 1,
            MemOp.StoreByteAt => useIndex == 1,
            MemOp.FrameAddr   => useIndex == 0,
            _ => false,
        };

    internal override void OnRegistered(DialectId id) => Id = id;
}
