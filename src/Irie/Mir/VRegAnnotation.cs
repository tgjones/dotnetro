
namespace Irie.Mir;

/// <summary>
/// A virtual register starts life as TypedVReg (pre-isel) and is overwritten
/// with ClassedVReg by instruction selection. There is never both at once.
/// </summary>
public abstract record VRegAnnotation;

/// <summary>
/// A vreg that has a type but no register class yet. <paramref name="Range"/> optionally narrows the
/// values an integer vreg can hold (see <see cref="ValueRange"/>); null means unknown.
/// </summary>
public sealed record TypedVReg(IRType Type, ValueRange? Range = null) : VRegAnnotation;

public sealed record ClassedVReg(int ClassId, string Name) : VRegAnnotation;
