using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class SignatureTypeProvider(EcmaAssembly assembly)
    : ISignatureTypeProvider<TypeDescription, Instantiation>
{
    public TypeDescription GetArrayType(TypeDescription elementType, ArrayShape shape)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetByReferenceType(TypeDescription elementType) => elementType.MakeByReferenceType();

    public TypeDescription GetFunctionPointerType(MethodSignature<TypeDescription> signature) =>
        // A function pointer is a native int; calli carries the signature itself.
        assembly.Context.GetPrimitiveType(PrimitiveTypeCode.IntPtr);

    public TypeDescription GetGenericInstantiation(TypeDescription genericType, ImmutableArray<TypeDescription> typeArguments)
    {
        if (genericType is not EcmaType { IsGenericDefinition: true } definition)
            throw new InvalidOperationException($"{genericType.EncodedName} is not an open generic type definition.");

        return definition.Instantiate(typeArguments);
    }

    public TypeDescription GetGenericMethodParameter(Instantiation genericContext, int index)
    {
        if (index >= genericContext.MethodArgs.Length)
            throw new NotSupportedException(
                $"Generic method parameter !!{index} used outside an instantiation (open generic methods are never compiled).");

        return genericContext.MethodArgs[index];
    }

    public TypeDescription GetGenericTypeParameter(Instantiation genericContext, int index)
    {
        if (index >= genericContext.TypeArgs.Length)
            throw new NotSupportedException(
                $"Generic type parameter !{index} used outside an instantiation (open generic types are never compiled).");

        return genericContext.TypeArgs[index];
    }

    public TypeDescription GetModifiedType(TypeDescription modifier, TypeDescription unmodifiedType, bool isRequired) =>
        // Custom modifiers (e.g. the CallConv* modopts on an unmanaged calli
        // signature) don't change the representation.
        unmodifiedType;

    /// <summary>
    /// Pinning keeps the garbage collector from moving the target; there is no GC here,
    /// so a pinned local is just a local of the underlying type.
    /// </summary>
    public TypeDescription GetPinnedType(TypeDescription elementType) => elementType;

    public TypeDescription GetPointerType(TypeDescription elementType) => elementType.MakePointerType();

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode) => assembly.Context.GetPrimitiveType(typeCode);

    public TypeDescription GetSZArrayType(TypeDescription elementType) => elementType.MakeSZArrayType();

    public TypeDescription GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        return Underlying(assembly.GetType(handle));
    }

    public TypeDescription GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        return Underlying(assembly.ResolveType(handle));
    }

    /// <summary>
    /// An enum is represented by its underlying integer type everywhere a signature
    /// names it (locals, fields, parameters, arrays); the enum type itself is only
    /// needed by `box` &amp; co, which take a type token and so never come through here.
    /// </summary>
    private static TypeDescription Underlying(EcmaType type) =>
        // An open generic definition only ever heads a generic instantiation; it is never an enum.
        !type.IsGenericDefinition && type.IsEnum ? type.EnumUnderlyingType : type;

    public TypeDescription GetTypeFromSpecification(MetadataReader reader, Instantiation genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        return assembly.MetadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}