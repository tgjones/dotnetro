using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace DotNetro.Compiler.TypeSystem;

/// <summary>
/// The generic context a signature, field or IL body is decoded under: the type arguments of
/// the enclosing type and the method arguments of the enclosing method. Equality is by value
/// (element-wise reference equality, since every <see cref="TypeDescription"/> is interned),
/// which is what lets the caches keyed on it hit.
/// </summary>
/// <remarks>
/// Decoding with a concrete context substitutes <c>!n</c> / <c>!!n</c> on the way in, so nothing
/// downstream ever sees an open generic parameter.
/// </remarks>
internal sealed class Instantiation : IEquatable<Instantiation>
{
    /// <summary>Upper bound on the nesting of type arguments; stops polymorphic recursion such as <c>F&lt;T&gt;() =&gt; F&lt;List&lt;T&gt;&gt;()</c>.</summary>
    public const int MaxDepth = 16;

    public static readonly Instantiation Empty = new([], []);

    public ImmutableArray<TypeDescription> TypeArgs { get; }

    public ImmutableArray<TypeDescription> MethodArgs { get; }

    public Instantiation(ImmutableArray<TypeDescription> typeArgs, ImmutableArray<TypeDescription> methodArgs)
    {
        TypeArgs = typeArgs;
        MethodArgs = methodArgs;
    }

    public Instantiation(ImmutableArray<TypeDescription> typeArgs)
        : this(typeArgs, [])
    {
    }

    /// <summary>
    /// Symbol-safe, unambiguous suffix naming a list of arguments (empty for none). Each argument is
    /// length-prefixed so nested instantiations cannot collide: <c>Box&lt;Pair&lt;int,byte&gt;&gt;</c> and
    /// <c>Box&lt;Pair&lt;int&gt;,byte&gt;</c> encode differently.
    /// </summary>
    public static string EncodeArguments(ImmutableArray<TypeDescription> args)
    {
        if (args.IsDefaultOrEmpty)
            return "";

        var result = "_G";
        foreach (var arg in args)
        {
            var name = arg.EncodedName;
            result += $"_{name.Length}_{name}";
        }
        return result;
    }

    public bool Equals(Instantiation? other) =>
        other is not null
        && TypeArgs.AsSpan().SequenceEqual(other.TypeArgs.AsSpan())
        && MethodArgs.AsSpan().SequenceEqual(other.MethodArgs.AsSpan());

    public override bool Equals(object? obj) => obj is Instantiation other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var arg in TypeArgs)
            hash.Add(RuntimeHelpers.GetHashCode(arg));
        hash.Add(-1);
        foreach (var arg in MethodArgs)
            hash.Add(RuntimeHelpers.GetHashCode(arg));
        return hash.ToHashCode();
    }
}
