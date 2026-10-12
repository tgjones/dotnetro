using System.Reflection;

namespace DotNetro.Compiler;

/// <summary>
/// Supplies the bytes of assemblies the compiler links against: the core library
/// (the one that defines <c>System.Object</c>, <c>String</c>, ...), the DotNetro runtime
/// library, and anything the program references. The compiler never touches the file
/// system itself, so it can run where there is none (a WebAssembly worker).
/// </summary>
public interface IAssemblyResolver
{
    /// <summary>Returns the PE image for <paramref name="assemblyName"/>, or null if it is unknown.</summary>
    byte[]? Resolve(AssemblyName assemblyName);
}

/// <summary>
/// Resolves assemblies through the host runtime (<see cref="Assembly.Load(string)"/>).
/// This is what <c>dnrc</c> and the tests use: the host's own CoreLib and the DotNetro
/// runtime library sitting next to the compiler.
/// </summary>
public sealed class HostAssemblyResolver : IAssemblyResolver
{
    public static HostAssemblyResolver Instance { get; } = new();

    public byte[]? Resolve(AssemblyName assemblyName)
        => File.ReadAllBytes(Assembly.Load(assemblyName.FullName).Location);
}

/// <summary>
/// Resolves assemblies from in-memory images keyed by simple name (case-insensitive).
/// The version, culture and public key of the requested name are ignored.
/// </summary>
public sealed class InMemoryAssemblyResolver : IAssemblyResolver
{
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryAssemblyResolver() { }

    public InMemoryAssemblyResolver(IEnumerable<KeyValuePair<string, byte[]>> images)
    {
        foreach (var (name, image) in images)
            Add(name, image);
    }

    /// <summary>Registers an image under a simple assembly name, replacing any existing one.</summary>
    public InMemoryAssemblyResolver Add(string simpleName, byte[] image)
    {
        _images[simpleName] = image;
        return this;
    }

    public byte[]? Resolve(AssemblyName assemblyName)
        => assemblyName.Name is { } name && _images.TryGetValue(name, out var image) ? image : null;
}
