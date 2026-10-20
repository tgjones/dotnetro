using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using DotNetro.Compiler.TypeSystem;
using DotNetro.Compiler.Util;

namespace DotNetro.Compiler;

internal sealed class EcmaAssembly : IDisposable
{
    private readonly string _name;
    private readonly Dictionary<(MethodDefinitionHandle, Instantiation), EcmaMethod> _methods = new();
    private readonly Dictionary<(string, string), EcmaType> _typesByName = new();
    private readonly Dictionary<(TypeDefinitionHandle, Instantiation), EcmaType> _types = new();

    public TypeSystemContext Context { get; }

    public PEReader PEReader { get; }

    public MetadataReader MetadataReader { get; }

    public SignatureTypeProvider SignatureTypeProvider { get; }

    public EcmaAssembly(TypeSystemContext context, string name, byte[] image)
    {
        Context = context;
        _name = name;

        // The PEReader reads straight from the caller's array (no copy), so the
        // image must not be mutated afterwards.
        PEReader = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(image));
        MetadataReader = PEReader.GetMetadataReader();

        SignatureTypeProvider = new SignatureTypeProvider(this);
    }

    /// <summary>
    /// Reads the simple assembly name (e.g. <c>System.Private.CoreLib</c>) from a PE image.
    /// This is the identity of an assembly: the same image reached through different
    /// references (such as <c>mscorlib</c> forwarding to CoreLib) must resolve to one
    /// <see cref="EcmaAssembly"/>, or type identity breaks.
    /// </summary>
    public static string ReadAssemblyName(byte[] image)
    {
        using var peReader = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(image));
        var reader = peReader.GetMetadataReader();
        return reader.GetString(reader.GetAssemblyDefinition().Name);
    }

    public void Dispose()
    {
        PEReader.Dispose();
    }

    public override int GetHashCode() => _name.GetHashCode();

    /// <summary>
    /// Resolve a method token (<c>call</c>, <c>newobj</c>, <c>ldftn</c>, ...) found in a body decoded
    /// under <paramref name="context"/>: a definition, a member reference whose parent is a type
    /// reference or a (possibly generic) type specification, or a method specification naming the
    /// method arguments of a generic method.
    /// </summary>
    public EcmaMethod ResolveMethod(Handle methodHandle, Instantiation context)
    {
        if (methodHandle.Kind == HandleKind.MethodSpecification)
        {
            var specification = MetadataReader.GetMethodSpecification((MethodSpecificationHandle)methodHandle);
            var methodArgs = specification.DecodeSignature(SignatureTypeProvider, context);
            var (declaringType, handle) = ResolveMethodDefinition(specification.Method, context);
            return declaringType.Assembly.GetMethod(handle, declaringType.TypeArguments, methodArgs);
        }

        var (type, definition) = ResolveMethodDefinition(methodHandle, context);
        return type.Assembly.GetMethod(definition, type.TypeArguments);
    }

    /// <summary>
    /// The definition a method token names and the (instantiated) type it is declared on, without
    /// instantiating the method itself, so a generic method can be completed by a method specification.
    /// </summary>
    private (EcmaType DeclaringType, MethodDefinitionHandle Handle) ResolveMethodDefinition(Handle methodHandle, Instantiation context)
    {
        switch (methodHandle.Kind)
        {
            case HandleKind.MethodDefinition:
                var definitionHandle = (MethodDefinitionHandle)methodHandle;
                var declaringDefinition = MetadataReader.GetMethodDefinition(definitionHandle).GetDeclaringType();
                return (GetType(declaringDefinition, TypeArgumentsFor(declaringDefinition, context)), definitionHandle);

            case HandleKind.MemberReference:
                var memberReference = MetadataReader.GetMemberReference((MemberReferenceHandle)methodHandle);
                var declaringType = ResolveMemberParent(memberReference.Parent, context);
                var name = MetadataReader.GetString(memberReference.Name);

                // !n in the signature names the declaring type's arguments (not the caller's), and !!n a
                // generic method's own parameters, which no caller argument may bind: compare against the
                // definition with placeholders instead.
                var signature = memberReference.DecodeMethodSignature(
                    SignatureTypeProvider,
                    PlaceholderContext(declaringType.TypeArguments, GenericParameterCount(memberReference.Signature)));

                if (!declaringType.TryFindMethod(name, signature, out var handle))
                    throw new InvalidOperationException($"Could not find method {declaringType.FullName}::{name}");

                return (declaringType, handle);

            default:
                throw new InvalidOperationException($"Unsupported method token kind {methodHandle.Kind}.");
        }
    }

    /// <summary>The declaring type named by a member reference's parent (a type reference or type specification).</summary>
    private EcmaType ResolveMemberParent(EntityHandle parent, Instantiation context) => parent.Kind switch
    {
        HandleKind.TypeReference or HandleKind.TypeSpecification or HandleKind.TypeDefinition => ResolveType(parent, context),
        _ => throw new NotSupportedException($"Unsupported member reference parent {parent.Kind}."),
    };

    /// <summary>Resolve a field token: a definition, or a member reference to a field of a (possibly generic) type.</summary>
    public EcmaField ResolveField(Handle fieldHandle, Instantiation context)
    {
        switch (fieldHandle.Kind)
        {
            case HandleKind.FieldDefinition:
                var definitionHandle = (FieldDefinitionHandle)fieldHandle;
                var declaringDefinition = MetadataReader.GetFieldDefinition(definitionHandle).GetDeclaringType();
                return GetType(declaringDefinition, TypeArgumentsFor(declaringDefinition, context)).GetField(definitionHandle);

            case HandleKind.MemberReference:
                var memberReference = MetadataReader.GetMemberReference((MemberReferenceHandle)fieldHandle);
                return ResolveMemberParent(memberReference.Parent, context).GetField(MetadataReader.GetString(memberReference.Name));

            default:
                throw new InvalidOperationException($"Unsupported field token kind {fieldHandle.Kind}.");
        }
    }

    /// <summary>
    /// A definition token inside a generic body names the enclosing generic definition's own member; it
    /// takes the body's type arguments (the first <c>arity</c> of them, as a nested type redeclares its
    /// enclosing types' parameters first). A non-generic type takes none.
    /// </summary>
    private ImmutableArray<TypeDescription> TypeArgumentsFor(TypeDefinitionHandle handle, Instantiation context)
    {
        var arity = MetadataReader.GetTypeDefinition(handle).GetGenericParameters().Count;
        if (arity == 0)
            return [];

        if (context.TypeArgs.Length < arity)
            throw new NotSupportedException(
                $"{MetadataReader.GetTypeDefinition(handle).GetFullName(MetadataReader)} is generic but is used without type arguments.");

        return context.TypeArgs.Length == arity ? context.TypeArgs : [.. context.TypeArgs.Take(arity)];
    }

    /// <summary>
    /// A context whose method arguments are <see cref="GenericParameter"/> placeholders, used to compare
    /// the signature of a generic method independently of any instantiation.
    /// </summary>
    internal Instantiation PlaceholderContext(ImmutableArray<TypeDescription> typeArgs, int genericParameterCount)
    {
        if (genericParameterCount == 0)
            return new Instantiation(typeArgs);

        var placeholders = ImmutableArray.CreateBuilder<TypeDescription>(genericParameterCount);
        for (var i = 0; i < genericParameterCount; i++)
            placeholders.Add(Context.GetGenericMethodParameter(i));
        return new Instantiation(typeArgs, placeholders.MoveToImmutable());
    }

    private int GenericParameterCount(BlobHandle signature)
    {
        var reader = MetadataReader.GetBlobReader(signature);
        var header = reader.ReadSignatureHeader();
        return header.IsGeneric ? reader.ReadCompressedInteger() : 0;
    }

    public EcmaMethod GetMethod(MethodDefinitionHandle handle) => GetMethod(handle, []);

    public EcmaMethod GetMethod(MethodDefinitionHandle handle, ImmutableArray<TypeDescription> typeArgs)
        => GetMethod(handle, typeArgs, []);

    public EcmaMethod GetMethod(
        MethodDefinitionHandle handle,
        ImmutableArray<TypeDescription> typeArgs,
        ImmutableArray<TypeDescription> methodArgs)
    {
        var key = (handle, new Instantiation(typeArgs, methodArgs));
        if (!_methods.TryGetValue(key, out var result))
        {
            if (methodArgs.Any(x => x.InstantiationDepth >= Instantiation.MaxDepth))
                throw new NotSupportedException(
                    $"Generic instantiation nests deeper than {Instantiation.MaxDepth} levels (polymorphic recursion?).");

            var methodDefinition = MetadataReader.GetMethodDefinition(handle);
            var declaringType = GetType(methodDefinition.GetDeclaringType(), typeArgs);

            _methods.Add(key, result = new EcmaMethod(declaringType, methodDefinition, methodArgs));
        }

        return result;
    }

    public EcmaType GetType(in TypeDefinitionHandle handle) => GetType(handle, []);

    public EcmaType GetType(in TypeDefinitionHandle handle, ImmutableArray<TypeDescription> typeArgs)
    {
        var key = (handle, new Instantiation(typeArgs));
        if (!_types.TryGetValue(key, out var result))
        {
            if (typeArgs.Any(x => x.InstantiationDepth >= Instantiation.MaxDepth))
                throw new NotSupportedException(
                    $"Generic instantiation nests deeper than {Instantiation.MaxDepth} levels (polymorphic recursion?).");

            _types.Add(key, result = new EcmaType(this, handle, typeArgs));
        }

        return result;
    }

    /// <summary>
    /// Resolve a type token found in a body decoded under <paramref name="context"/>. A type
    /// specification (<c>List&lt;!0&gt;</c>, <c>!0</c>, <c>T[]</c>) is decoded with the context, so a
    /// generic parameter comes back as the concrete argument.
    /// </summary>
    public EcmaType ResolveType(in EntityHandle handle, Instantiation context)
        => AsEcmaType(ResolveTypeDescription(handle, context));

    /// <summary>
    /// Like <see cref="ResolveType(in EntityHandle, Instantiation)"/> but keeps what a type specification
    /// decodes to as it is, so an array (<c>int[]</c>) stays an <see cref="SZArrayType"/>.
    /// </summary>
    public TypeDescription ResolveTypeDescription(in EntityHandle handle, Instantiation context)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = (TypeDefinitionHandle)handle;
                return GetType(definition, TypeArgumentsFor(definition, context));

            case HandleKind.TypeReference:
                return ResolveType((TypeReferenceHandle)handle);

            case HandleKind.TypeSpecification:
                return MetadataReader.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(SignatureTypeProvider, context);

            default:
                throw new NotImplementedException($"Unsupported type token kind {handle.Kind}.");
        }
    }

    /// <summary>
    /// A type specification can decode to a primitive (<c>!0</c> with T = int), which the translator's
    /// type-token operations (box, sizeof, ...) expect as the corelib struct of the same name. An array
    /// (<c>int[]</c>) is a reference to a heap object like any class, so it stands for <c>System.Array</c>.
    /// </summary>
    public EcmaType AsEcmaType(TypeDescription type) => type switch
    {
        EcmaType ecmaType => ecmaType,
        PrimitiveType primitive => Context.CoreAssembly.GetType("System", primitive.PrimitiveTypeCode.ToString()),
        SZArrayType => Context.CoreAssembly.GetType("System", "Array"),
        _ => throw new NotSupportedException($"A type token of kind {type.GetType().Name} ({type.EncodedName}) is not supported here."),
    };

    public EcmaType ResolveType(in TypeReferenceHandle typeReferenceHandle)
    {
        var typeReference = MetadataReader.GetTypeReference(typeReferenceHandle);
        switch (typeReference.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                var assemblyReference = MetadataReader.GetAssemblyReference((AssemblyReferenceHandle)typeReference.ResolutionScope);
                var otherAssembly = Context.ResolveAssembly(assemblyReference.GetAssemblyName());
                return otherAssembly.GetType(
                    MetadataReader.GetString(typeReference.Namespace),
                    MetadataReader.GetString(typeReference.Name));

            default:
                throw new InvalidOperationException();
        }
    }

    public EcmaType GetType(string @namespace, string name)
    {
        var key = (@namespace, name);

        if (!_typesByName.TryGetValue(key, out var result))
        {
            result = GetTypeImpl(@namespace, name);

            _typesByName.Add(key, result);
        }

        return result;
    }

    private EcmaType GetTypeImpl(string @namespace, string name)
    {
        foreach (var typeDefinitionHandle in MetadataReader.TypeDefinitions)
        {
            var typeDefinition = MetadataReader.GetTypeDefinition(typeDefinitionHandle);

            var typeNamespace = MetadataReader.GetString(typeDefinition.Namespace);
            var typeName = MetadataReader.GetString(typeDefinition.Name);

            if (typeNamespace == @namespace && typeName == name)
            {
                return GetType(typeDefinitionHandle);
            }
        }

        foreach (var exportedTypeHandle in MetadataReader.ExportedTypes)
        {
            var exportedType = MetadataReader.GetExportedType(exportedTypeHandle);

            var typeNamespace = MetadataReader.GetString(exportedType.Namespace);
            var typeName = MetadataReader.GetString(exportedType.Name);

            if (typeNamespace == @namespace && typeName == name)
            {
                switch (exportedType.Implementation.Kind)
                {
                    case HandleKind.AssemblyReference:
                        var assemblyReference = MetadataReader.GetAssemblyReference((AssemblyReferenceHandle)exportedType.Implementation);
                        var otherAssembly = Context.ResolveAssembly(assemblyReference.GetAssemblyName());
                        return otherAssembly.GetType(@namespace, name);

                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        throw new InvalidOperationException($"Could not find type definition {@namespace}.{name}");
    }
}
