using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaAssembly : IDisposable
{
    private readonly string _name;
    private readonly Dictionary<FieldDefinitionHandle, EcmaField> _fields = new();
    private readonly Dictionary<MethodDefinitionHandle, EcmaMethod> _methods = new();
    private readonly Dictionary<(string, string), EcmaType> _typesByName = new();
    private readonly Dictionary<TypeDefinitionHandle, EcmaType> _types = new();

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

    public EcmaMethod ResolveMethod(Handle methodHandle)
    {
        switch (methodHandle.Kind)
        {
            case HandleKind.MethodDefinition:
                return GetMethod((MethodDefinitionHandle)methodHandle);

            case HandleKind.MemberReference:
                var memberReference = MetadataReader.GetMemberReference((MemberReferenceHandle)methodHandle);
                switch (memberReference.Parent.Kind)
                {
                    case HandleKind.TypeReference:
                        var declaringType = ResolveType((TypeReferenceHandle)memberReference.Parent);
                        return declaringType.GetMethod(
                            MetadataReader.GetString(memberReference.Name),
                            memberReference.DecodeMethodSignature(SignatureTypeProvider, Instantiation.Empty));

                    default:
                        throw new InvalidOperationException();
                }

            default:
                throw new InvalidOperationException();
        }
    }

    public EcmaMethod GetMethod(MethodDefinitionHandle handle)
    {
        if (!_methods.TryGetValue(handle, out var result))
        {
            var methodDefinition = MetadataReader.GetMethodDefinition(handle);
            var declaringType = GetType(methodDefinition.GetDeclaringType());

            _methods.Add(handle, result = new EcmaMethod(declaringType, methodDefinition));
        }

        return result;
    }

    public EcmaField GetField(FieldDefinitionHandle handle)
    {
        if (!_fields.TryGetValue(handle, out var result))
        {
            var fieldDefinition = MetadataReader.GetFieldDefinition(handle);
            var declaringType = GetType(fieldDefinition.GetDeclaringType());

            _fields.Add(handle, result = declaringType.GetField(handle));
        }

        return result;
    }

    public EcmaType GetType(in TypeDefinitionHandle handle)
    {
        if (!_types.TryGetValue(handle, out var result))
        {
            _types.Add(handle, result = new EcmaType(this, handle));
        }

        return result;
    }

    public EcmaType ResolveType(in EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => GetType((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => ResolveType((TypeReferenceHandle)handle),
        _ => throw new NotImplementedException(),
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
