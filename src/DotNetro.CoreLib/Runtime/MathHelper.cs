namespace DotNetro.Runtime;

// Software multiply, divide and remainder: the 6502 has none of them, so Irie's
// legalizer lowers arith.muli / udivi / sdivi / uremi / sremi to calls to these
// routines by their compiler-rt names (see MOS6502LegalizerInfo.LowerLibcall; the
// same naming llvm-mos uses for its libcalls). The translator emits each under
// that stable MIR name and links it in only when the program needs it
// (IlToMirTranslator.RequireMathHelper).
//
// They are written in C# and compiled with the program, but must not use the
// operators they implement (`*`, `/`, `%` would call themselves): only shifts by
// a constant, add, subtract and compare. Division by zero is undefined (these
// return garbage, but always terminate).
internal static class MathHelper
{
    // 16-bit multiply (pointer arithmetic with a non-power-of-two element size).
    internal static nuint __mulhi3(nuint a, nuint b)
    {
        nuint x = a;
        nuint y = b;
        nuint product = 0;
        while (y != 0)
        {
            if ((y & 1) != 0)
                product += x;
            x <<= 1;
            y >>= 1;
        }
        return product;
    }

    internal static uint __mulsi3(uint a, uint b)
    {
        uint x = a;
        uint y = b;
        uint product = 0;
        while (y != 0)
        {
            if ((y & 1) != 0)
                product += x;
            x <<= 1;
            y >>= 1;
        }
        return product;
    }

    // Restoring shift-subtract division, one quotient bit per step. The dividend
    // and the quotient share one variable: each step shifts the dividend's top
    // bit into the remainder and a quotient bit into the freed bottom bit, so only
    // the dividend/quotient, remainder and divisor are live across the loop (the
    // register allocator has no spill lowering yet, so keeping the live set small
    // is what lets this compile). `carry` catches a remainder that overflowed 32
    // bits (divisor >= 2^31).
    internal static uint __udivsi3(uint n, uint d)
    {
        uint quotient = n;
        uint remainder = 0;
        byte step = 32;
        while (step != 0)
        {
            bool carry = (remainder >> 31) != 0;
            remainder = (remainder << 1) | (quotient >> 31);
            quotient <<= 1;
            if (carry || remainder >= d)
            {
                remainder -= d;
                quotient |= 1;
            }
            step--;
        }
        return quotient;
    }

    internal static uint __umodsi3(uint n, uint d)
    {
        uint rest = n;
        uint remainder = 0;
        byte step = 32;
        while (step != 0)
        {
            bool carry = (remainder >> 31) != 0;
            remainder = (remainder << 1) | (rest >> 31);
            rest <<= 1;
            if (carry || remainder >= d)
                remainder -= d;
            step--;
        }
        return remainder;
    }

    // Signed division truncates toward zero, so divide the magnitudes and fix up
    // the sign: the quotient is negative when exactly one operand is.
    internal static int __divsi3(int a, int b)
    {
        bool negative = false;
        uint x = (uint)a;
        uint y = (uint)b;
        if (a < 0)
        {
            x = (uint)-a;
            negative = true;
        }
        if (b < 0)
        {
            y = (uint)-b;
            negative = !negative;
        }
        uint quotient = __udivsi3(x, y);
        return negative ? -(int)quotient : (int)quotient;
    }

    // The remainder takes the dividend's sign.
    internal static int __modsi3(int a, int b)
    {
        uint x = (uint)a;
        uint y = (uint)b;
        if (a < 0)
            x = (uint)-a;
        if (b < 0)
            y = (uint)-b;
        uint remainder = __umodsi3(x, y);
        return a < 0 ? -(int)remainder : (int)remainder;
    }

    // 64-bit versions of the above (the C# `long` / `ulong` operators).

    internal static ulong __muldi3(ulong a, ulong b)
    {
        ulong x = a;
        ulong y = b;
        ulong product = 0;
        while (y != 0)
        {
            if ((y & 1) != 0)
                product += x;
            x <<= 1;
            y >>= 1;
        }
        return product;
    }

    internal static ulong __udivdi3(ulong n, ulong d)
    {
        ulong quotient = n;
        ulong remainder = 0;
        byte step = 64;
        while (step != 0)
        {
            bool carry = (remainder >> 63) != 0;
            remainder = (remainder << 1) | (quotient >> 63);
            quotient <<= 1;
            if (carry || remainder >= d)
            {
                remainder -= d;
                quotient |= 1;
            }
            step--;
        }
        return quotient;
    }

    internal static ulong __umoddi3(ulong n, ulong d)
    {
        ulong rest = n;
        ulong remainder = 0;
        byte step = 64;
        while (step != 0)
        {
            bool carry = (remainder >> 63) != 0;
            remainder = (remainder << 1) | (rest >> 63);
            rest <<= 1;
            if (carry || remainder >= d)
                remainder -= d;
            step--;
        }
        return remainder;
    }

    internal static long __divdi3(long a, long b)
    {
        bool negative = false;
        ulong x = (ulong)a;
        ulong y = (ulong)b;
        if (a < 0)
        {
            x = (ulong)-a;
            negative = true;
        }
        if (b < 0)
        {
            y = (ulong)-b;
            negative = !negative;
        }
        ulong quotient = __udivdi3(x, y);
        return negative ? -(long)quotient : (long)quotient;
    }

    internal static long __moddi3(long a, long b)
    {
        ulong x = (ulong)a;
        ulong y = (ulong)b;
        if (a < 0)
            x = (ulong)-a;
        if (b < 0)
            y = (ulong)-b;
        ulong remainder = __umoddi3(x, y);
        return a < 0 ? -(long)remainder : (long)remainder;
    }
}
