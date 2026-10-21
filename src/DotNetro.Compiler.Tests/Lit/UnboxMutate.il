// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// unbox yields a managed pointer into the box: a write through it changes the boxed value.
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly UnboxMutate { }

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 3
    .locals init (object o)
    ldc.i4 41
    box [System.Runtime]System.Int32
    stloc.0
    ldloc.0
    unbox [System.Runtime]System.Int32
    dup
    ldind.i4
    ldc.i4.1
    add
    stind.i4
    ldloc.0
    unbox.any [System.Runtime]System.Int32
    call void [System.Console]System.Console::WriteLine(int32)
    ret
  }
}
