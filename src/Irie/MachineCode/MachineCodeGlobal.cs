namespace Irie.MachineCode;

/// <summary>
/// Module-level named region of memory carried at the MachineCode layer.
/// Mirrors Mir.MirGlobal but drops the IRType (the encoder only needs the size
/// and the data items; the source-level type is a MIR concept).
/// </summary>
public sealed record MachineCodeGlobal(
    string SymbolName,
    int SizeInBytes,
    MachineCodeDataItem[]? Items)   // null means zero-init (bss-style).
{
    /// <summary>
    /// When non-null, the global is pinned to this pre-assigned absolute byte
    /// address and reserves no output storage: the encoder records the symbol here
    /// but emits no bytes and leaves the output cursor untouched. Mirrors
    /// MirGlobal.FixedAddress.
    /// </summary>
    public int? FixedAddress { get; init; }
}

/// <summary>
/// A single piece of a global's initial value. Items are concatenated in order
/// starting at the global's base address.
/// </summary>
public abstract record MachineCodeDataItem;

/// <summary>Raw bytes written verbatim at the current position within the global.</summary>
public sealed record MachineCodeDataBytes(byte[] Bytes) : MachineCodeDataItem;

/// <summary>
/// A 2-byte little-endian reference to the absolute address of another symbol
/// (function or global). Resolved by the binary encoder.
/// </summary>
public sealed record MachineCodeDataSymbolRef(string SymbolName) : MachineCodeDataItem;
