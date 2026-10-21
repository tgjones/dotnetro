// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// The long (two-byte index) forms of ldarg, starg and ldarga. C# only emits them past 255 arguments.
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly LongArgForms { }

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 2
    ldc.i4.5
    ldc.i4.6
    call int32 Program::Compute(int32, int32)
    call void [System.Console]System.Console::WriteLine(int32)
    ldc.i4.5
    call int32 Program::AddThroughAddress(int32)
    call void [System.Console]System.Console::WriteLine(int32)
    ret
  }

  // a = a * 20 (ldarg, starg); return a + b + 1 = 107 for (5, 6)
  .method public static int32 Compute(int32 a, int32 b) cil managed
  {
    .maxstack 3
    ldarg 0
    ldc.i4 20
    mul
    starg 0
    ldarg 0
    ldarg 1
    add
    ldc.i4.1
    add
    ret
  }

  // *(&n) += 6; return n  (ldarga)
  .method public static int32 AddThroughAddress(int32 n) cil managed
  {
    .maxstack 3
    ldarga 0
    dup
    ldind.i4
    ldc.i4.6
    add
    stind.i4
    ldarg.0
    ret
  }
}
