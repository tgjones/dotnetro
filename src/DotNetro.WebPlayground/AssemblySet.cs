using System.Reflection;

using DotNetro.Compiler;

namespace DotNetro.WebPlayground;

/// <summary>
/// The assemblies the playground compiles against, loaded from embedded resources
/// (see the EmbedPlaygroundAssemblies target in the csproj).
/// </summary>
internal static class AssemblySet
{
    private static readonly Dictionary<string, byte[]> s_images = Load();

    /// <summary>Reference assemblies Roslyn compiles user code against.</summary>
    public static IReadOnlyList<byte[]> RoslynReferences { get; } = [s_images["System.Runtime"], s_images["System.Console"]];

    /// <summary>
    /// Link-time assemblies for the DotNetro compiler. The <c>System.Runtime</c>
    /// *reference* assembly stands in for the core library (it defines System.Object,
    /// String, ...; the translator only needs signatures of BCL types, never BCL bodies),
    /// so no CoreLib implementation has to be shipped.
    /// </summary>
    public static IAssemblyResolver CreateResolver(TargetDescriptor target)
    {
        var resolver = new InMemoryAssemblyResolver();
        foreach (var name in target.ImplementationAssemblies)
        {
            var image = name == "System.Private.CoreLib" ? s_images["System.Runtime"] : s_images[name];
            resolver.Add(name, image);
        }

        return resolver;
    }

    private static Dictionary<string, byte[]> Load()
    {
        const string prefix = "assemblies/";
        var assembly = typeof(AssemblySet).Assembly;
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            var image = new byte[stream.Length];
            stream.ReadExactly(image);
            result[Path.GetFileNameWithoutExtension(resourceName)] = image;
        }

        return result;
    }
}
