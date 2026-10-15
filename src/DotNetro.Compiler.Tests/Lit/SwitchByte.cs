// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// A byte selector (promoted to int32 on the IL stack) with locals live across the switch.
byte b = 250;
int total = 0;
for (int n = 0; n < 10; n++)
{
    switch (b)
    {
        case 250: total += 1; break;
        case 251: total += 20; break;
        case 252: total += 300; break;
        case 253: total += 4000; break;
        case 254: total += 50000; break;
        default: total += 7; break;
    }
    b++;
    Console.WriteLine(total);
}
