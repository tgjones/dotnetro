// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// The tail. prefix is compiled as an ordinary call.
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly TailCall { }

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 1
    ldc.i4.3
    call void Program::Count(int32)
    ret
  }

  // static void Count(int n) { if (n == 0) return; Console.WriteLine(n); tail. Count(n - 1); }
  .method public static void Count(int32 n) cil managed
  {
    .maxstack 2
    ldarg.0
    brfalse.s done
    ldarg.0
    call void [System.Console]System.Console::WriteLine(int32)
    ldarg.0
    ldc.i4.1
    sub
    tail. call void Program::Count(int32)
    ret
  done:
    ret
  }
}
