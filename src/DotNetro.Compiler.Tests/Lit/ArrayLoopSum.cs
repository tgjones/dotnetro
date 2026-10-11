// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = new int[5];
for (int i = 0; i < a.Length; i++)
    a[i] = i * i;
int sum = 0;
for (int i = 0; i < a.Length; i++)
    sum += a[i];
Console.WriteLine(sum);
a[2] += 10;
a[3]++;
Console.WriteLine(a[2]);
Console.WriteLine(a[3]);
