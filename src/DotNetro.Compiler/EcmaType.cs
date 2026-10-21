using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;
using DotNetro.Compiler.Util;

namespace DotNetro.Compiler;

internal sealed class EcmaType : TypeDescription
{
    private readonly Dictionary<FieldDefinitionHandle, EcmaField> _fields = [];
    private bool _builtFields;
    private int _size;
    private bool _builtBaseType;
    private EcmaType? _baseType;
    private bool _builtIsValueType;
    private bool _isValueType;
    private bool _isEnum;

    public EcmaAssembly Assembly { get; }

    public TypeDefinitionHandle TypeDefinitionHandle { get; }

    public TypeDefinition TypeDefinition { get; }

    /// <summary>
    /// The type arguments this type is instantiated with (empty for a non-generic type and for an
    /// open generic definition, which only exists as the head of an instantiation).
    /// </summary>
    public ImmutableArray<TypeDescription> TypeArguments { get; }

    /// <summary>The generic context this type's fields, base type and members are decoded under.</summary>
    public Instantiation Instantiation { get; }

    /// <summary>
    /// Number of generic parameters, counting those of enclosing types: metadata redeclares an
    /// enclosing type's parameters on each nested type (ECMA-335 II.9), so this is also the length
    /// <see cref="TypeArguments"/> must have.
    /// </summary>
    public int GenericArity { get; }

    public bool IsGenericDefinition => GenericArity > 0 && TypeArguments.IsEmpty;

    /// <summary>Namespace-qualified name; for a generic instance the arguments follow, e.g. <c>System.Nullable&lt;Int32&gt;</c>.</summary>
    public string FullName { get; }

    public override string EncodedName { get; }

    public override int InstantiationDepth { get; }

    public override int InstanceSize
    {
        get
        {
            EnsureFields();
            return _size;
        }
    }

    public override int Size => !IsValueType
        ? Context.PointerSize
        : InstanceSize;

    public override bool IsPointerLike => !IsValueType;

    public EcmaType? BaseType
    {
        get
        {
            EnsureBaseType();
            return _baseType;
        }
    }

    /// <summary>
    /// True for an <c>enum</c> type (derives from <c>System.Enum</c>). Enums are value types
    /// whose representation is that of their underlying integer type.
    /// </summary>
    public bool IsEnum
    {
        get
        {
            EnsureIsValueType();
            return _isEnum;
        }
    }

    public bool IsInterface => TypeDefinition.Attributes.HasFlag(TypeAttributes.Interface);

    public bool IsSealed => TypeDefinition.Attributes.HasFlag(TypeAttributes.Sealed);

    /// <summary>The integer type an enum is stored as: the type of its <c>value__</c> field.</summary>
    public TypeDescription EnumUnderlyingType
    {
        get
        {
            foreach (var fieldDefinitionHandle in TypeDefinition.GetFields())
            {
                var fieldDefinition = Assembly.MetadataReader.GetFieldDefinition(fieldDefinitionHandle);
                if (!fieldDefinition.Attributes.HasFlag(FieldAttributes.Static))
                    return fieldDefinition.DecodeSignature(Assembly.SignatureTypeProvider, Instantiation);
            }
            throw new InvalidOperationException($"Enum {FullName} has no value__ field.");
        }
    }

    public bool IsValueType
    {
        get
        {
            EnsureIsValueType();
            return _isValueType;
        }
    }

    /// <summary>Only <see cref="EcmaAssembly.GetType(in TypeDefinitionHandle, ImmutableArray{TypeDescription})"/> may call this: types are interned.</summary>
    internal EcmaType(EcmaAssembly assembly, TypeDefinitionHandle typeDefinitionHandle, ImmutableArray<TypeDescription> typeArguments)
        : base(assembly.Context)
    {
        Assembly = assembly;
        TypeDefinitionHandle = typeDefinitionHandle;

        TypeDefinition = Assembly.MetadataReader.GetTypeDefinition(TypeDefinitionHandle);

        TypeArguments = typeArguments;
        Instantiation = new Instantiation(typeArguments);
        GenericArity = TypeDefinition.GetGenericParameters().Count;
        if (!typeArguments.IsEmpty && typeArguments.Length != GenericArity)
            throw new InvalidOperationException(
                $"{TypeDefinition.GetFullName(assembly.MetadataReader)} takes {GenericArity} type arguments, not {typeArguments.Length}.");

        var definitionName = TypeDefinition.GetFullName(assembly.MetadataReader);
        if (typeArguments.IsEmpty)
        {
            FullName = definitionName;
            EncodedName = definitionName.Replace('.', '_').Replace('`', '_');
        }
        else
        {
            var arity = definitionName.IndexOf('`');
            var baseName = arity < 0 ? definitionName : definitionName[..arity];
            FullName = $"{baseName}<{string.Join(",", typeArguments.Select(x => x.EncodedName))}>";
            EncodedName = definitionName.Replace('.', '_').Replace('`', '_') + Instantiation.EncodeArguments(typeArguments);
            InstantiationDepth = typeArguments.Max(x => x.InstantiationDepth) + 1;
        }
    }

    /// <summary>The name of the generic definition without its arguments (equal to <see cref="FullName"/> for a non-generic type).</summary>
    public string DefinitionFullName => TypeDefinition.GetFullName(Assembly.MetadataReader);

