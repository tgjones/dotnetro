namespace Irie.Mir;

/// <summary>
/// Module-level named region of memory: string constants, static fields,
/// vtables, and frame slots are all instances of this single concept.
/// `Initializer` is null for zero-init (`.bss`-style) globals.
/// </summary>
public sealed record MirGlobal(
    string          SymbolName,
    IRType          Type,
    int             SizeInBytes,
    MirInitializer? Initializer)
{
    /// <summary>
    /// When non-null, the global is *pinned* to this pre-assigned absolute byte
    /// address and reserves NO output storage: its bytes live in pre-existing RAM
    /// that the target manages elsewhere, so the binary encoder records the symbol
    /// at this address but emits no bytes and does not advance the output cursor.
    /// `null` (the default) = an ordinary global, laid out into output storage
    /// after all functions. The concept is target-agnostic (a generic
    /// pinned-address reservation); a target may use it to alias a frame slot to a
    /// fixed location it owns. Settable because placement may pin a global late,
    /// after the global itself was materialised.
    /// </summary>
    public int? FixedAddress { get; set; }
}

/// <summary>Sequence of data items concatenated to form a global's initial value.</summary>
public sealed record MirInitializer(MirDataItem[] Items);

/// <summary>A single item inside a MirInitializer.</summary>
public abstract record MirDataItem;

/// <summary>Raw bytes (e.g. a string's ASCII payload).</summary>
public sealed record DataBytes(byte[] Bytes) : MirDataItem;

/// <summary>
/// A 2-byte little-endian reference to another global's address (e.g. a vtable
/// slot pointing at a method). Resolved at link time.
/// </summary>
public sealed record DataSymbolRef(string SymbolName) : MirDataItem;
