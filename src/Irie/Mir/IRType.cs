namespace Irie.Mir;

public abstract record IRType(int SizeInBits)
{
    public static readonly IRType Void = new VoidType();

    public static readonly IntegerType I1 = new(1);
    public static readonly IntegerType I8 = new(8);
    public static readonly IntegerType I16 = new(16);
    public static readonly IntegerType I32 = new(32);
    public static readonly IntegerType I64 = new(64);

    public static readonly IRType Pointer = I16;

    // The distinct pointer type. Not yet what `Pointer` aliases (that flips in
    // a later stage); available now so the ptr ops can be built and tested.
    public static readonly PointerType Ptr16 = new(16);

    public static readonly IRType Label = new LabelType();

    public abstract string DisplayName { get; }
}

public sealed record IntegerType(int SizeInBits)
    : IRType(SizeInBits)
{
    public override string DisplayName => $"i{SizeInBits}";
}

// An opaque address: no pointee type (the element type lives on the load/store
// op). Not an integer — no arith op accepts it; convert with cast.ptrtoint /
// cast.inttoptr. The width is a field so it can become a target property.
public sealed record PointerType(int SizeInBits)
    : IRType(SizeInBits)
{
    public override string DisplayName => "ptr";
}

public sealed record FloatType(int SizeInBits)
    : IRType(SizeInBits)
{
    public override string DisplayName => $"f{SizeInBits}";
}

public sealed record LabelType()
    : IRType(0)
{
    public override string DisplayName => "label";
}

public sealed record VoidType()
    : IRType(0)
{
    public override string DisplayName => "void";
}
