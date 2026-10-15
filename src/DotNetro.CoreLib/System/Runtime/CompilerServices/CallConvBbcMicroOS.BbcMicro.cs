namespace System.Runtime.CompilerServices;

// The calling convention of the BBC Micro OS ROM entry points: byte arguments in
// A, X, Y (in that order) and byte results back in the same registers. Named in
// C# as `delegate* unmanaged[BbcMicroOS]<…>`; Roslyn emits this type as a modifier
// on the calli's return type and the compiler maps it to Irie's `bbcmicro_os`.
public class CallConvBbcMicroOS
{
}
