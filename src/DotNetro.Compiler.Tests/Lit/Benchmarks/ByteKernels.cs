// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: byte arrays and 8-bit arithmetic (CRC-8, byte checksum, fill).
var buf = new byte[16];
Bench.Fill(buf);
Console.WriteLine(Bench.Crc8(buf));
Console.WriteLine(Bench.Checksum(buf));

static class Bench
{
    public static void Fill(byte[] b)
    {
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 7 + 3);
    }

    public static byte Crc8(byte[] data)
    {
        byte crc = 0;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    public static byte Checksum(byte[] data)
    {
        byte s = 0;
        for (int i = 0; i < data.Length; i++) s += data[i];
        return s;
    }
}
