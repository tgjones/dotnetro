using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaMethodBody
{
    private readonly MethodBodyBlock? _methodBodyBlock;

    /// <summary>The IL of a synthesized (intrinsic) body, pinned for the BlobReader; null for a body from the assembly.</summary>
    private readonly byte[]? _syntheticIL;

    public ImmutableArray<LocalVariable> LocalVariables { get; }

    public int LocalsSize { get; }

    /// <summary>The method's `.locals init` flag, which [SkipLocalsInit] clears.</summary>
    public bool LocalVariablesInitialized => _methodBodyBlock?.LocalVariablesInitialized ?? true;

    public unsafe BlobReader GetILReader() => _syntheticIL is { } il
        ? new BlobReader((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref il[0]), il.Length)
        : _methodBodyBlock!.GetILReader();

    /// <summary>The body the compiler supplies for an intrinsic method (see <see cref="IntrinsicBodies"/>): IL with no locals.</summary>
    public EcmaMethodBody(byte[] il)
    {
        _syntheticIL = GC.AllocateArray<byte>(il.Length, pinned: true);
        il.CopyTo(_syntheticIL, 0);
        LocalVariables = [];
    }

    public EcmaMethodBody(EcmaType declaringType, EcmaMethod method)
    {
        _methodBodyBlock = declaringType.Assembly.PEReader.GetMethodBody(method.MethodDefinition.RelativeVirtualAddress);

        if (!_methodBodyBlock.LocalSignature.IsNil)
        {
            var localSignature = method.MetadataReader.GetStandaloneSignature(_methodBodyBlock.LocalSignature);
            var localTypes = localSignature.DecodeLocalSignature(declaringType.Assembly.SignatureTypeProvider, method.Instantiation);
            var localOffset = 0;
            var localsBuilder = ImmutableArray.CreateBuilder<LocalVariable>(localTypes.Length);
            for (var i = 0; i < localTypes.Length; i++)
            {
                var local = new LocalVariable(method, i, localOffset, localTypes[i]);
                localsBuilder.Add(local);
                localOffset += local.Type.Size;
            }
            LocalVariables = localsBuilder.ToImmutable();
            LocalsSize = localOffset;
        }
        else
        {
            LocalVariables = [];
        }
    }
}

internal sealed class EcmaMethod
{
    public EcmaType DeclaringType { get; }

    /// <summary>The method arguments of a generic method instance (empty for a non-generic method).</summary>
    public ImmutableArray<TypeDescription> MethodArguments { get; }

    /// <summary>The generic context the signature and body are decoded under: the declaring type's arguments plus <see cref="MethodArguments"/>.</summary>
    public Instantiation Instantiation { get; }

    public MethodDefinition MethodDefinition { get; }

    public ImmutableArray<Parameter> Parameters { get; }

    public MethodSignature<TypeDescription> MethodSignature { get; }

    public EcmaMethodBody? MethodBody { get; }

    public MetadataReader MetadataReader { get; }

    public string Name { get; }
    public string UniqueName { get; }

    public bool IsVirtual => MethodDefinition.Attributes.HasFlag(MethodAttributes.Virtual);

    public bool IsFinal => MethodDefinition.Attributes.HasFlag(MethodAttributes.Final);

    /// <summary>
    /// [MethodImpl(MethodImplOptions.InternalCall)]: the method has no IL body and
    /// is provided by the "runtime". DotNetro uses this to mark ROM-call wrappers
    /// (e.g. PutChar) whose bodies are hand-written MOS6502 MIR in runtime.irie; the
    /// translator routes calls to them straight to the matching runtime symbol
    /// instead of translating a body.
    /// </summary>
    public bool IsInternalCall =>
        MethodDefinition.ImplAttributes.HasFlag(MethodImplAttributes.InternalCall);

