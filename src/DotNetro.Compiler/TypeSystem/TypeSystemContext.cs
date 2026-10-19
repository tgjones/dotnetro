using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class TypeSystemContext(int pointerSize, IAssemblyResolver resolver) : IDisposable
{
    /// <summary>
    /// Identity: an assembly is identified by the simple name in its own metadata, so
    /// the same image reached under different names (mscorlib -&gt; System.Private.CoreLib
    /// via type forwarders, or by file path) is one EcmaAssembly and types stay unique.
    /// </summary>
    private readonly Dictionary<string, EcmaAssembly> _assembliesByIdentity = [];

    /// <summary>
    /// Cache of reference -&gt; assembly lookups, so the resolver is asked at most once per
    /// distinct reference.
    /// </summary>
    private readonly Dictionary<string, EcmaAssembly> _assembliesByReference = [];

    private readonly ConcurrentDictionary<TypeDescription, ByReferenceType> _byReferenceTypes = new();
    private readonly ConcurrentDictionary<TypeDescription, PointerType> _pointerTypes = new();
    private readonly ConcurrentDictionary<PrimitiveTypeCode, TypeDescription> _primitiveTypes = new();
    private readonly ConcurrentDictionary<TypeDescription, SZArrayType> _szArrayTypes = new();
    private readonly ConcurrentDictionary<WellKnownType, TypeDescription> _wellKnownTypes = new();
    private readonly ConcurrentDictionary<(Instantiation, int), GenericParameter> _genericMethodParameters = new();
    private readonly ConcurrentDictionary<(Instantiation, int), GenericParameter> _genericTypeParameters = new();
    private readonly ConcurrentDictionary<(TypeDescription, Instantiation), InstantiatedType> _instantiatedTypes = new();

    /// <summary>Simple name of DotNetro's own core library (see <c>src/DotNetro.CoreLib</c>).</summary>
    public const string CoreLibraryName = WellKnownAssemblies.CoreLib;

    /// <summary>
    /// Framework assembly names that user programs (compiled by Roslyn against the
    /// standard reference assemblies) and the host's facades refer to. They are all
    /// redirected to DotNetro.CoreLib, which defines every type we support under the
    /// same namespace and name; the host's own copies are never loaded. Add a name
    /// here when we fold another framework assembly's types into the core library.
    /// </summary>
    private static readonly HashSet<string> s_redirectedFrameworkAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "netstandard",
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Runtime.Extensions",
        "System.Runtime.InteropServices",
        "System.Collections",
        "System.Console",
        "System.Memory",
    };

    public EcmaAssembly CoreAssembly => ResolveAssembly(new AssemblyName(CoreLibraryName));

    /// <summary>The runtime helpers (ManagedHeap, ConsoleHelper) live in the core library.</summary>
    public EcmaAssembly RuntimeAssembly => CoreAssembly;

    public int PointerSize { get; } = pointerSize;

    public TypeDescription Boolean => GetPrimitiveType(PrimitiveTypeCode.Boolean);

    public TypeDescription Int32 => GetPrimitiveType(PrimitiveTypeCode.Int32);

    public TypeDescription IntPtr => GetPrimitiveType(PrimitiveTypeCode.IntPtr);

    public TypeDescription String => GetPrimitiveType(PrimitiveTypeCode.String);

    public TypeDescription Void => GetPrimitiveType(PrimitiveTypeCode.Void);

    public ByReferenceType GetByReferenceType(TypeDescription elementType)
    {
        return _byReferenceTypes.GetOrAdd(elementType, x => new ByReferenceType(this, x));
    }

    public InstantiatedType GetInstantiatedType(TypeDescription genericType, Instantiation instantiation)
    {
        return _instantiatedTypes.GetOrAdd((genericType, instantiation), x => new InstantiatedType(this, (EcmaType)genericType, instantiation));
    }

    public PointerType GetPointerType(TypeDescription elementType)
    {
        return _pointerTypes.GetOrAdd(elementType, x => new PointerType(this, x));
    }

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return _primitiveTypes.GetOrAdd(typeCode, x => new PrimitiveType(this, x));
    }

    public SZArrayType GetSZArrayType(TypeDescription elementType)
    {
        return _szArrayTypes.GetOrAdd(elementType, x => new SZArrayType(this, x));
    }

    public TypeDescription GetWellKnownType(WellKnownType wellKnownType)
    {
        return _wellKnownTypes.GetOrAdd(wellKnownType, x => CoreAssembly.GetType("System", wellKnownType.ToString()));
    }

    public GenericParameter GetGenericMethodParameter(Instantiation context, int index)
    {
        return _genericMethodParameters.GetOrAdd((context, index), x => new GenericParameter(this, GenericParameterKind.Method, index));
    }

    public GenericParameter GetGenericTypeParameter(Instantiation context, int index)
    {
        return _genericTypeParameters.GetOrAdd((context, index), x => new GenericParameter(this, GenericParameterKind.Type, index));
    }

    public EcmaAssembly ResolveAssembly(string assemblyPath)
        => ResolveAssembly(File.ReadAllBytes(assemblyPath));

    public EcmaAssembly ResolveAssembly(byte[] image)
    {
        var identity = EcmaAssembly.ReadAssemblyName(image);

        if (!_assembliesByIdentity.TryGetValue(identity, out var result))
        {
            result = new EcmaAssembly(this, identity, image);
            _assembliesByIdentity.Add(identity, result);
        }

        return result;
    }

    public EcmaAssembly ResolveAssembly(AssemblyName assemblyName)
    {
        assemblyName = Redirect(assemblyName);
        var fullName = assemblyName.FullName;

        if (!_assembliesByReference.TryGetValue(fullName, out var result))
        {
            var image = resolver.Resolve(assemblyName)
                ?? throw new InvalidOperationException($"Could not resolve assembly '{fullName}'.");
            result = ResolveAssembly(image);
            _assembliesByReference.Add(fullName, result);
        }

        return result;
    }

    /// <summary>
    /// Maps a framework assembly reference onto DotNetro.CoreLib before the resolver
    /// is asked, so the host's CoreLib/facades can never be loaded by accident. A
    /// framework assembly we have no mapping for is an error naming it. Everything
    /// else (the user's own assembly, third-party libraries) resolves normally.
    /// </summary>
    private static AssemblyName Redirect(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || string.Equals(name, CoreLibraryName, StringComparison.OrdinalIgnoreCase))
            return assemblyName;

        if (s_redirectedFrameworkAssemblies.Contains(name))
            return new AssemblyName(CoreLibraryName);

        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "WindowsBase", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The program references the framework assembly '{name}', which DotNetro does not support. "
                + $"Only {CoreLibraryName} is available (framework references are redirected to it).");
        }

        return assemblyName;
    }

    public void Dispose()
    {
        foreach (var assembly in _assembliesByIdentity.Values)
        {
            assembly.Dispose();
        }
    }
}
