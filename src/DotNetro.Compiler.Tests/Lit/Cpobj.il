// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// cpobj of a struct, a scalar and an object reference. C# never emits it (a copy is ldobj + stobj).
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly Cpobj { }

.class public sequential ansi sealed Point extends [System.Runtime]System.ValueType
{
  .field public int32 X
  .field public int32 Y
}

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 3
    .locals init (valuetype Point source, valuetype Point destination, int32 number, int32 copy, object first, object second)

    // struct
    ldloca source
    ldc.i4.3
    stfld int32 Point::X
    ldloca source
    ldc.i4.4
    stfld int32 Point::Y
    ldloca destination
    ldloca source
    cpobj Point
    ldloca destination
    ldfld int32 Point::X
    call void [System.Console]System.Console::WriteLine(int32)
    ldloca destination
    ldfld int32 Point::Y
    call void [System.Console]System.Console::WriteLine(int32)

    // scalar
    ldc.i4 1234
    stloc number
    ldloca copy
    ldloca number
    cpobj [System.Runtime]System.Int32
    ldloc copy
    call void [System.Console]System.Console::WriteLine(int32)

    // reference: the boxed int is shared, not copied
    ldc.i4.s 42
    box [System.Runtime]System.Int32
    stloc first
    ldloca second
    ldloca first
    cpobj [System.Runtime]System.Object
    ldloc second
    unbox.any [System.Runtime]System.Int32
    call void [System.Console]System.Console::WriteLine(int32)
    ret
  }
}
