namespace System.Reflection;

// Roslyn puts this on any type that declares an indexer.
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class DefaultMemberAttribute : Attribute
{
    public DefaultMemberAttribute(string memberName)
    {
    }
}
