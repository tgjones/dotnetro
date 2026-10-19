namespace Irie.Mir.Binary;

internal static class MirBinaryFormat
{
    /// <summary>
    /// ASCII "IRI3": the trailing digit is the
    /// major format generation, not a version.
    /// </summary>
    internal static readonly byte[] Magic = [(byte)'I', (byte)'R', (byte)'I', (byte)'3'];

    internal const int FormatVersion = 1;

    internal enum TypeTag : byte
    {
        Void = 0,
        I1   = 1,
        I8   = 2,
        I16  = 3,
        I32  = 4,
        I64  = 5,
        Ptr  = 6,
    }

    internal enum OperandTag : byte
    {
        VirtualReg  = 0,
        PhysicalReg = 1,
        Immediate   = 2,
        BlockTarget = 3,
        Symbol      = 4,
    }

    internal enum VRegAnnotationTag : byte
    {
        Typed   = 0,
        Classed = 1,
        TypedRanged = 2,
    }

    internal enum DataItemTag : byte
    {
        Bytes     = 0,
        SymbolRef = 1,
    }
}