    /// <summary>Instantiate this open generic definition with <paramref name="typeArguments"/>.</summary>
    public EcmaType Instantiate(ImmutableArray<TypeDescription> typeArguments)
    {
        if (!IsGenericDefinition)
            throw new InvalidOperationException($"{FullName} is not an open generic type definition.");

        return Assembly.GetType(TypeDefinitionHandle, typeArguments);
    }

    public EcmaMethod? GetStaticConstructor()
    {
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            if (Assembly.MetadataReader.GetString(methodDefinition.Name) == ".cctor")
            {
                return Assembly.GetMethod(methodDefinitionHandle, TypeArguments);
            }
        }

        return null;
    }

    /// <summary>
    /// Find the method definition called <paramref name="name"/> whose signature (decoded under this
    /// type's arguments, with open <c>!!n</c> as placeholders) is compatible with <paramref name="signature"/>.
    /// </summary>
    public bool TryFindMethod(string name, in MethodSignature<TypeDescription> signature, out MethodDefinitionHandle result)
    {
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            if (Assembly.MetadataReader.GetString(methodDefinition.Name) == name)
            {
                var context = Assembly.PlaceholderContext(TypeArguments, methodDefinition.GetGenericParameters().Count);
                var methodSignature = methodDefinition.DecodeSignature(Assembly.SignatureTypeProvider, context);
                if (MethodSignatureUtility.AreCompatible(signature, methodSignature))
                {
                    result = methodDefinitionHandle;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    public bool TryGetMethod(string name, in MethodSignature<TypeDescription> signature, [NotNullWhen(true)] out EcmaMethod? result)
    {
        if (TryFindMethod(name, signature, out var handle))
        {
            result = Assembly.GetMethod(handle, TypeArguments);
            return true;
        }

        result = null;
        return false;
    }

    public EcmaMethod GetMethod(string name, MethodSignature<TypeDescription> signature)
    {
        if (!TryGetMethod(name, signature, out var result))
        {
            throw new InvalidOperationException($"Could not find method {name}");
        }

        return result;
    }

    /// <summary>
    /// Resolve a method by name alone. Use only for methods known to have a single
    /// overload (e.g. runtime helpers like ManagedHeap.Alloc); throws
    /// if the name is absent or ambiguous so an unexpected overload is visible.
    /// </summary>
    public EcmaMethod GetMethod(string name)
    {
        EcmaMethod? found = null;
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);
            if (Assembly.MetadataReader.GetString(methodDefinition.Name) != name)
                continue;
            if (found != null)
                throw new InvalidOperationException($"Multiple methods named {name} on {FullName}");
            found = Assembly.GetMethod(methodDefinitionHandle, TypeArguments);
        }
        return found ?? throw new InvalidOperationException($"Could not find method {name} on {FullName}");
    }

    private void EnsureFields()
    {
        if (_builtFields)
        {
            return;
        }

        if (IsGenericDefinition)
            throw new NotSupportedException($"{FullName} is an open generic type; only instantiations have a layout.");

        // A class's fields follow those of its base class (a struct has no base to inherit from).
        var offset = !IsValueType && BaseType is { } baseType ? baseType.InstanceSize : 0;

        foreach (var fieldDefinitionHandle in TypeDefinition.GetFields())
        {
            var fieldDefinition = Assembly.MetadataReader.GetFieldDefinition(fieldDefinitionHandle);

            var fieldType = fieldDefinition.DecodeSignature(
                Assembly.SignatureTypeProvider,
                Instantiation);

            var isStaticField = fieldDefinition.Attributes.HasFlag(FieldAttributes.Static);

            _fields.Add(fieldDefinitionHandle, new EcmaField(this, fieldDefinition, fieldType, isStaticField ? 0 : offset));

            if (!isStaticField)
            {
                offset += fieldType.Size;
            }
        }

        _size = offset;

        _builtFields = true;
    }

    public EcmaField GetField(FieldDefinitionHandle handle)
    {
        EnsureFields();
        return _fields[handle];
    }

    /// <summary>The field called <paramref name="name"/> (for a member reference, which names its field rather than pointing at it).</summary>
    public EcmaField GetField(string name)
    {
        EnsureFields();
        foreach (var field in _fields.Values)
        {
            if (field.Name == name)
                return field;
        }
        throw new InvalidOperationException($"Could not find field {name} on {FullName}");
    }

    private void EnsureBaseType()
    {
        if (_builtBaseType)
        {
            return;
        }

        _baseType = !TypeDefinition.BaseType.IsNil
            ? Assembly.ResolveType(TypeDefinition.BaseType, Instantiation)
            : null;

        _builtBaseType = true;
    }

    private void EnsureIsValueType()
    {
        if (_builtIsValueType)
        {
            return;
        }

        // System.Enum itself derives from ValueType but is a reference type.
        var isEnumClass = Assembly == Context.CoreAssembly && DefinitionFullName == "System.Enum";
        _isEnum = !isEnumClass && (BaseType?.IsWellKnownType(WellKnownType.Enum) ?? false);
        _isValueType = _isEnum
            || (!isEnumClass && (BaseType?.IsWellKnownType(WellKnownType.ValueType) ?? false));

        _builtIsValueType = true;
    }
}
