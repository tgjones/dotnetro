namespace System;

// Roslyn requires AttributeUsageAttribute (it checks every attribute class against it).
[Flags]
public enum AttributeTargets
{
    Assembly = 1,
    Module = 2,
    Class = 4,
    Struct = 8,
    Enum = 16,
    Constructor = 32,
    Method = 64,
    Property = 128,
    Field = 256,
    Event = 512,
    Interface = 1024,
    Parameter = 2048,
    Delegate = 4096,
    ReturnValue = 8192,
    GenericParameter = 16384,
    All = 32767,
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AttributeUsageAttribute : Attribute
{
    public AttributeUsageAttribute(AttributeTargets validOn)
    {
    }

    public bool AllowMultiple { get; set; }

    public bool Inherited { get; set; }
}

[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class FlagsAttribute : Attribute
{
}
