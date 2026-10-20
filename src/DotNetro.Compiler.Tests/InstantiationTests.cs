using System.Collections.Immutable;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler.Tests;

public sealed class InstantiationTests
{
    private static TypeSystemContext CreateContext() =>
        new(pointerSize: 2, HostAssemblyResolver.ForTarget(TargetRegistry.BbcMicroId));

    private static EcmaType NullableDefinition(TypeSystemContext context) =>
        context.CoreAssembly.GetType("System", "Nullable`1");

    [Test]
    public async Task EqualInstantiationsAreEqual()
    {
        using var context = CreateContext();
        ImmutableArray<TypeDescription> ints = [context.Int32];

        var a = new Instantiation(ints, []);
        var b = new Instantiation([context.Int32], []);
        var c = new Instantiation([context.Boolean], []);

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a).IsNotEqualTo(c);
        await Assert.That(new Instantiation([], ints)).IsNotEqualTo(new Instantiation(ints, []));
    }

    [Test]
    public async Task EqualInstantiationsInternToOneType()
    {
        using var context = CreateContext();
        var definition = NullableDefinition(context);

        var first = definition.Instantiate([context.Int32]);
        var second = definition.Instantiate([context.Int32]);
        var other = definition.Instantiate([context.Boolean]);

        await Assert.That(first).IsSameReferenceAs(second);
        await Assert.That(first).IsNotSameReferenceAs(other);
        await Assert.That(first.EncodedName).IsNotEqualTo(other.EncodedName);
    }

    [Test]
    public async Task InstantiatedFieldsAreDecodedWithTheTypeArguments()
    {
        using var context = CreateContext();
        var definition = NullableDefinition(context);

        var ofInt = definition.Instantiate([context.Int32]);
        var ofBool = definition.Instantiate([context.Boolean]);

        await Assert.That(ofInt.GetField("value").Type).IsSameReferenceAs(context.Int32);
        await Assert.That(ofInt.InstanceSize).IsEqualTo(1 + 4);
        await Assert.That(ofBool.InstanceSize).IsEqualTo(1 + 1);
        await Assert.That(ofInt.IsValueType).IsTrue();
    }

    [Test]
    public async Task OpenGenericDefinitionHasNoLayout()
    {
        using var context = CreateContext();

        await Assert.That(() => NullableDefinition(context).InstanceSize).Throws<NotSupportedException>();
    }

    [Test]
    public async Task NestedInstantiationsHaveDistinctSymbolSafeNames()
    {
        using var context = CreateContext();
        var definition = NullableDefinition(context);

        var inner = definition.Instantiate([context.Int32]);
        var nested = definition.Instantiate([inner]);

        await Assert.That(nested.EncodedName).IsNotEqualTo(inner.EncodedName);
        await Assert.That(nested.EncodedName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).IsTrue();
        await Assert.That(nested.FullName).IsEqualTo("System.Nullable<System_Nullable_1_G_5_Int32>");
    }

    [Test]
    public async Task PolymorphicRecursionHitsTheDepthLimit()
    {
        using var context = CreateContext();
        var definition = NullableDefinition(context);

        TypeDescription type = context.Int32;
        await Assert.That(() =>
        {
            for (var i = 0; i < Instantiation.MaxDepth + 2; i++)
                type = definition.Instantiate([type]);
        }).Throws<NotSupportedException>();
    }
}
