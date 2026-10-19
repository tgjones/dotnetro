using Irie.Mir;
using Irie.Tools.Interpreter;

namespace Irie.Tests.Interpreter;

/// <summary>
/// The interpreter is the oracle compiled code is checked against, so these pin
/// it to hand-computed values.
/// </summary>
public sealed class MirInterpreterTests
{
    private static MirInterpreter Load(string mir) => new(MirModule.Parse(new StringReader(mir)));

    [Test]
    [Arguments(0x00FFUL, 0x0001UL, 0x0100UL)]
    [Arguments(0xFFFFUL, 0x0001UL, 0x0000UL)] // wraps at the type's width
    public async Task AddI16_WrapsAtWidth(ulong a, ulong b, ulong expected)
    {
        var interpreter = Load("""
            func @f : (i16, i16) -> i16 {
            bb0(%0 : i16, %1 : i16):
                %2 : i16 = arith.addi %0, %1
                core.return %2
            }
            """);

        await Assert.That(interpreter.Call("f", a, b)).IsEqualTo(expected);
    }

    /// <summary>The carry out of an 8-bit add with carry, and the result.</summary>
    [Test]
    [Arguments(0xFFUL, 0x01UL, 0UL, 0x00UL, 1UL)]
    [Arguments(0xFFUL, 0x00UL, 1UL, 0x00UL, 1UL)] // the carry in counts
    [Arguments(0x7FUL, 0x01UL, 0UL, 0x80UL, 0UL)]
    public async Task AddWithCarry_ProducesTheSumAndTheCarry(ulong a, ulong b, ulong carryIn, ulong sum, ulong carryOut)
    {
        var interpreter = Load("""
            func @f : (i8, i8, i1) -> i8 {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8, %4 : i1 = arith.addi_with_carry %0, %1, %2
                core.return %3
            }
            func @carry : (i8, i8, i1) -> i1 {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8, %4 : i1 = arith.addi_with_carry %0, %1, %2
                core.return %4
            }
            """);

        await Assert.That(interpreter.Call("f", a, b, carryIn)).IsEqualTo(sum);
        await Assert.That(interpreter.Call("carry", a, b, carryIn)).IsEqualTo(carryOut);
    }

    /// <summary>The borrow follows the 6502: carry 1 means no borrow.</summary>
    [Test]
    [Arguments(0x05UL, 0x03UL, 1UL, 0x02UL, 1UL)]
    [Arguments(0x05UL, 0x03UL, 0UL, 0x01UL, 1UL)] // a borrow in
    [Arguments(0x03UL, 0x05UL, 1UL, 0xFEUL, 0UL)] // a borrow out
    public async Task SubWithBorrow_FollowsTheCarryPolarityOfThe6502(ulong a, ulong b, ulong carryIn, ulong difference, ulong carryOut)
    {
        var interpreter = Load("""
            func @f : (i8, i8, i1) -> i8 {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8, %4 : i1 = arith.subi_with_borrow %0, %1, %2
                core.return %3
            }
            func @carry : (i8, i8, i1) -> i1 {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8, %4 : i1 = arith.subi_with_borrow %0, %1, %2
                core.return %4
            }
            """);

        await Assert.That(interpreter.Call("f", a, b, carryIn)).IsEqualTo(difference);
        await Assert.That(interpreter.Call("carry", a, b, carryIn)).IsEqualTo(carryOut);
    }

    [Test]
    [Arguments(0xFFFFUL, 0x0001UL, 1UL)] // -1 < 1 signed
    [Arguments(0x0001UL, 0xFFFFUL, 0UL)]
    public async Task SignedCompare_UsesTwosComplement(ulong a, ulong b, ulong expected)
    {
        var interpreter = Load("""
            func @f : (i16, i16) -> i1 {
            bb0(%0 : i16, %1 : i16):
                %2 : i1 = arith.cmpi slt, %0, %1
                core.return %2
            }
            """);

        await Assert.That(interpreter.Call("f", a, b)).IsEqualTo(expected);
    }

    [Test]
    public async Task SignExtend_ReplicatesTheSignBit()
    {
        var interpreter = Load("""
            func @f : (i8) -> i16 {
            bb0(%0 : i8):
                %1 : i16 = cast.sext %0
                core.return %1
            }
            """);

        await Assert.That(interpreter.Call("f", 0x80)).IsEqualTo(0xFF80UL);
        await Assert.That(interpreter.Call("f", 0x7F)).IsEqualTo(0x007FUL);
    }

    [Test]
    public async Task BlockArguments_AreReadBeforeAnyIsWritten()
    {
        // Rotating (a, b) = (b, a) each iteration: sequential assignment would
        // make both equal. Two iterations return the original a.
        var interpreter = Load("""
            func @f : (i8, i8) -> i8 {
            bb0(%0 : i8, %1 : i8):
                %2 : i8 = arith.constant 0
                cf.br bb1(%0, %1, %2)
            bb1(%3 : i8, %4 : i8, %5 : i8):
                %6 : i8 = arith.constant 2
                %7 : i1 = arith.cmpi ult, %5, %6
                cf.cond_br %7, bb2(), bb3()
            bb2():
                %8 : i8 = arith.constant 1
                %9 : i8 = arith.addi %5, %8
                cf.br bb1(%4, %3, %9)
            bb3():
                core.return %3
            }
            """);

        await Assert.That(interpreter.Call("f", 10, 20)).IsEqualTo(10UL);
    }

    [Test]
    public async Task Call_PassesArgumentsAndReturnsTheResult()
    {
        var interpreter = Load("""
            func @inc : (i8) -> i8 {
            bb0(%0 : i8):
                %1 : i8 = arith.constant 1
                %2 : i8 = arith.addi %0, %1
                core.return %2
            }
            func @f : (i8) -> i8 {
            bb0(%0 : i8):
                %1 : i8 = call.func @inc, %0
                %2 : i8 = call.func @inc, %1
                core.return %2
            }
            """);

        await Assert.That(interpreter.Call("f", 0xFF)).IsEqualTo(1UL);
    }

    [Test]
    public async Task DivisionByZero_Throws()
    {
        var interpreter = Load("""
            func @f : (i8, i8) -> i8 {
            bb0(%0 : i8, %1 : i8):
                %2 : i8 = arith.udivi %0, %1
                core.return %2
            }
            """);

        await Assert.That(() => interpreter.Call("f", 1, 0)).Throws<InterpreterException>();
    }

    [Test]
    public async Task RunawayLoop_Throws()
    {
        var interpreter = new MirInterpreter(MirModule.Parse(new StringReader("""
            func @f : () -> void {
            bb0():
                cf.br bb0()
            }
            """)), maxSteps: 1000);

        await Assert.That(() => interpreter.Call("f")).Throws<InterpreterException>();
    }
}
