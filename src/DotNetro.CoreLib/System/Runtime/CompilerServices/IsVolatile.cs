namespace System.Runtime.CompilerServices;

/// <summary>
/// The custom modifier Roslyn puts on the type of a `volatile` field
/// (modreq(System.Runtime.CompilerServices.IsVolatile)); the compiler resolves it
/// when it decodes the field signature.
/// </summary>
public static class IsVolatile
{
}
