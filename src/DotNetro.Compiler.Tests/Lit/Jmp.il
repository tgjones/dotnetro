// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// jmp forwards the incoming arguments to another method, whose result is the caller's.
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly Jmp { }

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 2
    ldc.i4 41
    ldc.i4 100
    call int32 Program::Forward(int32, int32)
    call void [System.Console]System.Console::WriteLine(int32)
    ret
  }

  .method public static int32 Forward(int32 a, int32 b) cil managed
  {
    jmp int32 Program::Add(int32, int32)
  }

  .method public static int32 Add(int32 a, int32 b) cil managed
  {
    .maxstack 2
    ldarg.0
    ldarg.1
    add
    ret
  }
}
