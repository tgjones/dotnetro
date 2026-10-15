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

/// <summary>Names of assemblies the compiler itself knows about.</summary>
public static class WellKnownAssemblies
{
    /// <summary>
    /// DotNetro's own core library. It also holds the runtime helpers, and every framework
    /// assembly reference in a program is redirected to it.
    /// </summary>
    public const string CoreLib = "DotNetro.CoreLib";
}

/// <summary>
/// Resolves assemblies from the file system. This is what <c>dnrc</c> and the tests
/// use: the target's <c>DotNetro.CoreLib.dll</c> in <c>corelib/&lt;core library id&gt;/</c> next to the
/// compiler, and any other assembly through the host runtime
/// (<see cref="Assembly.Load(string)"/>). Framework assemblies never get here: the type
/// system redirects them to the core library first.
/// </summary>
public sealed class HostAssemblyResolver(string coreLibId) : IAssemblyResolver
{
    /// <summary>Folder, relative to the compiler, holding one core library build per target.</summary>
    public const string CoreLibFolder = "corelib";

    /// <summary>Resolver for a registered target (a clear error for an unknown id).</summary>
    public static HostAssemblyResolver ForTarget(string targetId)
        => new(TargetRegistry.Get(targetId).CoreLibId);

    public byte[]? Resolve(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, WellKnownAssemblies.CoreLib, StringComparison.OrdinalIgnoreCase))
        {
            // The core library is data for the compiler, not code to load, so it is copied
            // beside the compiler as a plain file rather than being on the app's dependency list.
            foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(HostAssemblyResolver).Assembly.Location)! })
            {
                var path = Path.Combine(directory, CoreLibFolder, coreLibId, WellKnownAssemblies.CoreLib + ".dll");
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }

            return null;
        }

        return File.ReadAllBytes(Assembly.Load(assemblyName.FullName).Location);
    }
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
