namespace DotNetro.Compiler.TypeSystem;

/// <summary>
/// A placeholder for an open generic method parameter (<c>!!n</c>). Bodies and fields are always
/// decoded with concrete arguments, so this only appears when comparing the signature of a generic
/// method referenced by a member reference with its definition (see <c>EcmaType.TryFindMethod</c>).
/// </summary>
internal sealed class GenericParameter(TypeSystemContext context, int index)
    : TypeDescription(context)
{
    public override int InstanceSize => throw new NotSupportedException("An open generic parameter has no size.");

    public override string EncodedName => $"MVar{Index}";

    public int Index { get; } = index;
}
