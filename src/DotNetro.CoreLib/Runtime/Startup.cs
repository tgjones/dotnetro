namespace DotNetro.Runtime;

// The program's first act, before static constructors and Main. The compiler's
// entry function calls it; each target says what the machine needs (Startup.<Target>.cs).
internal static partial class Startup
{
    public static unsafe partial void Initialize();
}
