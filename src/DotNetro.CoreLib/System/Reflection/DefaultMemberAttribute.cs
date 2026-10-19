namespace System.Reflection;

/// <summary>Roslyn puts this on any type that declares an indexer.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class DefaultMemberAttribute : Attribute
{
    public DefaultMemberAttribute(string memberName)
    {
    }
}
