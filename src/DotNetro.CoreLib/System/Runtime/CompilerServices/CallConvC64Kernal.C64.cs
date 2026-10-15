namespace System.Runtime.CompilerServices;

// The calling convention of the Commodore 64 KERNAL jump table: byte arguments in
// A, X, Y (in that order) and byte results back in the same registers. Named in C#
// as `delegate* unmanaged[C64Kernal]<…>`; Roslyn emits this type as a modifier on
// the calli's return type and the compiler maps it to Irie's `c64_kernal`.
public class CallConvC64Kernal
{
}
