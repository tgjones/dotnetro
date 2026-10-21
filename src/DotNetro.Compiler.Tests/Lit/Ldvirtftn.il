// RUN-dotnet: @il_compiler @file | @dotnet_runner
// RUN-emulated: @il_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @il_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// ldvirtftn resolves a virtual method against the object's runtime type, so calli through the
// result runs the override; for a non-virtual method it yields the method itself.
.assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly extern System.Console { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A) .ver 10:0:0:0 }
.assembly Ldvirtftn { }

.class public auto ansi Base extends [System.Runtime]System.Object
{
  .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
  {
    ldarg.0
    call instance void [System.Runtime]System.Object::.ctor()
    ret
  }
  .method public hidebysig newslot virtual instance int32 Get() cil managed
  {
    ldc.i4 42
    ret
  }
  .method public hidebysig instance int32 Plain() cil managed
  {
    ldc.i4 7
    ret
  }
}

.class public auto ansi Derived extends Base
{
  .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
  {
    ldarg.0
    call instance void Base::.ctor()
    ret
  }
  .method public hidebysig virtual instance int32 Get() cil managed
  {
    ldc.i4 43
    ret
  }
}

.class public abstract sealed auto ansi Program extends [System.Runtime]System.Object
{
  .method public static void Main() cil managed
  {
    .entrypoint
    .maxstack 2
    .locals init (class Base b, class Base d)
    newobj instance void Base::.ctor()
    stloc.0
    newobj instance void Derived::.ctor()
    stloc.1

    ldloc.0
    ldloc.0
    ldvirtftn instance int32 Base::Get()
    calli instance default int32()
    call void [System.Console]System.Console::WriteLine(int32)

    ldloc.1
    ldloc.1
    ldvirtftn instance int32 Base::Get()
    calli instance default int32()
    call void [System.Console]System.Console::WriteLine(int32)

    ldloc.1
    ldloc.1
    ldvirtftn instance int32 Base::Plain()
    calli instance default int32()
    call void [System.Console]System.Console::WriteLine(int32)
    ret
  }
}