    /// <summary>
    /// A method the runtime implements (<c>runtime managed</c> in IL): it has no body in the assembly.
    /// The compiler declares these for a delegate type: its constructor and <c>Invoke</c>, which the
    /// translator expands where they are called.
    /// </summary>
    public bool IsRuntimeManaged =>
        (MethodDefinition.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime;

    /// <summary>
    /// [MethodImpl(MethodImplOptions.NoInlining)]: the translator marks the MIR function
    /// `noinline`, so the Irie InliningPass leaves it a callable unit of its own.
    /// </summary>
    public bool IsNoInlining =>
        MethodDefinition.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining);

    /// <summary>
    /// [MethodImpl(MethodImplOptions.AggressiveInlining)]: the translator marks the MIR function
    /// `alwaysinline`, so the InliningPass inlines every direct call to it where it can.
    /// </summary>
    public bool IsAggressiveInlining =>
        MethodDefinition.ImplAttributes.HasFlag(MethodImplAttributes.AggressiveInlining);

    /// <summary>Only <see cref="EcmaAssembly.GetMethod(MethodDefinitionHandle, ImmutableArray{TypeDescription}, ImmutableArray{TypeDescription})"/> may call this: methods are interned.</summary>
    internal EcmaMethod(EcmaType declaringType, in MethodDefinition methodDefinition, ImmutableArray<TypeDescription> methodArguments)
    {
        DeclaringType = declaringType;
        MethodArguments = methodArguments;
        Instantiation = new Instantiation(declaringType.TypeArguments, methodArguments);

        var arity = methodDefinition.GetGenericParameters().Count;
        if (arity != methodArguments.Length)
            throw new NotSupportedException(
                $"{declaringType.FullName}::{declaringType.Assembly.MetadataReader.GetString(methodDefinition.Name)} "
                + $"takes {arity} method type arguments but was given {methodArguments.Length} (open generic methods are never compiled).");

        MetadataReader = declaringType.Assembly.MetadataReader;

        MethodDefinition = methodDefinition;

        if (MethodDefinition.RelativeVirtualAddress != 0)
        {
            MethodBody = new EcmaMethodBody(declaringType, this);
        }

        MethodSignature = MethodDefinition.DecodeSignature(declaringType.Assembly.SignatureTypeProvider, Instantiation);

        var parameterOffset = 0;
        var parametersBuilder = ImmutableArray.CreateBuilder<Parameter>(MethodSignature.ParameterTypes.Length);
        var parameterIndex = 0;
        if (!MethodDefinition.Attributes.HasFlag(MethodAttributes.Static))
        {
            TypeDescription parameterType = declaringType.IsValueType
                ? declaringType.MakeByReferenceType()
                : declaringType;

            var parameter = new Parameter(parameterIndex++, parameterOffset, parameterType);
            parametersBuilder.Add(parameter);
            parameterOffset += parameter.Type.Size;
        }
        for (var i = 0; i < MethodSignature.ParameterTypes.Length; i++)
        {
            var parameter = new Parameter(parameterIndex++, parameterOffset, MethodSignature.ParameterTypes[i]);
            parametersBuilder.Add(parameter);
            parameterOffset += parameter.Type.Size;
        }
        Parameters = parametersBuilder.ToImmutable();

        Name = MetadataReader.GetString(MethodDefinition.Name);
        UniqueName = $"{MakeSafeName(DeclaringType.DefinitionFullName)}{Instantiation.EncodeArguments(DeclaringType.TypeArguments)}"
            + $"_{MakeSafeName(Name)}{Instantiation.EncodeArguments(MethodArguments)}";

        foreach (var parameterType in MethodSignature.ParameterTypes)
        {
            UniqueName += $"_{parameterType.EncodedName}";
        }

        if (MethodBody == null && !IsInternalCall && IntrinsicBodies.TryGet(declaringType, Name) is { } il)
            MethodBody = new EcmaMethodBody(il);
    }

    private static string MakeSafeName(string name)
    {
        return name
            .Replace('.', '_')
            .Replace('$', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('|', '_')
            .Replace('`', '_');
    }
}
