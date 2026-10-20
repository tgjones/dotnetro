namespace DotNetro.Compiler.TypeSystem;

internal abstract class ParameterizedType(TypeSystemContext context, TypeDescription elementType)
    : TypeDescription(context)
{
    public TypeDescription ElementType { get; } = elementType;

    public override int InstantiationDepth => ElementType.InstantiationDepth;
}
