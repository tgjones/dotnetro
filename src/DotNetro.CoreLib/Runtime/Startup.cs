namespace DotNetro.Runtime;

/// <summary>
/// The program's first act, before static constructors and Main. The compiler's
/// entry function calls it; each target says what the machine needs (Startup.&lt;Target&gt;.cs).
/// </summary>
internal static partial class Startup
{
    public static unsafe partial void Initialize();
}
