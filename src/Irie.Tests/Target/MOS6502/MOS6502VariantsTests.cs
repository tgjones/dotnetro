using Irie.Mir;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

/// <summary>
/// The variant table is the one place a placement reads costs from and emits from,
/// so what it claims has to be what is encoded.
/// </summary>
public sealed class MOS6502VariantsTests
{
    /// <summary>The bytes of an instruction follow from how its operand is encoded.</summary>
    private static int EncodedBytes(MOS6502Op op) => MOS6502MachineCodeEmitTable.Get(op).Kind switch
    {
        EmitOperandKind.Implied => 1,
        EmitOperandKind.AbsoluteAddress => 3,
        _ => 2,
    };

    [Test]
    public async Task EveryVariantsBytesAreWhatItsInstructionsEncodeTo()
    {
        foreach (var variant in MOS6502Variants.All)
        {
            var expected = variant.Code.Sum(i => EncodedBytes(i.Op));
            await Assert.That(variant.Bytes).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(MOS6502Op.LdaImm, 2, 2)]
    [Arguments(MOS6502Op.LdaZp, 2, 3)]
    [Arguments(MOS6502Op.LdaAbs, 3, 4)]
    [Arguments(MOS6502Op.LdaIndY, 2, 5)]
    [Arguments(MOS6502Op.StaZp, 2, 3)]
    [Arguments(MOS6502Op.StaAbs, 3, 4)]
    [Arguments(MOS6502Op.StaIndY, 2, 6)]
    [Arguments(MOS6502Op.AdcImm, 2, 2)]
    [Arguments(MOS6502Op.AdcZp, 2, 3)]
    [Arguments(MOS6502Op.AdcAbs, 3, 4)]
    [Arguments(MOS6502Op.CmpZp, 2, 3)]
    [Arguments(MOS6502Op.CpxAbs, 3, 4)]
    [Arguments(MOS6502Op.AslAcc, 1, 2)]
    [Arguments(MOS6502Op.Clc, 1, 2)]
    public async Task DescribeInstruction_BytesAndCycles(MOS6502Op op, int bytes, int cycles)
    {
        var description = MOS6502Variants.DescribeInstruction(op);
        await Assert.That(description.Bytes).IsEqualTo(bytes);
        await Assert.That(description.Cycles).IsEqualTo(cycles);
    }

    [Test]
    public async Task AddWithImmediate_CostsTheBytesAndCyclesOfAdcImmediate()
    {
        var variant = MOS6502Variants.Require(MOS6502Operation.Adc, Place.A, Place.Imm);
        await Assert.That(variant.Bytes).IsEqualTo(2);
        await Assert.That(variant.Cycles).IsEqualTo(2);
    }

    [Test]
    public async Task HomeVariantsAreCheaperThanTheirAbsoluteTwins()
    {
        foreach (var operation in new[] { MOS6502Operation.Adc, MOS6502Operation.Sbc, MOS6502Operation.And, MOS6502Operation.Cmp })
        {
            var home = MOS6502Variants.Require(operation, Place.A, Place.Home);
            var absolute = MOS6502Variants.Require(operation, Place.A, Place.Abs);
            await Assert.That(absolute.Bytes).IsEqualTo(home.Bytes + 1);
            await Assert.That(absolute.Cycles).IsEqualTo(home.Cycles + 1);
        }
    }

    [Test]
    public async Task EveryVariantInstantiatesToTheOperandLayoutOfItsOps()
    {
        foreach (var variant in MOS6502Variants.All)
        {
            MirOperand memory = variant.Memory switch
            {
                Place.Imm => new Immediate(5),
                Place.Abs => new Symbol("sym"),
                _ => new PhysicalReg(MOS6502Registers.Home(0), IsDefinition: false),
            };
            foreach (var (opcode, operands) in variant.Instantiate(memory))
            {
                await Assert.That(opcode.Dialect).IsEqualTo(MOS6502Dialect.Id);
                await Assert.That(operands.Length).IsGreaterThan(0);
            }
        }
    }

    [Test]
    public async Task Find_ReturnsNullForACombinationTheTableLacks()
    {
        // Nothing adds with its right operand in X.
        await Assert.That(MOS6502Variants.Find(MOS6502Operation.Adc, Place.A, Place.X)).IsNull();
        await Assert.That(() => MOS6502Variants.Require(MOS6502Operation.Adc, Place.A, Place.X))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Compare_HasVariantsForA_X_AndY()
    {
        foreach (var register in new[] { Place.A, Place.X, Place.Y })
            await Assert.That(MOS6502Variants.Find(MOS6502Operation.Cmp, register, Place.Home)).IsNotNull();
    }
}
