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
        return genericType.MakeInstantiatedType(new Instantiation([.. typeArguments]));
    }

    public TypeDescription GetGenericMethodParameter(Instantiation genericContext, int index)
    {
        return assembly.Context.GetGenericMethodParameter(genericContext, index);
    }

    public TypeDescription GetGenericTypeParameter(Instantiation genericContext, int index)
    {
        return assembly.Context.GetGenericTypeParameter(genericContext, index);
    }

    public TypeDescription GetModifiedType(TypeDescription modifier, TypeDescription unmodifiedType, bool isRequired) =>
        // Custom modifiers (e.g. the CallConv* modopts on an unmanaged calli
        // signature) don't change the representation.
        unmodifiedType;

    // Pinning keeps the garbage collector from moving the target; there is no GC here,
    // so a pinned local is just a local of the underlying type.
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

    // An enum is represented by its underlying integer type everywhere a signature
    // names it (locals, fields, parameters, arrays); the enum type itself is only
    // needed by `box` & co, which take a type token and so never come through here.
    private static TypeDescription Underlying(EcmaType type) =>
        type.IsEnum ? type.EnumUnderlyingType : type;

    public TypeDescription GetTypeFromSpecification(MetadataReader reader, Instantiation genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        throw new NotImplementedException();
    }
}