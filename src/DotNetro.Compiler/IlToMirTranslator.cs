using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using System.Text;

using DotNetro.Compiler.TypeSystem;

using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Cast;
using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Dialects.Mem;
using Irie.Mir;

using IRPointerType = Irie.Mir.PointerType;

namespace DotNetro.Compiler;

/// <summary>
/// Walks the same IL as DotNetCompiler but, instead of emitting 6502 assembly
/// text, builds an Irie MirModule via MirBuilder. The module is then handed to
/// the Irie MOS6502 pass pipeline (see CompilerDriver) to produce program bytes.
/// </summary>
/// <remarks>
/// This is the step-7/8 skeleton: only the IL ops required for Print42 and
/// AddInt32 are implemented. Unimplemented ops throw NotSupportedException so the
/// gaps are visible as the translator grows (plan step 9).
/// </remarks>
internal sealed class IlToMirTranslator : IDisposable
{
    private readonly TypeSystemContext _typeSystemContext;
    private readonly EcmaAssembly _rootAssemblyContext;

    private readonly HashSet<EcmaMethod> _visitedMethods = [];
    private readonly Queue<EcmaMethod> _methodsToVisit = new();

    /// <summary>
    /// Names of functions supplied by the hand-written runtime; the BFS walk must
    /// not try to translate (or recurse into) these — they already exist as MIR.
    /// </summary>
    private readonly HashSet<string> _runtimeFunctionNames = [];

    private readonly string _runtimeText;

    /// <summary>
    /// Module-level globals (string constants, static fields) collected during
    /// the IL walk and appended to the module once translation completes. Keyed
    /// by symbol name so the same string / static field is registered only once.
    /// </summary>
    private readonly Dictionary<string, MirGlobal> _globals = [];

    /// <summary>
    /// Reference types newobj'd by the program. Each gets a `Vtable_&lt;type&gt;`
    /// global; the object header (at obj-2) points at it.
    /// </summary>
    private readonly HashSet<EcmaType> _usedTypes = [];

    /// <summary>Cached vtable layouts (slot index → method to dispatch). See GetVtableLayout.</summary>
    private readonly Dictionary<EcmaType, List<EcmaMethod>> _vtableLayouts = [];

    /// <summary>
    /// Types whose static fields are touched (ldsfld/stsfld); their `.cctor`
    /// must run at startup so the field has its initial value (e.g. ManagedHeap's
    /// s_heapPtr = 0x1000). Static constructors discovered, in call order.
    /// </summary>
    private readonly HashSet<EcmaType> _staticFieldOwners = [];
    private readonly List<EcmaMethod> _staticConstructors = [];

    /// <summary>
    /// Lazily-resolved ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr,
    /// the runtime allocator newobj calls. Kept in C# and translated like any
    /// other method (plan open-question 3 — the translated path).
    /// </summary>
    private EcmaMethod? _allocMethod;
    private EcmaMethod? _allocArrayMethod;

    /// <summary>Pointer size is 16-bit on the 6502.</summary>
    private const int PointerSize = 2;

    public IlToMirTranslator(string rootAssemblyPath, string runtimeText, string targetId = TargetRegistry.BbcMicroId)
        : this(File.ReadAllBytes(rootAssemblyPath), HostAssemblyResolver.ForTarget(targetId), runtimeText)
    {
    }

    public IlToMirTranslator(byte[] rootAssembly, IAssemblyResolver resolver, string runtimeText)
    {
        _runtimeText = runtimeText;
        _typeSystemContext = new TypeSystemContext(PointerSize, resolver);
        _rootAssemblyContext = _typeSystemContext.ResolveAssembly(rootAssembly);
    }

    public void Dispose() => _typeSystemContext.Dispose();

    public MirModule Translate(string entryPointMethodName)
    {
        var entryPointMethod = FindMethod(entryPointMethodName)
            ?? FindMethod("<Main>$")
            ?? throw new InvalidOperationException(
                $"Could not find entry point method {entryPointMethodName}");

        // Parse the hand-written runtime first so its functions are available
        // (and so we know which names to skip during the IL walk).
        using var runtimeReader = new StringReader(_runtimeText);
        var runtimeModule = MirModule.Parse(runtimeReader);
        foreach (var function in runtimeModule.Functions)
            _runtimeFunctionNames.Add(function.Name);

        var module = new MirModule();

        // Walk every method reachable from the entry point. Translating a method
        // can mark new types used (newobj) or pull in virtual overrides (the
        // vtable's slot methods) and static constructors, so iterate to a
        // fixpoint: drain the queue, then re-seed it from the vtables / static
        // fields discovered, until nothing new is enqueued.
        EnqueueMethod(entryPointMethod);
        var startupMethod = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "Startup").GetMethod("Initialize");
        EnqueueMethod(startupMethod);

        do
        {
            while (_methodsToVisit.Count > 0)
                module.Functions.Add(TranslateMethod(_methodsToVisit.Dequeue()));

            EnqueueVtableSlotMethods();
            EnqueueStaticConstructors();
        } while (_methodsToVisit.Count > 0);

        // The entry function must lay out first (at `origin`) so the emulator's
        // `JSR $2000` lands on it. It runs the target's start-up, the static
        // constructors, calls Main, then halts — so it is built after the walk,
        // once the static-constructor list is known.
        module.Functions.Insert(0, BuildEntryFunction(entryPointMethod, startupMethod));

        // Append the runtime functions (the indirect-call trampoline, …).
        module.Functions.AddRange(runtimeModule.Functions);

        // Vtables for every newobj'd type (registered after the walk, once all
        // used types and their dispatch targets are known).
        RegisterVtableGlobals();

        // Append the string constants / static fields / vtables collected.
        module.Globals.AddRange(_globals.Values);

        return module;
    }

    internal void MarkTypeUsed(EcmaType type) => _usedTypes.Add(type);

    /// <summary>
    /// ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr. Resolved on
    /// first newobj; kept in C# and translated through the pipeline.
    /// </summary>
    internal EcmaMethod GetAllocMethod()
    {
        if (_allocMethod != null)
            return _allocMethod;

        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        var bytePointer = _typeSystemContext.GetPrimitiveType(PrimitiveTypeCode.Byte).MakePointerType();
        var voidPointer = _typeSystemContext.Void.MakePointerType();
        _allocMethod = managedHeapType.GetMethod("Alloc",
            new MethodSignature<TypeDescription>(
                new SignatureHeader(), bytePointer, 2, 0,
                [_typeSystemContext.IntPtr, voidPointer]));
        return _allocMethod;
    }

    /// <summary>
    /// ManagedHeap.AllocArray(IntPtr length, IntPtr contentSize) → IntPtr. Resolved
    /// on first newarr.
    /// </summary>
    internal EcmaMethod GetAllocArrayMethod()
    {
        if (_allocArrayMethod != null)
            return _allocArrayMethod;

        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        var bytePointer = _typeSystemContext.GetPrimitiveType(PrimitiveTypeCode.Byte).MakePointerType();
        _allocArrayMethod = managedHeapType.GetMethod("AllocArray",
            new MethodSignature<TypeDescription>(
                new SignatureHeader(), bytePointer, 2, 0,
                [_typeSystemContext.IntPtr, _typeSystemContext.IntPtr]));
        return _allocArrayMethod;
    }

    /// <summary>
    /// DotNetro.Runtime.TypeCheck.&lt;name&gt;(object, vtable) → object, the run-time half of
    /// isinst / castclass / unbox.any. Resolved on first use.
    /// </summary>
    internal EcmaMethod GetTypeCheckMethod(string name)
    {
        var type = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "TypeCheck");
        return type.GetMethod(name);
    }

    /// <summary>
    /// True if `name` matches a function supplied by the hand-written runtime
    /// (the indirect-call trampoline, …). Used to validate InternalCall routing.
    /// </summary>
    internal bool IsRuntimeFunction(string name) => _runtimeFunctionNames.Contains(name);

    /// <summary>
    /// The dispatch table for `type`: slot index → the method that index resolves
    /// to for an instance of `type`. Built base-first (so inherited slots keep
    /// their index) then extended with `type`'s own NewSlot virtuals; overrides
    /// replace the inherited slot in place.
    /// </summary>
    /// <remarks>
    /// Recursion stops at System.Object, which has no base and contributes its own
    /// virtuals (Equals/GetHashCode/ToString) as slots 0..2. Slot indices are a pure
    /// function of metadata, independent of which methods turn out to be "used", so
    /// the translator can emit the right offset at each callvirt without a whole-
    /// program fixpoint over the used set.
    /// </remarks>
    private List<EcmaMethod> GetVtableLayout(EcmaType type)
    {
        if (_vtableLayouts.TryGetValue(type, out var cached))
            return cached;

        var slots = type.BaseType == null
            ? []
            : new List<EcmaMethod>(GetVtableLayout(type.BaseType));

        foreach (var methodDefinitionHandle in type.TypeDefinition.GetMethods())
        {
            var methodDefinition = type.Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);
            if (!methodDefinition.Attributes.HasFlag(MethodAttributes.Virtual))
                continue;

            var method = type.Assembly.GetMethod(methodDefinitionHandle);
            if (methodDefinition.Attributes.HasFlag(MethodAttributes.NewSlot))
            {
                slots.Add(method);
            }
            else
            {
                var overridden = FindOverriddenMethod(method);
                var index = GetVtableLayout(overridden.DeclaringType).IndexOf(overridden);
                if (index < 0)
                    throw new NotSupportedException(
                        $"IL→MIR: override {method.UniqueName} has no vtable slot for {overridden.UniqueName}.");
                slots[index] = method;
            }
        }

        _vtableLayouts[type] = slots;
        return slots;
    }

    /// <summary>The slot index a callvirt of `callee` reads from the object's vtable.</summary>
    internal int GetVtableSlotIndex(EcmaMethod callee)
    {
        var index = GetVtableLayout(callee.DeclaringType).IndexOf(callee);
        if (index < 0)
            throw new NotSupportedException(
                $"IL→MIR: virtual method {callee.UniqueName} has no vtable slot.");
        return index;
    }

    private static EcmaMethod FindOverriddenMethod(EcmaMethod method)
    {
        var baseType = method.DeclaringType.BaseType;
        while (baseType != null)
        {
            if (baseType.TryGetMethod(method.Name, method.MethodSignature, out var overridden))
                return overridden;
            baseType = baseType.BaseType;
        }
        throw new InvalidOperationException(
            $"IL→MIR: could not find the method overridden by {method.UniqueName}.");
    }

    /// <summary>
    /// Enqueue every dispatch target reachable through a used type's vtable so the
    /// override bodies (e.g. Derived.MyMethod) are translated, not just the
    /// statically-resolved callvirt callee.
    /// </summary>
    private void EnqueueVtableSlotMethods()
    {
        foreach (var type in _usedTypes.ToArray())
            foreach (var method in GetVtableLayout(type))
                EnqueueMethod(method);
    }

    /// <summary>
    /// Enqueue the `.cctor` of each type whose static fields were touched, and
    /// record it (in discovery order) so __entry can call them before Main.
    /// </summary>
    private void EnqueueStaticConstructors()
    {
        foreach (var owner in _staticFieldOwners.ToArray())
        {
            var cctor = owner.GetStaticConstructor();
            if (cctor == null || _staticConstructors.Contains(cctor))
                continue;
            _staticConstructors.Add(cctor);
            EnqueueMethod(cctor);
        }
    }

    /// <summary>
    /// A vtable is a packed array of 2-byte little-endian pointers (plain pointers —
    /// no RTS-trick offset; the indirect-call trampoline does JMP (zp)):
    ///   word 0         the base type's vtable (0 for System.Object); a type test
    ///                  walks this chain
    ///   word 1 + slot  the dispatch targets, one per vtable slot
    /// An object's header (at obj-2) points at its vtable. Strings and arrays have
    /// no vtable (a null header).
    /// </summary>
    private const int VtableHeaderSize = PointerSize;

    /// <summary>Symbol name for a type's vtable global.</summary>
    private static string VtableName(EcmaType type) => $"Vtable_{type.EncodedName}";

    /// <summary>
    /// Types whose vtable global is needed: those instances are made of (_usedTypes), the
    /// targets of a type test, and every base of those (the chain is walked at run time).
    /// </summary>
    private readonly HashSet<EcmaType> _vtableTypes = [];

    /// <summary>Make sure `type` (and its bases) have a vtable global, e.g. as the target of isinst.</summary>
    internal void RequireVtable(EcmaType type)
    {
        for (var t = type; t != null && _vtableTypes.Add(t); t = t.BaseType)
        {
        }
    }

    private void RegisterVtableGlobals()
    {
        foreach (var type in _usedTypes)
            RequireVtable(type);

        foreach (var type in _vtableTypes)
        {
            var name = VtableName(type);
            if (_globals.ContainsKey(name))
                continue;

            var slots = GetVtableLayout(type);
            var items = new List<MirDataItem>
            {
                type.BaseType is { } baseType
                    ? new DataSymbolRef(VtableName(baseType))
                    : new DataBytes(new byte[PointerSize]),
            };

            // Only a type that has instances dispatches through its vtable; for the
            // rest (a type test target, a base class) the slots are never read, and
            // their methods were not compiled.
            var hasInstances = _usedTypes.Contains(type);
            foreach (var method in slots)
            {
                items.Add(hasInstances
                    ? new DataSymbolRef(method.UniqueName)
                    : new DataBytes(new byte[PointerSize]));
            }

            _globals[name] = new MirGlobal(
                SymbolName:  name,
                Type:        IRType.Pointer,
                SizeInBytes: VtableHeaderSize + slots.Count * PointerSize,
                Initializer: new MirInitializer([.. items]));
        }
    }

    /// <summary>
    /// Intern a string constant as a module-level MirGlobal and return its symbol
    /// name (`string{n:X4}`, numbered in order of first use; equal text shares one
    /// global, and tokens are per-assembly so they can't name it). The global is a complete string object,
    /// laid out like an array: a null vtable slot, the u16 length (in characters), one
    /// byte per character, then a NUL. A string reference points at the length, so
    /// the symbol's address plus StringObjectHeaderSize is the reference (see
    /// TranslateLdstr). A character above 0xFF cannot be stored and becomes '?'.
    /// </summary>
    private string RegisterStringGlobal(string value)
    {
        if (!_stringSymbols.TryGetValue(value, out var stringKey))
        {
            stringKey = $"string{_stringSymbols.Count:X4}";
            _stringSymbols[value] = stringKey;

            var bytes = new byte[StringObjectHeaderSize + ArrayLengthSize + value.Length + 1];
            bytes[StringObjectHeaderSize] = (byte)value.Length;
            bytes[StringObjectHeaderSize + 1] = (byte)(value.Length >> 8);
            for (var i = 0; i < value.Length; i++)
                bytes[StringObjectHeaderSize + ArrayLengthSize + i] = value[i] <= 0xFF ? (byte)value[i] : (byte)'?';

            _globals[stringKey] = new MirGlobal(
                SymbolName:  stringKey,
                Type:        IRType.Pointer,
                SizeInBytes: bytes.Length,
                Initializer: new MirInitializer([new DataBytes(bytes)]));
        }

        return stringKey;
    }

    private readonly Dictionary<string, string> _stringSymbols = [];

    /// <summary>The vtable slot below an object reference.</summary>
    private const int StringObjectHeaderSize = PointerSize;
    private const int ArrayLengthSize = 2;

    /// <summary>
    /// Register a static field as a zero-initialized (.bss-style) MirGlobal and
    /// return its symbol name. Mirrors DotNetCompiler's GetStaticFieldName:
    /// `{field.Owner.EncodedName}_{field.Name}`.
    /// </summary>
    private string RegisterStaticFieldGlobal(EcmaField field)
    {
        // Remember the owner so its static constructor runs at startup.
        _staticFieldOwners.Add(field.Owner);

        var name = $"{field.Owner.EncodedName}_{field.Name}";
        if (!_globals.ContainsKey(name))
        {
            _globals[name] = new MirGlobal(
                SymbolName:  name,
                Type:        ToIRType(field.Type),
                SizeInBytes: field.Type.Size,
                Initializer: null);
        }

        return name;
    }

    private EcmaMethod? FindMethod(string methodName)
    {
        foreach (var methodDefinitionHandle in _rootAssemblyContext.MetadataReader.MethodDefinitions)
        {
            var methodDefinition = _rootAssemblyContext.MetadataReader.GetMethodDefinition(methodDefinitionHandle);
            var name = _rootAssemblyContext.MetadataReader.GetString(methodDefinition.Name);
            if (name == methodName)
                return _rootAssemblyContext.GetMethod(methodDefinitionHandle);
        }
        return null;
    }

    private void EnqueueMethod(EcmaMethod method)
    {
        if (_runtimeFunctionNames.Contains(method.UniqueName))
            return;
        if (_visitedMethods.Add(method))
            _methodsToVisit.Enqueue(method);
    }

    /// <summary>
    /// Synthesize the program entry point:
    ///   func @__entry : () -&gt; void {
    ///     call.func @DotNetro_Runtime_Startup_Initialize()   ; the target's setup (BBC: MODE 7)
    ///     call.func @&lt;static constructors&gt;()
    ///     call.func @&lt;Main&gt;()
    ///     core.return
    ///   }
    /// </summary>
    private MirFunction BuildEntryFunction(EcmaMethod entryPointMethod, EcmaMethod startupMethod)
    {
        var function = new MirFunction("__entry", [], IRType.Void);
        var entryBlock = function.CreateBlock();
        var builder = new MirBuilder(function);
        builder.SetInsertionPointAtEnd(entryBlock);

        builder.BuildCall(startupMethod.UniqueName, []);

        // Run static constructors before Main (e.g. ManagedHeap's s_heapPtr). They are void and parameterless (.cctor).
        foreach (var cctor in _staticConstructors)
            builder.BuildCall(cctor.UniqueName, []);

        // Top-level statements compile to `<Main>$(string[] args)`. The args
        // array is unused by the programs we support, but the call's arity must
        // still match the callee, so pass a null (constant-0) pointer per param.
        var argVregs = new int[entryPointMethod.Parameters.Length];
        for (var i = 0; i < entryPointMethod.Parameters.Length; i++)
        {
            var type = ToIRType(entryPointMethod.Parameters[i].Type);
            argVregs[i] = BuildZero(builder, type);
        }

        builder.BuildCall(entryPointMethod.UniqueName, [], argVregs);

        // Nothing to return to on a retro machine: when Main returns, halt in a
        // self-branching block (`jmp *`). Targets that have somewhere to go can
        // add their own exit later.
        var haltBlock = function.CreateBlock();
        builder.BuildInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(haltBlock, []));
        builder.SetInsertionPointAtEnd(haltBlock);
        builder.BuildInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(haltBlock, []));
        return function;
    }

    private MirFunction TranslateMethod(EcmaMethod method)
    {
        var paramTypes = method.Parameters
            .Select(p => ToIRType(p.Type))
            .ToArray();
        var returnType = ToIRType(method.MethodSignature.ReturnType);

        var function = new MirFunction(method.UniqueName, paramTypes, returnType);
        var methodTranslator = new MethodTranslator(this, method, function);
        methodTranslator.Translate();
        return function;
    }

    /// <summary>
    /// Whether a CLR type is a signed integer (so a value of it sign-extends when
    /// widened). Native int / pointers are deliberately treated as unsigned:
    /// they hold addresses.
    /// </summary>
    private static bool IsSignedClr(TypeDescription type) =>
        type is PrimitiveType
        {
            PrimitiveTypeCode: PrimitiveTypeCode.SByte or PrimitiveTypeCode.Int16
                or PrimitiveTypeCode.Int32 or PrimitiveTypeCode.Int64
        };

    /// <summary>
    /// The zero value of `type`: arith.constant 0, or for a ptr the null pointer
    /// (`inttoptr (arith.constant i16 0)`, as no arith op takes a ptr).
    /// </summary>
    private static int BuildZero(MirBuilder builder, IRType type)
    {
        if (type is not IRPointerType)
            return builder.BuildConstant(type, 0);

        var zero = builder.BuildConstant(IRType.I16, 0);
        return BuildIntToPtr(builder, zero);
    }

    private static int BuildIntToPtr(MirBuilder builder, int intVreg)
    {
        var result = builder.Function.CreateVirtualRegister(IRType.Pointer);
        builder.BuildInstruction(CastDialect.OpRef(CastOp.IntToPtr),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(intVreg, IsDefinition: false));
        return result;
    }

    private static int BuildPtrToInt(MirBuilder builder, int ptrVreg)
    {
        var result = builder.Function.CreateVirtualRegister(IRType.I16);
        builder.BuildInstruction(CastDialect.OpRef(CastOp.PtrToInt),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(ptrVreg, IsDefinition: false));
        return result;
    }

    /// <summary>
    /// Whether values of a type are addresses (ptr): arrays, `T*`, `ref T` and
    /// class instances.
    /// </summary>
    private static bool IsReferenceLike(TypeDescription type) =>
        type is SZArrayType or TypeSystem.PointerType or ByReferenceType
        || type is EcmaType { IsValueType: false };

    private static IRType ToIRType(TypeDescription type)
    {
        if (type is PrimitiveType primitive)
        {
            return primitive.PrimitiveTypeCode switch
            {
                PrimitiveTypeCode.Void => IRType.Void,
                PrimitiveTypeCode.Boolean => IRType.I8,
                PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte => IRType.I8,
                PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => IRType.I32,
                PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => IRType.I64,
                PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => IRType.I16,
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => IRType.I16,
                // A managed string / object is a heap reference: a ptr (16-bit on
                // the 6502; the pointer @PlatformReadLine returns / Console.WriteLine
                // consumes; the `this` of System.Object::.ctor, base-called by
                // every user constructor).
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => IRType.Pointer,
                _ => throw new NotSupportedException(
                    $"IL→MIR: primitive type {primitive.PrimitiveTypeCode} is not supported yet."),
            };
        }

        // Reference types (including arrays), `T*` and `ref T` are pointers. A
        // reference-type EcmaType (class) — `this`, a class-typed field, a class
        // parameter/return — is likewise a heap pointer.
        if (IsReferenceLike(type))
            return IRType.Pointer;

        throw new NotSupportedException(
            $"IL→MIR: type {type} is not supported yet.");
    }

    /// <summary>
    /// Translates a single method body. One instance per method so the eval stack
    /// and local SSA-name maps are per-method.
    /// </summary>
    private sealed class MethodTranslator
    {
        private readonly IlToMirTranslator _parent;
        private readonly EcmaMethod _method;
        private readonly MirFunction _function;
        private readonly MirBuilder _builder;

        /// <summary>
        /// Mirrors the IL evaluation stack: vreg IDs (paired with their type so
        /// we can size arith ops). Runs alongside the IL semantics. Values still
        /// on the stack at a block boundary (e.g. the arms of `c ? a : b`) travel
        /// as extra block parameters; see _blockStackParams.
        /// </summary>
        private readonly Stack<StackValue> _stack = new();

        /// <summary>
        /// Block → the block parameters carrying the eval stack on entry
        /// (bottom of stack first). Created by the first edge into the block, so
        /// they follow the local parameters in the block's parameter list; every
        /// later edge must supply the same number of values (IL guarantees a
        /// consistent stack depth at a join).
        /// </summary>
        private readonly Dictionary<MirBlock, StackValue[]> _blockStackParams = [];

        /// <summary>
        /// Primitive SSA locals: local index → current vreg ID (and its type).
        /// Re-seeded to the current block's local parameter vregs at each block
        /// entry so reads observe the merged value flowing in along every edge.
        /// </summary>
        private readonly Dictionary<int, StackValue> _locals = [];

        /// <summary>
        /// IL has no basic blocks; we discover them by pre-scanning for branch
        /// targets and fall-through-after-branch points (see DiscoverBlocks).
        /// Maps an IL offset that starts a block → the MirBlock for it.
        /// </summary>
        private readonly Dictionary<int, MirBlock> _ilOffsetToBlock = [];

        /// <summary>
        /// The IL offsets that start a block, in ascending order. Used to find
        /// the next block when emitting a fall-through terminator.
        /// </summary>
        private int[] _blockStartOffsets = [];

        /// <summary>
        /// Per-local IRType (local index → type), used to size the per-block
        /// local parameters and the entry zero-init constants. Only meaningful
        /// for SSA (non-FrameSlot) locals; FrameSlot locals carry a placeholder.
        /// </summary>
        private IRType[] _localTypes = [];

        /// <summary>
        /// Per-local CLR type and FrameSlot classification. A local is FrameSlot-
        /// bound (lives in a .bss-style MirGlobal rather than an SSA vreg) when it
        /// is a value-type struct or is address-taken via ldloca. Its FrameSlot's
        /// Index equals the local index; ldloc/stloc/ldloca on it go through
        /// memory (mem.frame_addr + mem.load/store, or a struct byte copy).
        /// </summary>
        private TypeDescription[] _localClrTypes = [];
        private bool[] _localIsFrameSlot = [];

        /// <summary>
        /// Non-entry blocks carry one parameter vreg per local (in local-index
        /// order) so the loop variable flows across edges as a block argument
        /// (MIR's PHI substitute). Maps a block → its local parameter vregs.
        /// </summary>
        private readonly Dictionary<MirBlock, int[]> _blockLocalParams = [];

        public MethodTranslator(IlToMirTranslator parent, EcmaMethod method, MirFunction function)
        {
            _parent = parent;
            _method = method;
            _function = function;
            _builder = new MirBuilder(function);
        }

        public void Translate()
        {
            var locals = _method.MethodBody?.LocalVariables ?? [];

            // A parameter that is assigned (`starg`) is no longer a single SSA value,
            // so it is shadowed by an extra local appended after the declared ones:
            // seeded from the parameter vreg at entry, then read and written like any
            // local (so it threads across blocks as a block parameter).
            _argLocal = new int[_method.Parameters.Length];
            Array.Fill(_argLocal, -1);
            var localCount = locals.Length;
            foreach (var arg in ScanStargParameters().Order())
            {
                if (arg >= _argLocal.Length || _method.Parameters[arg].Type is EcmaType { IsValueType: true })
                    throw new NotSupportedException(
                        $"IL→MIR: starg {arg} is not supported for this parameter (method {_method.UniqueName}).");
                _argLocal[arg] = localCount++;
            }

            _localTypes = new IRType[localCount];
            _localClrTypes = new TypeDescription[localCount];
            _localIsFrameSlot = new bool[localCount];
            for (var i = 0; i < _argLocal.Length; i++)
            {
                if (_argLocal[i] < 0) continue;
                _localClrTypes[_argLocal[i]] = _method.Parameters[i].Type;
                _localTypes[_argLocal[i]] = ToIRType(_method.Parameters[i].Type);
            }

            // Address-taken locals (those with an `ldloca`) and value-type
            // (struct) locals are FrameSlot-bound; everything else is a pure SSA
            // vreg. Decide upfront so ldloc/stloc/ldarg know which path to take.
            // Cross-call values flow as ordinary SSA vregs and reach the RA as
            // live-across-call values: the RA places them in function-local
            // zero-page homes (not clobbered by a call: program layout keeps
            // them disjoint from every callee's homes).
            var addressTaken = ScanAddressTakenLocals();
            for (var i = 0; i < locals.Length; i++)
            {
                var clr = locals[i].Type;
                _localClrTypes[i] = clr;

                var isStruct = clr is EcmaType { IsValueType: true };
                if (isStruct || addressTaken.Contains(i))
                {
                    _localIsFrameSlot[i] = true;
                    // A struct doesn't fit a single primitive IRType; size the
                    // slot in bytes via a wide IntegerType (FrameLoweringPass
                    // only reads SizeInBits/8). Address-taken primitives keep
                    // their natural width.
                    var slotType = isStruct
                        ? new IntegerType(clr.InstanceSize * 8)
                        : ToIRType(clr);
                    _function.FrameSlots.Add(new FrameSlot(
                        Index:      i,
                        Type:       slotType,
                        SymbolName: $"{_function.Name}_local{i}"));
                    _localTypes[i] = IRType.I16; // placeholder; unused for slots
                }
                else
                {
                    _localTypes[i] = ToIRType(clr);
                }
            }

            DiscoverBlocks();

            var entryBlock = _ilOffsetToBlock[0];

            // Parameters become entry-block parameter vregs (lowered by
            // AbiLoweringPass). Record them so ldarg.N can read them.
            var paramVregs = new int[_method.Parameters.Length];
            for (var i = 0; i < _method.Parameters.Length; i++)
            {
                var type = ToIRType(_method.Parameters[i].Type);
                var vreg = _function.CreateVirtualRegister(type);
                entryBlock.Parameters.Add(vreg);
                paramVregs[i] = vreg;
            }
            _parameterVregs = paramVregs;
            _builder.SetInsertionPointAtEnd(entryBlock);

            // C# locals are zero-initialised (`.locals init`). Seed every local
            // with an explicit `arith.constant 0` of its type at function entry
            // so each local always has a definite SSA value. This makes the
            // block-parameter wiring total: every non-entry block can take a
            // parameter for every local, and every edge can supply a value.
            for (var i = 0; i < _localTypes.Length; i++)
            {
                if (i >= locals.Length)
                {
                    // Shadow local of an assigned parameter: starts as the argument.
                    var arg = Array.IndexOf(_argLocal, i);
                    _locals[i] = new StackValue(paramVregs[arg], _localTypes[i]);
                    continue;
                }
                // FrameSlot locals live in memory (a zero-init .bss global), not
                // an SSA vreg — they are seeded by the program's own initobj /
                // stores, so skip the SSA zero-init for them.
                if (_localIsFrameSlot[i]) continue;
                var zero = BuildZero(_builder, _localTypes[i]);
                _locals[i] = new StackValue(zero, _localTypes[i]);
            }

            var ilReader = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            _currentBlock = entryBlock;
            _ilBlock = entryBlock;

            while (ilReader.RemainingBytes > 0)
            {
                var instructionOffset = ilReader.Offset;

                // Entering a new basic block (a discovered leader, but not the
                // entry which we already opened). Close the previous block with
                // a fall-through branch if it has no terminator, then switch.
                if (instructionOffset != 0
                    && _ilOffsetToBlock.TryGetValue(instructionOffset, out var nextBlock))
                {
                    if (!BlockHasTerminator(_currentBlock))
                        EmitBranchTo(_currentBlock, nextBlock);

                    EnterBlock(nextBlock);
                    _currentBlock = nextBlock;
                    _ilBlock = nextBlock;
                }

                var opCode = ReadOpCode(ref ilReader);
                switch (opCode)
                {
                    case ILOpCode.Nop:
                        break;

                    // volatile. prefix: the following load/store must not be
                    // elided, merged or reordered. Nothing in Irie does any of
                    // those to a mem.load / mem.store (they are not side-effect
                    // free, and no pass CSEs, forwards, schedules or hoists
                    // memory ops), so every access is already volatile and the
                    // prefix needs no translation. The same holds for the
                    // single-threaded target, where it only matters for hardware
                    // registers and interrupt handlers. A future memory
                    // optimisation must honour it (carry a flag on the op).
                    case ILOpCode.Volatile:
                        break;

                    // unaligned. <alignment> prefix: the following access may be
                    // misaligned. The 6502 has no alignment requirements (every
                    // access is byte-wise), so there is nothing to do but skip
                    // the 1-byte alignment operand.
                    case ILOpCode.Unaligned:
                        ilReader.ReadByte();
                        break;

                    // readonly. prefix on ldelema: the result is only read, so the
                    // array-covariance type check may be skipped. We emit no
                    // covariance check for ldelema, so it is a no-op.
                    case ILOpCode.Readonly:
                        break;

                    case ILOpCode.Ldnull:
                        _stack.Push(new StackValue(BuildZero(_builder, IRType.Pointer), IRType.Pointer));
                        break;

                    case ILOpCode.Ldc_i4_0: PushConstant(0); break;
                    case ILOpCode.Ldc_i4_1: PushConstant(1); break;
                    case ILOpCode.Ldc_i4_2: PushConstant(2); break;
                    case ILOpCode.Ldc_i4_3: PushConstant(3); break;
                    case ILOpCode.Ldc_i4_4: PushConstant(4); break;
                    case ILOpCode.Ldc_i4_5: PushConstant(5); break;
                    case ILOpCode.Ldc_i4_6: PushConstant(6); break;
                    case ILOpCode.Ldc_i4_7: PushConstant(7); break;
                    case ILOpCode.Ldc_i4_8: PushConstant(8); break;
                    case ILOpCode.Ldc_i4_m1: PushConstant(-1); break;
                    case ILOpCode.Ldc_i4_s: PushConstant(ilReader.ReadSByte()); break;
                    case ILOpCode.Ldc_i4: PushConstant(ilReader.ReadInt32()); break;
                    case ILOpCode.Ldc_i8: PushConstant64(ilReader.ReadInt64()); break;

                    case ILOpCode.Add: TranslateIntegerBinary(ArithOp.AddI); break;
                    case ILOpCode.Sub: TranslateIntegerBinary(ArithOp.SubI); break;
                    case ILOpCode.Mul: TranslateIntegerBinary(ArithOp.MulI); break;
                    case ILOpCode.Div: TranslateIntegerBinary(ArithOp.SDivI); break;
                    case ILOpCode.Div_un: TranslateIntegerBinary(ArithOp.UDivI); break;
                    case ILOpCode.Rem: TranslateIntegerBinary(ArithOp.SRemI); break;
                    case ILOpCode.Rem_un: TranslateIntegerBinary(ArithOp.URemI); break;
                    case ILOpCode.And: TranslateBitwise(ArithOp.And); break;
                    case ILOpCode.Or: TranslateBitwise(ArithOp.Or); break;
                    case ILOpCode.Xor: TranslateBitwise(ArithOp.Xor); break;
                    case ILOpCode.Not: TranslateNot(); break;
                    case ILOpCode.Neg: TranslateNeg(); break;
                    case ILOpCode.Shl: TranslateShift(ArithOp.Shl); break;
                    case ILOpCode.Shr: TranslateShift(ArithOp.AShr); break;
                    case ILOpCode.Shr_un: TranslateShift(ArithOp.LShr); break;

                    case ILOpCode.Ceq: TranslateCompare(ArithCmpPredicate.Eq); break;
                    case ILOpCode.Cgt: TranslateCompare(ArithCmpPredicate.Sgt); break;
                    case ILOpCode.Cgt_un: TranslateCompare(ArithCmpPredicate.Ugt); break;
                    case ILOpCode.Clt: TranslateCompare(ArithCmpPredicate.Slt); break;
                    case ILOpCode.Clt_un: TranslateCompare(ArithCmpPredicate.Ult); break;

                    case ILOpCode.Stloc_0: TranslateStloc(0); break;
                    case ILOpCode.Stloc_1: TranslateStloc(1); break;
                    case ILOpCode.Stloc_2: TranslateStloc(2); break;
                    case ILOpCode.Stloc_3: TranslateStloc(3); break;
                    case ILOpCode.Stloc_s: TranslateStloc(ilReader.ReadByte()); break;

                    case ILOpCode.Ldloc_0: TranslateLdloc(0); break;
                    case ILOpCode.Ldloc_1: TranslateLdloc(1); break;
                    case ILOpCode.Ldloc_2: TranslateLdloc(2); break;
                    case ILOpCode.Ldloc_3: TranslateLdloc(3); break;
                    case ILOpCode.Ldloc_s: TranslateLdloc(ilReader.ReadByte()); break;

                    case ILOpCode.Ldloca_s: TranslateLdloca(ilReader.ReadByte()); break;

                    case ILOpCode.Ldfld:
                        TranslateLdfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Ldflda:
                        TranslateLdflda(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Stfld:
                        TranslateStfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Initobj:
                        TranslateInitobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Starg_s: TranslateStarg(ilReader.ReadByte()); break;
                    case ILOpCode.Ldarg_0: TranslateLdarg(0); break;
                    case ILOpCode.Ldarg_1: TranslateLdarg(1); break;
                    case ILOpCode.Ldarg_2: TranslateLdarg(2); break;
                    case ILOpCode.Ldarg_3: TranslateLdarg(3); break;
                    case ILOpCode.Ldarg_s: TranslateLdarg(ilReader.ReadByte()); break;

                    case ILOpCode.Br_s:
                        TranslateBr(_currentBlock, ilReader.ReadSByte() + ilReader.Offset);
                        break;
                    case ILOpCode.Br:
                        TranslateBr(_currentBlock, ilReader.ReadInt32() + ilReader.Offset);
                        break;

                    case ILOpCode.Brtrue_s:
                        TranslateCondBranch(_currentBlock, ilReader.ReadSByte() + ilReader.Offset, branchIfTrue: true);
                        break;
                    case ILOpCode.Brtrue:
                        TranslateCondBranch(_currentBlock, ilReader.ReadInt32() + ilReader.Offset, branchIfTrue: true);
                        break;
                    case ILOpCode.Brfalse_s:
                        TranslateCondBranch(_currentBlock, ilReader.ReadSByte() + ilReader.Offset, branchIfTrue: false);
                        break;
                    case ILOpCode.Brfalse:
                        TranslateCondBranch(_currentBlock, ilReader.ReadInt32() + ilReader.Offset, branchIfTrue: false);
                        break;

                    case ILOpCode.Ldstr:
                        TranslateLdstr(ilReader.ReadInt32());
                        break;

                    case ILOpCode.Ldsfld:
                        TranslateLdsfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Stsfld:
                        TranslateStsfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Call:
                        TranslateCall(MetadataTokens.Handle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Callvirt:
                        TranslateCallvirt(MetadataTokens.Handle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Calli:
                        TranslateCalli(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Ldftn:
                        TranslateLdftn(MetadataTokens.Handle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Newobj:
                        TranslateNewobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Dup:
                        TranslateDup();
                        break;

                    case ILOpCode.Box:
                        TranslateBox(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Unbox_any:
                        TranslateUnboxAny(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Isinst:
                        TranslateTypeTest(MetadataTokens.EntityHandle(ilReader.ReadInt32()), "IsInstance");
                        break;
                    case ILOpCode.Castclass:
                        TranslateTypeTest(MetadataTokens.EntityHandle(ilReader.ReadInt32()), "CastClass");
                        break;

                    // pop → discard the top of the stack (any instructions that produced
                    // it, such as a call, have already been emitted).
                    case ILOpCode.Pop:
                        _stack.Pop();
                        break;

                    case ILOpCode.Sizeof:
                        TranslateSizeof(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Conv_i:
                    case ILOpCode.Conv_u:
                        TranslateConv(IRType.I16, signed: false);
                        break;
                    case ILOpCode.Conv_i1: TranslateConv(IRType.I8, signed: true); break;
                    case ILOpCode.Conv_u1: TranslateConv(IRType.I8, signed: false); break;
                    case ILOpCode.Conv_i2: TranslateConv(IRType.I16, signed: true); break;
                    case ILOpCode.Conv_u2: TranslateConv(IRType.I16, signed: false); break;
                    case ILOpCode.Conv_i4: TranslateConv(IRType.I32, signed: true); break;
                    case ILOpCode.Conv_u4: TranslateConv(IRType.I32, signed: false); break;
                    case ILOpCode.Conv_i8: TranslateConvToI64(signed: true); break;
                    case ILOpCode.Conv_u8: TranslateConvToI64(signed: false); break;

                    case ILOpCode.Stind_i1: TranslateStind(IRType.I8); break;
                    case ILOpCode.Stind_i2: TranslateStind(IRType.I16); break;
                    case ILOpCode.Stind_i4: TranslateStind(IRType.I32); break;
                    case ILOpCode.Stind_i8: TranslateStind(IRType.I64); break;
                    case ILOpCode.Stind_i: TranslateStind(IRType.I16); break;
                    case ILOpCode.Stind_ref: TranslateStind(IRType.Pointer); break;

                    case ILOpCode.Ldind_i1: TranslateLdind(IRType.I8, signed: true); break;
                    case ILOpCode.Ldind_u1: TranslateLdind(IRType.I8, signed: false); break;
                    case ILOpCode.Ldind_i2: TranslateLdind(IRType.I16, signed: true); break;
                    case ILOpCode.Ldind_u2: TranslateLdind(IRType.I16, signed: false); break;
                    case ILOpCode.Ldind_i4: TranslateLdind(IRType.I32, signed: true); break;
                    case ILOpCode.Ldind_u4: TranslateLdind(IRType.I32, signed: false); break;
                    case ILOpCode.Ldind_i8: TranslateLdind(IRType.I64, signed: true); break;
                    case ILOpCode.Ldind_i: TranslateLdind(IRType.I16, signed: false); break;
                    case ILOpCode.Ldind_ref: TranslateLdind(IRType.Pointer, signed: false); break;

                    case ILOpCode.Newarr:
                        TranslateNewarr(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Ldlen: TranslateLdlen(); break;
                    case ILOpCode.Ldelema:
                        TranslateLdelema(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Ldelem_i1: TranslateLdelem(IRType.I8, signed: true); break;
                    case ILOpCode.Ldelem_u1: TranslateLdelem(IRType.I8, signed: false); break;
                    case ILOpCode.Ldelem_i2: TranslateLdelem(IRType.I16, signed: true); break;
                    case ILOpCode.Ldelem_u2: TranslateLdelem(IRType.I16, signed: false); break;
                    case ILOpCode.Ldelem_i4: TranslateLdelem(IRType.I32, signed: true); break;
                    case ILOpCode.Ldelem_u4: TranslateLdelem(IRType.I32, signed: false); break;
                    case ILOpCode.Ldelem_i8: TranslateLdelem(IRType.I64, signed: true); break;
                    case ILOpCode.Ldelem_i: TranslateLdelem(IRType.I16, signed: false); break;
                    case ILOpCode.Ldelem_ref: TranslateLdelem(IRType.Pointer, signed: false); break;
                    case ILOpCode.Ldelem:
                        TranslateLdelem(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Stelem_i1: TranslateStelem(IRType.I8); break;
                    case ILOpCode.Stelem_i2: TranslateStelem(IRType.I16); break;
                    case ILOpCode.Stelem_i4: TranslateStelem(IRType.I32); break;
                    case ILOpCode.Stelem_i8: TranslateStelem(IRType.I64); break;
                    case ILOpCode.Stelem_i: TranslateStelem(IRType.I16); break;
                    case ILOpCode.Stelem_ref: TranslateStelem(IRType.Pointer); break;
                    case ILOpCode.Stelem:
                        TranslateStelem(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Ret:
                        TranslateRet();
                        break;

                    case ILOpCode.Switch:
                        TranslateSwitch(_currentBlock, ReadSwitchTargets(ref ilReader));
                        break;

                    case var _ when TryGetCompareBranch(opCode, out var cmpPredicate, out var cmpShort):
                        TranslateCompareBranch(
                            _currentBlock, ReadBranchTarget(ref ilReader, cmpShort), cmpPredicate);
                        break;

                    default:
                        throw new NotSupportedException(
                            $"IL→MIR: opcode {opCode} is not supported yet (method {_method.UniqueName}).");
                }
            }
        }

        /// <summary>
        /// Pre-scan the IL once to find every basic-block leader: offset 0, every
        /// branch target, and every instruction immediately following a branch
        /// (the not-taken fall-through). A MirBlock is created per leader; the
        /// map lets branch ops resolve targets, including forward references.
        /// </summary>
        private void DiscoverBlocks()
        {
            _blockStartOffsets = ComputeBlockLeaders();
            foreach (var offset in _blockStartOffsets)
                _ilOffsetToBlock[offset] = _function.CreateBlock();

            DiscoverBlocksLocalParams();
        }

        /// <summary>
        /// The set of basic-block leader IL offsets (offset 0, every branch
        /// target, and every instruction immediately after a branch), ascending.
        /// Used both by DiscoverBlocks (to create MirBlocks) and by the cross-call
        /// liveness analysis, which runs before classification (hence before the
        /// MirBlocks exist).
        /// </summary>
        private int[] ComputeBlockLeaders()
        {
            var leaders = new SortedSet<int> { 0 };

            var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (scan.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref scan);
                int? target = null;
                switch (opCode)
                {
                    case ILOpCode.Br_s:
                    case ILOpCode.Brtrue_s:
                    case ILOpCode.Brfalse_s:
                        target = scan.ReadSByte() + scan.Offset;
                        break;
                    case ILOpCode.Br:
                    case ILOpCode.Brtrue:
                    case ILOpCode.Brfalse:
                        target = scan.ReadInt32() + scan.Offset;
                        break;
                    case var _ when TryGetCompareBranch(opCode, out _, out var cmpShort):
                        target = ReadBranchTarget(ref scan, cmpShort);
                        break;
                    case ILOpCode.Switch:
                        foreach (var caseOffset in ReadSwitchTargets(ref scan)) leaders.Add(caseOffset);
                        // The not-taken fall-through starts a block too.
                        if (scan.RemainingBytes > 0)
                            leaders.Add(scan.Offset);
                        break;
                    default:
                        SkipOperand(ref scan, opCode);
                        break;
                }

                if (target is int t)
                {
                    leaders.Add(t);
                    // The instruction after a branch starts a block (fall-through
                    // for conditionals; dead leader for unconditional br — still
                    // a boundary, harmless if unreachable).
                    if (scan.RemainingBytes > 0)
                        leaders.Add(scan.Offset);
                }
            }

            return [.. leaders];
        }

        /// <summary>
        /// Wire up the per-block local parameter vregs (the SSA/PHI substitute),
        /// computed from local liveness. Split out of DiscoverBlocks so the
        /// leader/block creation can be read on its own.
        /// </summary>
        private void DiscoverBlocksLocalParams()
        {
            // Compute which locals are live-in to each block; only those become
            // block parameters (and thus block arguments on the incoming edges).
            // Threading *all* locals everywhere would make a transient bool temp
            // (the clt result feeding brtrue) a block argument too, breaking the
            // cmpi+cond_br fusion (which needs the i1 to have exactly one use).
            var liveIn = ComputeLocalLiveness();

            foreach (var offset in _blockStartOffsets)
            {
                if (offset == 0) continue; // entry block has the method params

                var block = _ilOffsetToBlock[offset];
                var live = liveIn[offset];
                var localParams = new int[_localTypes.Length];
                for (var i = 0; i < _localTypes.Length; i++)
                    localParams[i] = -1;

                foreach (var localIndex in live)
                {
                    // FrameSlot locals live in memory, not SSA, so they are
                    // never threaded across edges as block parameters.
                    if (_localIsFrameSlot[localIndex]) continue;
                    var vreg = _function.CreateVirtualRegister(_localTypes[localIndex]);
                    block.Parameters.Add(vreg);
                    localParams[localIndex] = vreg;
                }
                _blockLocalParams[block] = localParams;
            }
        }

        /// <summary>
        /// Classic backwards local-liveness fixpoint over the discovered blocks.
        /// Returns, per block-start offset, the set of local indices that are
        /// live on entry (read along some path before being overwritten). Used
        /// to decide which locals each block carries as parameters.
        /// </summary>
        private Dictionary<int, SortedSet<int>> ComputeLocalLiveness()
        {
            // Per block: upward-exposed uses (read before any def in the block),
            // defs (written in the block), and successor block-start offsets.
            var useBeforeDef = new Dictionary<int, HashSet<int>>();
            var defined = new Dictionary<int, HashSet<int>>();
            var successors = new Dictionary<int, List<int>>();

            for (var bi = 0; bi < _blockStartOffsets.Length; bi++)
            {
                var start = _blockStartOffsets[bi];
                var end = bi + 1 < _blockStartOffsets.Length ? _blockStartOffsets[bi + 1] : int.MaxValue;

                var ube = new HashSet<int>();
                var def = new HashSet<int>();
                var succ = new List<int>();
                var endsWithUncondBranch = false;
                var endsWithReturn = false;

                var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
                AdvanceTo(ref scan, start);
                while (scan.RemainingBytes > 0 && scan.Offset < end)
                {
                    var opCode = ReadOpCode(ref scan);
                    switch (opCode)
                    {
                        case ILOpCode.Ldloc_0: RecordUse(0, ube, def); break;
                        case ILOpCode.Ldloc_1: RecordUse(1, ube, def); break;
                        case ILOpCode.Ldloc_2: RecordUse(2, ube, def); break;
                        case ILOpCode.Ldloc_3: RecordUse(3, ube, def); break;
                        case ILOpCode.Ldloc_s: RecordUse(scan.ReadByte(), ube, def); break;
                        case ILOpCode.Ldloca_s: RecordUse(scan.ReadByte(), ube, def); break;

                        case ILOpCode.Ldarg_0: RecordArgUse(0, ube, def); break;
                        case ILOpCode.Ldarg_1: RecordArgUse(1, ube, def); break;
                        case ILOpCode.Ldarg_2: RecordArgUse(2, ube, def); break;
                        case ILOpCode.Ldarg_3: RecordArgUse(3, ube, def); break;
                        case ILOpCode.Ldarg_s: RecordArgUse(scan.ReadByte(), ube, def); break;
                        case ILOpCode.Starg_s: def.Add(_argLocal[scan.ReadByte()]); break;

                        case ILOpCode.Stloc_0: def.Add(0); break;
                        case ILOpCode.Stloc_1: def.Add(1); break;
                        case ILOpCode.Stloc_2: def.Add(2); break;
                        case ILOpCode.Stloc_3: def.Add(3); break;
                        case ILOpCode.Stloc_s: def.Add(scan.ReadByte()); break;

                        case ILOpCode.Br_s:
                            succ.Add(scan.ReadSByte() + scan.Offset);
                            endsWithUncondBranch = true;
                            break;
                        case ILOpCode.Br:
                            succ.Add(scan.ReadInt32() + scan.Offset);
                            endsWithUncondBranch = true;
                            break;
                        case ILOpCode.Brtrue_s:
                        case ILOpCode.Brfalse_s:
                            succ.Add(scan.ReadSByte() + scan.Offset);
                            break;
                        case ILOpCode.Brtrue:
                        case ILOpCode.Brfalse:
                            succ.Add(scan.ReadInt32() + scan.Offset);
                            break;
                        case ILOpCode.Ret:
                            endsWithReturn = true;
                            break;
                        case var _ when TryGetCompareBranch(opCode, out _, out var cmpShort):
                            succ.Add(ReadBranchTarget(ref scan, cmpShort));
                            break;
                        case ILOpCode.Switch:
                            succ.AddRange(ReadSwitchTargets(ref scan));
                            break;
                        default:
                            SkipOperand(ref scan, opCode);
                            break;
                    }
                }

                // Fall-through successor: a block that doesn't end in an
                // unconditional branch or return flows into the next leader.
                if (!endsWithUncondBranch && !endsWithReturn && end != int.MaxValue)
                    succ.Add(end);

                useBeforeDef[start] = ube;
                defined[start] = def;
                successors[start] = succ;
            }

            var liveIn = new Dictionary<int, SortedSet<int>>();
            var liveOut = new Dictionary<int, HashSet<int>>();
            foreach (var start in _blockStartOffsets)
            {
                liveIn[start] = [];
                liveOut[start] = [];
            }

            bool changed;
            do
            {
                changed = false;
                // Iterate in reverse program order for faster convergence.
                for (var bi = _blockStartOffsets.Length - 1; bi >= 0; bi--)
                {
                    var start = _blockStartOffsets[bi];

                    var outSet = liveOut[start];
                    var beforeOut = outSet.Count;
                    foreach (var s in successors[start])
                        if (liveIn.TryGetValue(s, out var sIn))
                            foreach (var v in sIn) outSet.Add(v);

                    // liveIn = useBeforeDef ∪ (liveOut − defined)
                    var inSet = new SortedSet<int>(useBeforeDef[start]);
                    foreach (var v in outSet)
                        if (!defined[start].Contains(v)) inSet.Add(v);

                    if (outSet.Count != beforeOut || !inSet.SetEquals(liveIn[start]))
                    {
                        liveIn[start] = inSet;
                        changed = true;
                    }
                }
            } while (changed);

            return liveIn;
        }

        private void RecordArgUse(int arg, HashSet<int> useBeforeDef, HashSet<int> defined)
        {
            if (arg < _argLocal.Length && _argLocal[arg] >= 0)
                RecordUse(_argLocal[arg], useBeforeDef, defined);
        }

        private static void RecordUse(int local, HashSet<int> useBeforeDef, HashSet<int> defined)
        {
            if (!defined.Contains(local))
                useBeforeDef.Add(local);
        }

        /// <summary>Advance an IL reader to a given offset (block discovery helper).</summary>
        private static void AdvanceTo(ref BlobReader reader, int offset)
        {
            while (reader.Offset < offset && reader.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref reader);
                SkipBranchOrOperand(ref reader, opCode);
            }
        }

        /// <summary>
        /// Like SkipOperand but also consumes branch displacement bytes (used
        /// when fast-forwarding to a block start, where branches are just bytes).
        /// </summary>
        private static void SkipBranchOrOperand(ref BlobReader reader, ILOpCode opCode)
        {
            switch (opCode)
            {
                case ILOpCode.Br_s: case ILOpCode.Brtrue_s: case ILOpCode.Brfalse_s:
                    reader.ReadSByte();
                    break;
                case ILOpCode.Br: case ILOpCode.Brtrue: case ILOpCode.Brfalse:
                    reader.ReadInt32();
                    break;
                case var _ when TryGetCompareBranch(opCode, out _, out var cmpShort):
                    if (cmpShort) reader.ReadSByte(); else reader.ReadInt32();
                    break;
                default:
                    SkipOperand(ref reader, opCode);
                    break;
            }
        }

        /// <summary>
        /// On entering a discovered block, rebind every local to that block's
        /// parameter vreg (the merged value) and set the builder's insertion
        /// point. The eval stack becomes the block's stack parameters, if any
        /// (whatever the previous block left behind has been passed on its edges).
        /// </summary>
        private void EnterBlock(MirBlock block)
        {
            _stack.Clear();
            if (_blockStackParams.TryGetValue(block, out var stackParams))
                foreach (var value in stackParams)
                    _stack.Push(value);

            if (_blockLocalParams.TryGetValue(block, out var localParams))
            {
                for (var i = 0; i < localParams.Length; i++)
                    if (localParams[i] >= 0)
                        _locals[i] = new StackValue(localParams[i], _localTypes[i]);
            }
            _builder.SetInsertionPointAtEnd(block);
        }

        /// <summary>
        /// Emit `cf.br target(localArgs)` passing the current SSA value of every
        /// local as a block argument, in local-index order.
        /// </summary>
        private void EmitBranchTo(MirBlock from, MirBlock target)
        {
            if (MaterialiseStackCompares()) from = _currentBlock;
            _builder.SetInsertionPointAtEnd(from);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(target, EdgeArgsFor(target)));
        }

        /// <summary>
        /// A compare still pending on the eval stack (`a &amp;&amp; b` used as a value leaves
        /// one across the branch) travels as a block argument, which needs a concrete
        /// 0/1 byte. Materialise each one in place; that splits the current block, so
        /// the branch must then be emitted from `_currentBlock`. Returns whether it did.
        /// </summary>
        private bool MaterialiseStackCompares()
        {
            if (!_stack.Any(static v => v.Type.Equals(IRType.I1)))
                return false;

            var values = _stack.Reverse().ToArray();
            _stack.Clear();
            foreach (var value in values)
                _stack.Push(value.Type.Equals(IRType.I1)
                    ? new StackValue(MaterialiseCompare(value.Vreg), IRType.I8)
                    : value);
            return true;
        }

        /// <summary>Block arguments for an edge to `target`: locals, then the eval stack.</summary>
        private MirOperand[] EdgeArgsFor(MirBlock target) =>
            [.. LocalArgsFor(target), .. StackArgsFor(target)];

        /// <summary>
        /// The values left on the eval stack (bottom first), as block arguments
        /// for `target`. The first edge into a block fixes its stack parameters'
        /// types; a later edge with a different width is resized to match. Emits
        /// any resize at the builder's insertion point, i.e. before the branch.
        /// </summary>
        private MirOperand[] StackArgsFor(MirBlock target)
        {
            var onStack = _stack.Reverse().ToArray();
            if (!_blockStackParams.TryGetValue(target, out var stackParams))
            {
                if (onStack.Length == 0) return [];
                stackParams = new StackValue[onStack.Length];
                for (var i = 0; i < onStack.Length; i++)
                {
                    if (onStack[i].Type.Equals(IRType.I1))
                        throw new NotSupportedException(
                            $"IL→MIR: unmaterialised compare on the eval stack across a branch " +
                            $"(method {_method.UniqueName}).");
                    var vreg = _function.CreateVirtualRegister(onStack[i].Type);
                    target.Parameters.Add(vreg);
                    stackParams[i] = onStack[i] with { Vreg = vreg };
                }
                _blockStackParams[target] = stackParams;
            }

            if (stackParams.Length != onStack.Length)
                throw new NotSupportedException(
                    $"IL→MIR: inconsistent eval stack depth at a join (method {_method.UniqueName}).");

            var args = new MirOperand[onStack.Length];
            for (var i = 0; i < onStack.Length; i++)
                args[i] = new VirtualReg(Resize(onStack[i], stackParams[i].Type).Vreg, IsDefinition: false);
            return args;
        }

        /// <summary>
        /// Block arguments for an edge to `target`: the current value of each
        /// live-in local, in ascending local-index order (matching the order
        /// the target declared its parameters in DiscoverBlocks).
        /// </summary>
        private MirOperand[] LocalArgsFor(MirBlock target)
        {
            if (!_blockLocalParams.TryGetValue(target, out var localParams))
                return [];

            var args = new List<MirOperand>();
            for (var i = 0; i < localParams.Length; i++)
                if (localParams[i] >= 0)
                    args.Add(new VirtualReg(_locals[i].Vreg, IsDefinition: false));
            return [.. args];
        }

        private static bool BlockHasTerminator(MirBlock block)
        {
            if (block.Instructions.Count == 0) return false;
            var last = block.Instructions[^1];
            return DialectRegistry.ById(last.Opcode.Dialect).IsTerminator(last.Opcode.Code);
        }

        private MirBlock BlockAt(int ilOffset) =>
            _ilOffsetToBlock.TryGetValue(ilOffset, out var block)
                ? block
                : throw new InvalidOperationException(
                    $"IL→MIR: branch target IL_{ilOffset:X4} is not a block leader " +
                    $"(method {_method.UniqueName}).");

        /// <summary>
        /// Advance a scanning reader past an opcode's inline operand bytes during
        /// block discovery (we only care about branch displacements there).
        /// </summary>
        private static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
        {
            switch (opCode)
            {
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s:
                case ILOpCode.Stloc_s: case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s:
                case ILOpCode.Starg_s: case ILOpCode.Unaligned:
                    reader.ReadByte();
                    break;
                case ILOpCode.Ldc_i4: case ILOpCode.Call: case ILOpCode.Callvirt: case ILOpCode.Calli:
                case ILOpCode.Ldstr: case ILOpCode.Ldsfld: case ILOpCode.Stsfld:
                case ILOpCode.Newobj: case ILOpCode.Ldfld: case ILOpCode.Stfld:
                case ILOpCode.Ldsflda: case ILOpCode.Ldflda: case ILOpCode.Newarr:
                case ILOpCode.Box: case ILOpCode.Unbox_any: case ILOpCode.Castclass:
                case ILOpCode.Isinst: case ILOpCode.Ldtoken: case ILOpCode.Initobj:
                case ILOpCode.Sizeof: case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn:
                case ILOpCode.Ldelem: case ILOpCode.Stelem: case ILOpCode.Ldelema:
                    reader.ReadInt32();
                    break;
                case ILOpCode.Ldc_i8:
                    reader.ReadInt64();
                    break;
                case ILOpCode.Ldc_r4:
                    reader.ReadSingle();
                    break;
                case ILOpCode.Ldc_r8:
                    reader.ReadDouble();
                    break;
                case ILOpCode.Switch:
                    var n = reader.ReadUInt32();
                    for (var i = 0; i < n; i++) reader.ReadInt32();
                    break;
                default:
                    break;
            }
        }

        private int[] _parameterVregs = [];

        /// <summary>
        /// The block currently being emitted into. Normally tracks the IL
        /// basic-block leader, but a value-used compare (TranslateCompare's diamond)
        /// splits the current block and redirects this to the merge block so
        /// subsequent IL emits after the materialised 0/1 result.
        /// </summary>
        private MirBlock _currentBlock = null!;

        /// <summary>
        /// The IL basic block being translated (the block that starts at a leader
        /// offset). Unlike _currentBlock it is never one of the synthetic blocks
        /// (compare diamonds, switch tests), so it is what a conditional branch's
        /// fall-through successor is found from.
        /// </summary>
        private MirBlock _ilBlock = null!;

        private void PushConstant(int value)
        {
            // C# integer literals are i32 on the IL stack.
            var vreg = _builder.BuildConstant(IRType.I32, value);
            _stack.Push(new StackValue(vreg, IRType.I32));
        }

        private void PushConstant64(long value)
        {
            var vreg = _builder.BuildConstant(IRType.I64, value);
            _stack.Push(new StackValue(vreg, IRType.I64, IsSigned: true));
        }

        /// <summary>
        /// add / sub / mul / div / rem: the integer binary ops whose result depends on
        /// the bits above the operands' width (see the byte promotion below). Irie
        /// strength-reduces a multiply (and unsigned divide / remainder) by a
        /// constant power of two and legalizes the rest as a call to a libcall, which
        /// Irie's own library supplies.
        /// </summary>
        private void TranslateIntegerBinary(ArithOp op)
        {
            // ECMA-335 keeps bytes and 16-bit integers on the stack as int32, so
            // `byte + byte` carries into bit 8. Widen here and let Irie narrow it
            // back when only the low byte is demanded (e.g. a following conv.u1):
            // the dead high-byte adds are deleted by the legalizer.
            var rawRight = _stack.Pop();
            var rawLeft = _stack.Pop();
            if (rawLeft.IsPointer || rawRight.IsPointer)
            {
                TranslatePointerArithmetic(op, rawLeft, rawRight);
                return;
            }

            var right = PromoteNarrow(rawRight);
            var left = PromoteNarrow(rawLeft);
            if (!left.Type.Equals(right.Type))
                UnifyWidths(ref left, ref right);

            var result = _function.CreateVirtualRegister(left.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(left.Vreg, IsDefinition: false),
                new VirtualReg(right.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, left.Type));
        }

        /// <summary>
        /// add / sub with a pointer operand. ptr + int (either order) and ptr - int
        /// are mem.ptradd with the integer resized to the pointer's i16 width — IL
        /// leaves the index as i32 (no conv), e.g. `q++` on a byte* emits
        /// `ldloc q; ldc.i4.1; add`; ptr - int adds the negated offset. ptr - ptr is
        /// the i16 difference of the two addresses. Every other combination is
        /// invalid IL, or not an add/sub at all.
        /// </summary>
        private void TranslatePointerArithmetic(ArithOp op, StackValue left, StackValue right)
        {
            if (op is ArithOp.AddI && left.IsPointer != right.IsPointer)
            {
                var (pointer, offset) = left.IsPointer ? (left, right) : (right, left);
                _stack.Push(new StackValue(EmitPtrAdd(pointer.Vreg, Resize(offset, IRType.I16).Vreg), IRType.Pointer));
                return;
            }

            if (op is ArithOp.SubI && left.IsPointer && !right.IsPointer)
            {
                var offset = Resize(right, IRType.I16).Vreg;
                var zero = _builder.BuildConstant(IRType.I16, 0);
                var negated = _function.CreateVirtualRegister(IRType.I16);
                _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                    new VirtualReg(negated, IsDefinition: true),
                    new VirtualReg(zero, IsDefinition: false),
                    new VirtualReg(offset, IsDefinition: false));
                _stack.Push(new StackValue(EmitPtrAdd(left.Vreg, negated), IRType.Pointer));
                return;
            }

            if (op is ArithOp.SubI && left.IsPointer && right.IsPointer)
            {
                var difference = _function.CreateVirtualRegister(IRType.I16);
                _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                    new VirtualReg(difference, IsDefinition: true),
                    new VirtualReg(AsInteger(left).Vreg, IsDefinition: false),
                    new VirtualReg(AsInteger(right).Vreg, IsDefinition: false));
                _stack.Push(new StackValue(difference, IRType.I16));
                return;
            }

            throw new NotSupportedException(
                $"IL→MIR: {op} with a pointer operand is not supported (method {_method.UniqueName}).");
        }

        /// <summary>%q : ptr = mem.ptradd %p, %offset (a byte offset, i16).</summary>
        private int EmitPtrAdd(int pointerVreg, int offsetVreg)
        {
            var result = _function.CreateVirtualRegister(IRType.Pointer);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.PtrAdd),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(pointerVreg, IsDefinition: false),
                new VirtualReg(offsetVreg, IsDefinition: false));
            return result;
        }

        /// <summary>ptr → its address as an i16 integer; anything else is returned as is.</summary>
        private StackValue AsInteger(StackValue value) =>
            value.IsPointer
                ? new StackValue(BuildPtrToInt(_builder, value.Vreg), IRType.I16)
                : value;

        /// <summary>
        /// The value as an address operand: a ptr is returned as is; an integer
        /// (native int, or an i32 holding an address) is brought to i16 and
        /// converted with inttoptr.
        /// </summary>
        private StackValue AsAddress(StackValue value)
        {
            if (value.IsPointer) return value;
            var integer = Resize(value, IRType.I16);
            return new StackValue(BuildIntToPtr(_builder, integer.Vreg), IRType.Pointer);
        }

        /// <summary>
        /// A pop for operations that are defined on integers only: a ptr operand
        /// (invalid IL, or something that needs an explicit conv.i first) is rejected.
        /// </summary>
        private StackValue PopInteger(string operation)
        {
            var value = PopMaterialised();
            if (value.IsPointer)
                throw new NotSupportedException(
                    $"IL→MIR: {operation} on a pointer is not supported (method {_method.UniqueName}).");
            return value;
        }

        /// <summary>
        /// Whether local `index` is a block parameter of any block, i.e. its value
        /// flows across a block boundary rather than dying in the block that set it.
        /// </summary>
        private bool IsLocalLiveAcrossBlocks(int index) =>
            _blockLocalParams.Values.Any(localParams => index < localParams.Length && localParams[index] >= 0);

        private void TranslateStloc(int index)
        {
            if (!_localIsFrameSlot[index])
            {
                // A pending compare (i1) is kept as is: Roslyn's debug codegen parks
                // `ceq`/`clt` results in a temp local that a brfalse then reloads,
                // and the cmpi must stay fusable with that cond_br. It is
                // materialised only where a concrete byte is needed — including when
                // the local is live into another block, where it travels as an i8
                // block argument and an i1 would mismatch the parameter's type.
                var stored = _stack.Pop();
                if (stored.Type.Equals(IRType.I1) && IsLocalLiveAcrossBlocks(index))
                    stored = new StackValue(MaterialiseCompare(stored.Vreg), IRType.I8);
                _locals[index] = stored.Type.Equals(IRType.I1)
                    ? stored
                    : Resize(stored, _localTypes[index]);
                return;
            }

            // FrameSlot local: store through memory.
            var value = _stack.Pop();
            var destAddr = _builder.BuildFrameAddr(index);
            if (_localClrTypes[index] is EcmaType { IsValueType: true } structType)
            {
                // Struct assignment is a byte-wise copy from the source struct
                // (value.Vreg holds its address) into this slot.
                EmitStructCopy(destAddr, value.Vreg, structType.InstanceSize);
            }
            else
            {
                // Use the local's natural CLR width, not _localTypes[index]
                // (which holds an I16 placeholder for FrameSlot locals — see the
                // classification loop). This mirrors TranslateLdloc's load width.
                var type = ToIRType(_localClrTypes[index]);
                _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                    new VirtualReg(destAddr, IsDefinition: false),
                    new VirtualReg(Resize(value, type).Vreg, IsDefinition: false));
            }
        }

        /// <summary>Push a value loaded from storage of CLR type `clr`.</summary>
        private void PushTyped(int vreg, IRType type, TypeDescription clr) =>
            _stack.Push(new StackValue(vreg, type, IsSignedClr(clr)));

        private void TranslateLdloc(int index)
        {
            if (_localIsFrameSlot[index])
            {
                var addr = _builder.BuildFrameAddr(index);
                if (_localClrTypes[index] is EcmaType { IsValueType: true })
                {
                    // Loading a struct local pushes its address; the value is
                    // consumed in place (ldfld) or copied on the next stloc.
                    _stack.Push(new StackValue(addr, IRType.Pointer));
                    return;
                }

                // Address-taken primitive: load the value out of the slot.
                var type = ToIRType(_localClrTypes[index]);
                var value = _function.CreateVirtualRegister(type);
                _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                    new VirtualReg(value, IsDefinition: true),
                    new VirtualReg(addr, IsDefinition: false));
                PushTyped(value, type, _localClrTypes[index]);
                return;
            }

            if (!_locals.TryGetValue(index, out var ssaValue))
                throw new NotSupportedException(
                    $"IL→MIR: ldloc of uninitialized local {index} (method {_method.UniqueName}).");
            PushTyped(ssaValue.Vreg, ssaValue.Type, _localClrTypes[index]);
        }

        /// <summary>
        /// ldloca.N → push the address of the local's frame slot (a managed
        /// pointer). The local was forced FrameSlot-bound during classification.
        /// </summary>
        private void TranslateLdloca(int index)
        {
            if (!_localIsFrameSlot[index])
                throw new InvalidOperationException(
                    $"IL→MIR: ldloca of non-FrameSlot local {index} (method {_method.UniqueName}).");
            var addr = _builder.BuildFrameAddr(index);
            _stack.Push(new StackValue(addr, IRType.Pointer));
        }

        /// <summary>Local that shadows each parameter assigned by `starg` (-1: never assigned).</summary>
        private int[] _argLocal = [];

        private void TranslateStarg(int index)
        {
            if (index >= _argLocal.Length || _argLocal[index] < 0)
                throw new NotSupportedException(
                    $"IL→MIR: starg {index} out of range (method {_method.UniqueName}).");
            TranslateStloc(_argLocal[index]);
        }

        private void TranslateLdarg(int index)
        {
            if (index < _argLocal.Length && _argLocal[index] >= 0)
            {
                TranslateLdloc(_argLocal[index]);
                return;
            }
            if (index >= _parameterVregs.Length)
                throw new NotSupportedException(
                    $"IL→MIR: ldarg {index} out of range (method {_method.UniqueName}).");
            var type = ToIRType(_method.Parameters[index].Type);

            PushTyped(_parameterVregs[index], type, _method.Parameters[index].Type);
        }

        /// <summary>br / br.s → cf.br to the target block, passing current locals as args.</summary>
        private void TranslateBr(MirBlock from, int target)
        {
            EmitBranchTo(from, BlockAt(target));
        }

        /// <summary>
        /// and / or / xor: both operands must have the same integer width. A
        /// value-used compare (i1) is first materialised to its 0/1 i8, so
        /// `(a &lt; b) &amp; (c &lt; d)` combines two bytes.
        /// </summary>
        private void TranslateBitwise(ArithOp op)
        {
            var right = PopInteger("bitwise op");
            var left = PopInteger("bitwise op");
            // Two narrow operands of the same signedness combine bitwise at their
            // own width (extension commutes with and/or/xor); otherwise promote.
            if (left.Type.Equals(right.Type) && left.IsSigned != right.IsSigned)
            {
                left = PromoteNarrow(left);
                right = PromoteNarrow(right);
            }
            UnifyWidths(ref left, ref right);

            var result = _function.CreateVirtualRegister(left.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(left.Vreg, IsDefinition: false),
                new VirtualReg(right.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, left.Type, left.IsSigned && right.IsSigned));
        }

        /// <summary>
        /// shl / shr / shr.un: shift the value by an int32 amount. A byte operand is
        /// first widened to int32, as ECMA-335's stack promotion would. The amount
        /// becomes an i8 vreg: a constant is re-emitted as an i8 constant so the
        /// legalizer can read it; anything else is truncated, and the legalizer turns
        /// the shift into a libcall.
        /// </summary>
        private void TranslateShift(ArithOp op)
        {
            var amount = PopInteger("shift");
            var value = PopInteger("shift");
            value = PromoteNarrow(value);

            int amountVreg;
            if (_function.GetDefinition(amount.Vreg) is
                { Opcode: var amountOpcode, Operands: [_, Immediate amountImm] }
                && amountOpcode == ArithDialect.OpRef(ArithOp.Constant))
            {
                amountVreg = _builder.BuildConstant(IRType.I8, amountImm.Value & 0xFF);
            }
            else
            {
                amountVreg = Resize(amount, IRType.I8).Vreg;
            }

            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false),
                new VirtualReg(amountVreg, IsDefinition: false));
            _stack.Push(value with { Vreg = result });
        }

        /// <summary>not: bitwise complement, `x ^ -1` at the operand's width.</summary>
        private void TranslateNot()
        {
            var value = PromoteNarrow(PopInteger("not"));
            var allOnes = _builder.BuildConstant(value.Type, -1);
            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.Xor),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false),
                new VirtualReg(allOnes, IsDefinition: false));
            _stack.Push(new StackValue(result, value.Type));
        }

        /// <summary>neg: two's-complement negation, `0 - x` at the operand's width.</summary>
        private void TranslateNeg()
        {
            var value = PromoteNarrow(PopInteger("neg"));
            var zero = _builder.BuildConstant(value.Type, 0);
            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(zero, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, value.Type));
        }

        /// <summary>
        /// A byte or 16-bit integer on the IL evaluation stack is an int32
        /// (ECMA-335 §III.1.1): widen an i8 / i16 integer to i32 by its signedness
        /// before an operation whose result depends on the bits above its width
        /// (neg, not, add, sub, shifts, compares). Callers have already ruled out
        /// a ptr operand (PopInteger / AsInteger).
        /// </summary>
        private StackValue PromoteNarrow(StackValue value) =>
            value.Type.SizeInBits is 8 or 16
                ? Resize(value, IRType.I32)
                : value;

        /// <summary>
        /// Convert an integer stack value to `to`'s width: truncate when narrowing,
        /// otherwise zero- or sign-extend according to the value's signedness. A
        /// same-width value is returned as is. (i1 never reaches here — callers
        /// materialise a pending compare first, see PopMaterialised.)
        /// </summary>
        /// <remarks>
        /// A ptr operand or target goes through inttoptr / ptrtoint: Resize to a
        /// ptr type yields an address (AsAddress), and a ptr source is first read
        /// as its i16 address (AsInteger).
        /// </remarks>
        private StackValue Resize(StackValue value, IRType to)
        {
            if (to is IRPointerType) return AsAddress(value);
            value = AsInteger(value);

            var from = value.Type.SizeInBits;
            if (from == to.SizeInBits) return value;
            if (from < 8)
                throw new InvalidOperationException(
                    $"IL→MIR: cannot resize a {value.Type} value (method {_method.UniqueName}).");

            var op = to.SizeInBits < from ? CastOp.Trunc
                : value.IsSigned ? CastOp.Sext : CastOp.Zext;
            var result = _function.CreateVirtualRegister(to);
            _builder.BuildInstruction(CastDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false));
            return value with { Vreg = result, Type = to };
        }

        /// <summary>
        /// Binary operands of different widths (an i8 byte against an i32 constant)
        /// are widened to the wider one, as ECMA-335's implicit int32 promotion
        /// would, each side extending by its own signedness.
        /// </summary>
        private void UnifyWidths(ref StackValue left, ref StackValue right)
        {
            if (left.Type.SizeInBits < right.Type.SizeInBits) left = Resize(left, right.Type);
            else if (right.Type.SizeInBits < left.Type.SizeInBits) right = Resize(right, left.Type);
        }

        /// <summary>
        /// conv.i1 / conv.u1 / conv.i4 / conv.u4 / conv.i / conv.u: convert to the
        /// target width (truncating or extending by the source's signedness).
        /// `signed` is the result's signedness for narrow results: conv.i1 yields
        /// a sign-carrying byte, conv.u1 a zero-extending one.
        /// conv.i / conv.u produce a native int, an i16 integer (a ptr operand is
        /// read as its address first).
        /// </summary>
        private void TranslateConv(IRType to, bool signed)
        {
            var result = Resize(PopMaterialised(), to);
            _stack.Push(result with { IsSigned = signed });
        }

        /// <summary>
        /// conv.i8 / conv.u8: widen to 64 bits. The *opcode* picks the extension of
        /// an int32 (conv.i8 sign-extends, conv.u8 zero-extends), whatever the
        /// value's own signedness; a byte or 16-bit value is first widened to the
        /// int32 it already is on the IL stack, by its own signedness.
        /// </summary>
        private void TranslateConvToI64(bool signed)
        {
            var value = AsInteger(PopMaterialised());
            if (value.Type.SizeInBits < 32) value = Resize(value, IRType.I32);
            var result = Resize(value with { IsSigned = signed }, IRType.I64);
            _stack.Push(result with { IsSigned = signed });
        }

        /// <summary>Pops the stack, turning a pending i1 compare into a concrete 0/1 i8.</summary>
        private StackValue PopMaterialised()
        {
            var value = _stack.Pop();
            return value.Type.Equals(IRType.I1)
                ? new StackValue(MaterialiseCompare(value.Vreg), IRType.I8)
                : value;
        }

        /// <summary>
        /// ceq/cgt/cgt.un/clt/clt.un → arith.cmpi &lt;pred&gt;, %a, %b → i1, pushed onto
        /// the eval stack.
        /// </summary>
        /// <remarks>
        /// The cmpi is left unmaterialised: if an immediately-following
        /// brtrue/brfalse consumes it, TranslateCondBranch builds a cf.cond_br on
        /// it and Irie's isel fuses the pair (loop-condition path). If instead the
        /// i1 is consumed as a *value* (e.g. passed to Console.WriteLine(bool)),
        /// TranslateCall materialises it into a concrete 0/1 i8 via a cond_br
        /// diamond (see MaterialiseCompare). An i1 *operand* (comparing two
        /// compares) is materialised to its 0/1 i8 first.
        /// </remarks>
        private void TranslateCompare(ArithCmpPredicate predicate)
        {
            // Roslyn lowers `a != b`, `a <= b`, `a >= b` (and `!(a < b)`) as
            // `<compare>; ldc.i4.0; ceq`: an i1 compared for equality with a
            // constant 0/1. That is just the i1 or its logical negation — an
            // arith.not, which isel strips at the consuming cf.cond_br — so do
            // not widen the i1 to a byte only to compare it against an i32.
            if (predicate == ArithCmpPredicate.Eq
                && _stack.Count >= 2
                && _stack.ToArray() is [{ Type: var topType, Vreg: var topVreg }, { Type: var belowType, Vreg: var belowVreg }, ..]
                && TryFoldBoolCompare(belowType, belowVreg, topType, topVreg) is { } folded)
            {
                _stack.Pop();
                _stack.Pop();
                _stack.Push(folded);
                return;
            }

            // A pointer compares as its address.
            var right = PromoteNarrow(AsInteger(PopMaterialised()));
            var left = PromoteNarrow(AsInteger(PopMaterialised()));
            UnifyWidths(ref left, ref right);

            var result = _builder.BuildCmpI(predicate, left.Vreg, right.Vreg);
            _stack.Push(new StackValue(result, IRType.I1));
        }

        /// <summary>
        /// `i1 == 0` → not(i1), `i1 == 1` → i1 (in either operand order); null when
        /// the operands are not an i1 and a constant 0/1.
        /// </summary>
        private StackValue? TryFoldBoolCompare(IRType leftType, int leftVreg, IRType rightType, int rightVreg)
        {
            if (rightType.Equals(IRType.I1)) (leftType, leftVreg, rightType, rightVreg) = (rightType, rightVreg, leftType, leftVreg);
            if (!leftType.Equals(IRType.I1)) return null;
            if (_function.GetDefinition(rightVreg) is not { } def
                || def.Opcode != ArithDialect.OpRef(ArithOp.Constant)
                || def.Operands is not [_, Immediate { Value: var value and (0 or 1) }])
                return null;

            if (value == 1) return new StackValue(leftVreg, IRType.I1);

            var negated = _function.CreateVirtualRegister(IRType.I1);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.Not),
                new VirtualReg(negated, IsDefinition: true),
                new VirtualReg(leftVreg, IsDefinition: false));
            return new StackValue(negated, IRType.I1);
        }

        /// <summary>
        /// The ECMA-335 compare-and-branch family (beq, bge, bgt, ble, blt, bne.un,
        /// bge.un, bgt.un, ble.un, blt.un, each with a short form): the signed /
        /// unsigned / equality predicate the fused compare tests. Float
        /// unordered semantics of the `.un` forms are not modelled (no floats).
        /// </summary>
        private static bool TryGetCompareBranch(
            ILOpCode opCode, out ArithCmpPredicate predicate, out bool isShort)
        {
            isShort = opCode is ILOpCode.Beq_s or ILOpCode.Bge_s or ILOpCode.Bgt_s
                or ILOpCode.Ble_s or ILOpCode.Blt_s or ILOpCode.Bne_un_s
                or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s or ILOpCode.Ble_un_s
                or ILOpCode.Blt_un_s;
            switch (opCode)
            {
                case ILOpCode.Beq or ILOpCode.Beq_s:       predicate = ArithCmpPredicate.Eq;  return true;
                case ILOpCode.Bge or ILOpCode.Bge_s:       predicate = ArithCmpPredicate.Sge; return true;
                case ILOpCode.Bgt or ILOpCode.Bgt_s:       predicate = ArithCmpPredicate.Sgt; return true;
                case ILOpCode.Ble or ILOpCode.Ble_s:       predicate = ArithCmpPredicate.Sle; return true;
                case ILOpCode.Blt or ILOpCode.Blt_s:       predicate = ArithCmpPredicate.Slt; return true;
                case ILOpCode.Bne_un or ILOpCode.Bne_un_s: predicate = ArithCmpPredicate.Ne;  return true;
                case ILOpCode.Bge_un or ILOpCode.Bge_un_s: predicate = ArithCmpPredicate.Uge; return true;
                case ILOpCode.Bgt_un or ILOpCode.Bgt_un_s: predicate = ArithCmpPredicate.Ugt; return true;
                case ILOpCode.Ble_un or ILOpCode.Ble_un_s: predicate = ArithCmpPredicate.Ule; return true;
                case ILOpCode.Blt_un or ILOpCode.Blt_un_s: predicate = ArithCmpPredicate.Ult; return true;
            }
            predicate = default;
            return false;
        }

        /// <summary>
        /// Displacement of a branch whose operand is a 1-byte (short) or 4-byte
        /// offset from the end of the instruction.
        /// </summary>
        private static int ReadBranchTarget(ref BlobReader reader, bool isShort)
            => isShort ? reader.ReadSByte() + reader.Offset : reader.ReadInt32() + reader.Offset;

        /// <summary>
        /// A switch's jump table as absolute IL offsets. The displacements are
        /// relative to the end of the whole instruction (after the last entry).
        /// </summary>
        private static int[] ReadSwitchTargets(ref BlobReader reader)
        {
            var count = reader.ReadUInt32();
            var targets = new int[count];
            for (var i = 0; i < count; i++) targets[i] = reader.ReadInt32();
            for (var i = 0; i < count; i++) targets[i] += reader.Offset;
            return targets;
        }

        /// <summary>
        /// switch: Irie has no multiway branch, so lower the jump table to a chain
        /// of equality tests (llvm-mos does likewise for small tables; it has no
        /// indirect-jump table lowering either):
        ///   &lt;from&gt;:  %c0 = cmpi eq %v, 0 ; cond_br %c0, case0, test1
        ///   test1:   %c1 = cmpi eq %v, 1 ; cond_br %c1, case1, test2
        ///   ...
        ///   testN:   cond_br %cN, caseN, &lt;fall-through&gt;
        /// </summary>
        /// <remarks>
        /// A value outside 0..N-1 (as unsigned) matches no test and falls through.
        /// Cases that already target the fall-through block need no test.
        /// </remarks>
        private void TranslateSwitch(MirBlock from, int[] targets)
        {
            var fallThrough = NextBlockAfterIlBlock();
            var selector = PromoteNarrow(AsInteger(PopMaterialised()));

            var cases = new List<(int Value, MirBlock Target)>();
            for (var i = 0; i < targets.Length; i++)
            {
                var target = BlockAt(targets[i]);
                if (!ReferenceEquals(target, fallThrough)) cases.Add((i, target));
            }

            if (cases.Count == 0)
            {
                EmitBranchTo(_currentBlock, fallThrough);
                return;
            }

            for (var i = 0; i < cases.Count; i++)
            {
                var (value, target) = cases[i];
                var isLast = i == cases.Count - 1;
                var next = isLast ? fallThrough : _function.CreateBlock();

                _builder.SetInsertionPointAtEnd(_currentBlock);
                var constant = _builder.BuildConstant(selector.Type, value);
                var cond = _builder.BuildCmpI(ArithCmpPredicate.Eq, selector.Vreg, constant);
                EmitCondBr(cond, target, next);

                if (!isLast)
                {
                    _currentBlock = next;
                    _builder.SetInsertionPointAtEnd(next);
                }
            }
        }

        /// <summary>
        /// beq/bge/…: pop two values, compare with `predicate`, branch to `target`
        /// when it holds, else fall through. The cmpi is built directly at the end
        /// of the block so isel fuses it with the cf.cond_br (no i1 is
        /// materialised), exactly as for `clt; brtrue`.
        /// </summary>
        private void TranslateCompareBranch(MirBlock from, int target, ArithCmpPredicate predicate)
        {
            // Resolve the fall-through from the block the branch started in: an
            // i1 operand's materialisation (PopMaterialised) moves _currentBlock
            // to a synthetic merge block with no IL offset.
            var fallThrough = NextBlockAfterIlBlock();

            // A compare pending beneath the two operands (`(a == b) == (c > d ? …)`)
            // must become a byte before the branch carries the stack. Done before the
            // compare so its cmpi stays at the end of the block the cond_br is in.
            MaterialiseStackCompares();
            TranslateCompare(predicate);
            var cond = _stack.Pop();

            _builder.SetInsertionPointAtEnd(_currentBlock);
            EmitCondBr(cond.Vreg, BlockAt(target), fallThrough);
        }

        /// <summary>
        /// Build the cond_br diamond that turns an i1 compare result (defined by a
        /// preceding arith.cmpi at the end of the current block) into a concrete
        /// 0/1 i8:
        ///   &lt;current&gt;:                              ; cmpi already emitted here
        ///       %c : i1 = arith.cmpi &lt;pred&gt;, %a, %b
        ///       cf.cond_br %c, trueLeg, falseLeg     ; appended here
        ///   trueLeg:  cf.br merge(1 : i8)
        ///   falseLeg: cf.br merge(0 : i8)
        ///   merge(%r : i8):                         ; becomes the new current block
        /// </summary>
        /// <remarks>
        /// The cmpi+cond_br pair is selected by isel exactly as the fused branch
        /// path. Returns the merge block's i8 parameter vreg (the 0/1 result).
        /// </remarks>
        private int MaterialiseCompare(int condVreg)
        {
            var trueLeg = _function.CreateBlock();
            var falseLeg = _function.CreateBlock();
            var merge = _function.CreateBlock();

            var resultVreg = _function.CreateVirtualRegister(IRType.I8);
            merge.Parameters.Add(resultVreg);

            // cond_br on the existing cmpi result, at the end of the current block.
            _builder.SetInsertionPointAtEnd(_currentBlock);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.CondBr),
                new VirtualReg(condVreg, IsDefinition: false),
                new BlockTarget(trueLeg, []),
                new BlockTarget(falseLeg, []));

            // trueLeg → merge(1 : i8)
            _builder.SetInsertionPointAtEnd(trueLeg);
            var one = _builder.BuildConstant(IRType.I8, 1);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(merge, [new VirtualReg(one, IsDefinition: false)]));

            // falseLeg → merge(0 : i8)
            _builder.SetInsertionPointAtEnd(falseLeg);
            var zero = _builder.BuildConstant(IRType.I8, 0);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(merge, [new VirtualReg(zero, IsDefinition: false)]));

            // Subsequent IL emits into the merge block.
            _builder.SetInsertionPointAtEnd(merge);
            _currentBlock = merge;
            return resultVreg;
        }

        /// <summary>
        /// brtrue/brfalse → cf.cond_br on the i1 condition. The taken edge goes
        /// to the branch target; the not-taken edge falls through to the next
        /// block (the instruction immediately after the branch — a discovered
        /// leader). Irie's isel fuses a preceding arith.cmpi into the cond_br.
        /// </summary>
        private void TranslateCondBranch(MirBlock from, int target, bool branchIfTrue)
        {
            var cond = _stack.Pop();

            var fallThrough = NextBlockAfterIlBlock();
            var targetBlock = BlockAt(target);

            if (MaterialiseStackCompares()) from = _currentBlock;
            _builder.SetInsertionPointAtEnd(from);
            // A reference tested for null compares as its address.
            if (cond.IsPointer) cond = AsInteger(cond);

            // A bare value condition (e.g. `while (c != 0)` lowers to brtrue on the
            // loaded byte, not a clt) is not an i1 — isel only lowers a cond_br
            // that is fused with a preceding arith.cmpi. Synthesize that compare
            // against zero: brtrue → `cmpi ne, v, 0`, brfalse → `cmpi eq, v, 0`.
            // (When the condition is already an i1 from a clt, branch on it
            // directly so isel fuses the existing cmpi.)
            int condVreg;
            if (cond.Type.Equals(IRType.I1))
            {
                condVreg = cond.Vreg;
            }
            else
            {
                var zero = _builder.BuildConstant(cond.Type, 0);
                var predicate = branchIfTrue ? ArithCmpPredicate.Ne : ArithCmpPredicate.Eq;
                condVreg = _builder.BuildCmpI(predicate, cond.Vreg, zero);
                // The predicate already encodes the test, so both forms now take
                // the target on the "compare succeeded" edge.
                branchIfTrue = true;
            }

            // brtrue: cond true → target, false → fall-through.
            // brfalse: invert the two edges.
            var trueEdge = branchIfTrue ? targetBlock : fallThrough;
            var falseEdge = branchIfTrue ? fallThrough : targetBlock;
            EmitCondBr(condVreg, trueEdge, falseEdge);
        }

        /// <summary>
        /// cf.cond_br %cond, trueEdge(locals), falseEdge(locals) at the builder's
        /// insertion point, threading the current locals as block arguments.
        /// </summary>
        private void EmitCondBr(int condVreg, MirBlock trueEdge, MirBlock falseEdge)
        {
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.CondBr),
                new VirtualReg(condVreg, IsDefinition: false),
                new BlockTarget(trueEdge, EdgeArgsFor(trueEdge)),
                new BlockTarget(falseEdge, EdgeArgsFor(falseEdge)));
        }

        /// <summary>
        /// The block that starts at the next leader offset after the current IL
        /// basic block — the fall-through successor of a conditional branch.
        /// </summary>
        private MirBlock NextBlockAfterIlBlock()
        {
            foreach (var (offset, b) in _ilOffsetToBlock)
            {
                if (!ReferenceEquals(b, _ilBlock)) continue;
                var idx = Array.IndexOf(_blockStartOffsets, offset);
                if (idx >= 0 && idx + 1 < _blockStartOffsets.Length)
                    return _ilOffsetToBlock[_blockStartOffsets[idx + 1]];
                break;
            }
            throw new InvalidOperationException(
                $"IL→MIR: conditional branch in a block with no fall-through successor " +
                $"(method {_method.UniqueName}).");
        }

        /// <summary>
        /// ldstr "..." → intern the string as a MirGlobal holding a whole string
        /// object and push a reference to it: the symbol plus the header size, i.e.
        /// the address of its length.
        /// </summary>
        private void TranslateLdstr(int token)
        {
            var value = _method.MetadataReader.GetUserString(
                MetadataTokens.UserStringHandle(token));
            var stringKey = _parent.RegisterStringGlobal(value);

            var symbol = _builder.BuildMemSymbol(stringKey);
            _stack.Push(new StackValue(EmitFieldAddress(symbol, StringObjectHeaderSize), IRType.Pointer));
        }

        /// <summary>
        /// ldsfld &lt;field&gt; → load the static field's value via its symbolic
        /// address: %p = mem.symbol @&lt;owner&gt;_&lt;field&gt;; %v = mem.load.iN %p.
        /// </summary>
        private void TranslateLdsfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var name = _parent.RegisterStaticFieldGlobal(field);
            var type = ToIRType(field.Type);

            var ptr = _builder.BuildMemSymbol(name);
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(ptr, IsDefinition: false));
            PushTyped(value, type, field.Type);
        }

        /// <summary>
        /// stsfld &lt;field&gt; → store the top of stack into the static field's
        /// symbolic address: %p = mem.symbol @&lt;owner&gt;_&lt;field&gt;; mem.store.iN %p, %v.
        /// </summary>
        private void TranslateStsfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var name = _parent.RegisterStaticFieldGlobal(field);
            var type = ToIRType(field.Type);

            var value = Resize(PopMaterialised(), type);
            var ptr = _builder.BuildMemSymbol(name);
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(ptr, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        /// <summary>
        /// ldfld &lt;field&gt; → load the field from base + offset. The base (top of
        /// stack) is a pointer / managed reference / struct address.
        /// </summary>
        private void TranslateLdfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var obj = AsAddress(_stack.Pop());
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(addr, IsDefinition: false));
            PushTyped(value, type, field.Type);
        }

        /// <summary>
        /// ldflda &lt;field&gt; → push the address of the field: the object pointer (or
        /// struct address) plus the field's offset.
        /// </summary>
        private void TranslateLdflda(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);

            var obj = AsAddress(_stack.Pop());
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            _stack.Push(new StackValue(addr, IRType.Pointer));
        }

        /// <summary>
        /// stfld &lt;field&gt; → store top-of-stack into (base + offset). Stack layout
        /// (top first): value, then the object pointer / struct address.
        /// </summary>
        private void TranslateStfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var value = Resize(PopMaterialised(), type);
            var obj = AsAddress(_stack.Pop());
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        /// <summary>initobj &lt;type&gt; → zero the &lt;size&gt; bytes at the pointer on top of stack.</summary>
        private void TranslateInitobj(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var ptr = AsAddress(_stack.Pop());
            var zero = _builder.BuildConstant(IRType.I8, 0);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.Fill),
                new VirtualReg(ptr.Vreg, IsDefinition: false),
                new VirtualReg(zero, IsDefinition: false),
                new Immediate(type.InstanceSize));
        }

        /// <summary>
        /// Address of a field at `offset` bytes from `baseVreg` (a ptr). Offset 0
        /// reuses the base directly; otherwise emit a mem.ptradd of a constant
        /// offset (the legalizer narrows it to byte-wise arithmetic).
        /// </summary>
        private int EmitFieldAddress(int baseVreg, int offset)
        {
            if (offset == 0) return baseVreg;
            return EmitPtrAdd(baseVreg, _builder.BuildConstant(IRType.I16, offset));
        }

        /// <summary>
        /// Copy `size` bytes from the struct at `srcAddr` to the struct at
        /// `destAddr` (both pointers), one byte at a time. Used by stloc of a
        /// value-type local (an IL struct assignment / copy).
        /// </summary>
        private void EmitStructCopy(int destAddr, int srcAddr, int size)
        {
            for (var offset = 0; offset < size; offset++)
            {
                var src = EmitFieldAddress(srcAddr, offset);
                var dst = EmitFieldAddress(destAddr, offset);
                var b = _function.CreateVirtualRegister(IRType.I8);
                _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI8),
                    new VirtualReg(b, IsDefinition: true),
                    new VirtualReg(src, IsDefinition: false));
                _builder.BuildInstruction(MemDialect.OpRef(MemOp.StoreI8),
                    new VirtualReg(dst, IsDefinition: false),
                    new VirtualReg(b, IsDefinition: false));
            }
        }

        /// <summary>Pre-scan the IL for the parameters assigned with `starg`.</summary>
        private HashSet<int> ScanStargParameters()
        {
            var result = new HashSet<int>();
            var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (scan.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref scan);
                if (opCode == ILOpCode.Starg_s)
                    result.Add(scan.ReadByte());
                else
                    SkipBranchOrOperand(ref scan, opCode);
            }
            return result;
        }

        /// <summary>
        /// Pre-scan the IL once for the set of locals that are address-taken
        /// (have an `ldloca`). Those are forced FrameSlot-bound regardless of
        /// type, alongside the value-type locals.
        /// </summary>
        private HashSet<int> ScanAddressTakenLocals()
        {
            var result = new HashSet<int>();
            var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (scan.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref scan);
                if (opCode == ILOpCode.Ldloca_s)
                    result.Add(scan.ReadByte());
                else
                    SkipBranchOrOperand(ref scan, opCode);
            }
            return result;
        }

        private static MemOp LoadOpFor(IRType type) => type is IRPointerType ? MemOp.LoadPtr : type.SizeInBits switch
        {
            8 => MemOp.LoadI8,
            16 => MemOp.LoadI16,
            32 => MemOp.LoadI32,
            64 => MemOp.LoadI64,
            _ => throw new NotSupportedException(
                $"IL→MIR: mem.load of {type} is not supported yet."),
        };

        private static MemOp StoreOpFor(IRType type) => type is IRPointerType ? MemOp.StorePtr : type.SizeInBits switch
        {
            8 => MemOp.StoreI8,
            16 => MemOp.StoreI16,
            32 => MemOp.StoreI32,
            64 => MemOp.StoreI64,
            _ => throw new NotSupportedException(
                $"IL→MIR: mem.store of {type} is not supported yet."),
        };

        private void TranslateCall(Handle methodHandle) =>
            TranslateDirectCall(_method.DeclaringType.Assembly.ResolveMethod(methodHandle));

        /// <summary>
        /// A direct (non-virtual) call: pop the args (including `this` for instance
        /// methods), emit call.func, push the return value.
        /// </summary>
        private void TranslateDirectCall(EcmaMethod callee)
        {
            // [MethodImpl(InternalCall)] methods (e.g. PutChar) have no IL body —
            // they are provided by the hand-written runtime. Route the call to the
            // matching runtime symbol (the method name) and don't enqueue anything.
            if (callee.IsInternalCall)
            {
                if (callee.Name == "AllocateString")
                {
                    TranslateAllocateString();
                    return;
                }
                if (!_parent.IsRuntimeFunction(callee.Name))
                    throw new NotSupportedException(
                        $"IL→MIR: [MethodImpl(InternalCall)] method {callee.UniqueName} has no " +
                        $"matching runtime function @{callee.Name} in runtime.irie.");
                EmitDirectCall(callee, callee.Name);
                return;
            }

            // An ordinary method: translate it.
            _parent.EnqueueMethod(callee);
            EmitDirectCall(callee, callee.UniqueName);
        }

        /// <summary>
        /// Pop one vreg per callee parameter, emit `call.func &lt;symbolName&gt;`, and
        /// push the return value (if any). The symbol may differ from the callee's
        /// UniqueName (special-cased BCL methods, InternalCall runtime wrappers).
        /// </summary>
        private void EmitDirectCall(EcmaMethod callee, string symbolName)
        {
            var argVregs = PopCallArgs(callee);
            var returnType = ToIRType(callee.MethodSignature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            var defs = _builder.BuildCall(symbolName, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                PushTyped(defs[0], returnType, callee.MethodSignature.ReturnType);
        }

        /// <summary>
        /// Pop one vreg per callee parameter (Parameters[0] is `this` for instance
        /// methods), in reverse so the deepest stack entry maps to parameter 0.
        /// </summary>
        private int[] PopCallArgs(EcmaMethod callee) =>
            PopCallArgs([.. callee.Parameters.Select(p => ToIRType(p.Type))]);

        private int[] PopCallArgs(IRType[] parameterTypes)
        {
            var argVregs = new int[parameterTypes.Length];
            for (var i = parameterTypes.Length - 1; i >= 0; i--)
            {
                // An i1 compare result passed as a value (rather than consumed by
                // a branch) is materialised into a concrete 0/1 i8 here, via the
                // cond_br diamond. The argument's expected type is i8 (Boolean
                // maps to i8 in ToIRType), so the materialised i8 matches. An
                // argument of another width (an int32 constant for a byte
                // parameter) is converted to the parameter's width.
                argVregs[i] = Resize(PopMaterialised(), parameterTypes[i]).Vreg;
            }
            return argVregs;
        }

        /// <summary>
        /// ldftn &lt;method&gt; → push the address of the method's code as a ptr (the
        /// symbol the direct-call path would call). The method is enqueued so it is
        /// compiled; ldftn'd methods follow the same ABI as vtable slots, so they
        /// are valid call.indirect targets as they stand.
        /// </summary>
        private void TranslateLdftn(Handle methodHandle)
        {
            var method = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);
            string symbol;
            if (method.IsInternalCall)
            {
                if (!_parent.IsRuntimeFunction(method.Name))
                    throw new NotSupportedException(
                        $"IL→MIR: [MethodImpl(InternalCall)] method {method.UniqueName} has no " +
                        $"matching runtime function @{method.Name} in runtime.irie.");
                symbol = method.Name;
            }
            else
            {
                _parent.EnqueueMethod(method);
                symbol = method.UniqueName;
            }
            _stack.Push(new StackValue(_builder.BuildMemSymbol(symbol), IRType.Pointer));
        }

        /// <summary>
        /// calli &lt;signature&gt; → an indirect call through the function pointer on top
        /// of the stack, args below it. The call uses CC_MOS whatever standard
        /// convention the signature names: the target has a single ABI, so cdecl /
        /// stdcall / fastcall / thiscall / managed all collapse to it (as on x64 /
        /// arm64, and as clang does for MOS). A target-specific convention is named by
        /// a `System.Runtime.CompilerServices.CallConv&lt;Name&gt;` modifier on the return
        /// type (C# `delegate* unmanaged[&lt;Name&gt;]&lt;…&gt;`). Varargs and generic signatures
        /// are rejected.
        /// </summary>
        private void TranslateCalli(EntityHandle signatureHandle)
        {
            var assembly = _method.DeclaringType.Assembly;
            var signature = assembly.MetadataReader
                .GetStandaloneSignature((StandaloneSignatureHandle)signatureHandle)
                .DecodeMethodSignature(assembly.SignatureTypeProvider, Instantiation.Empty);

            if (signature.Header.CallingConvention == SignatureCallingConvention.VarArgs)
                throw new NotSupportedException(
                    $"IL→MIR: calli with a vararg signature is not supported (method {_method.UniqueName}).");
            if (signature.GenericParameterCount != 0)
                throw new NotSupportedException(
                    $"IL→MIR: calli with a generic signature is not supported (method {_method.UniqueName}).");

            var target = AsAddress(PopMaterialised());

            // An instance signature carries an explicit `this` below its listed
            // parameters; it is a reference, so a ptr.
            var parameterTypes = new List<IRType>();
            if (signature.Header.IsInstance) parameterTypes.Add(IRType.Pointer);
            parameterTypes.AddRange(signature.ParameterTypes.Select(ToIRType));
            var argVregs = PopCallArgs([.. parameterTypes]);

            var returnType = ToIRType(signature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            var convention = GetCalliConvention(assembly, signatureHandle);
            var defs = _builder.BuildCallIndirect(target.Vreg, convention, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                PushTyped(defs[0], returnType, signature.ReturnType);
        }

        /// <summary>
        /// The Irie calling convention a calli signature asks for: the first return
        /// type modifier named CallConv&lt;Name&gt; that Irie knows, else the default.
        /// </summary>
        private static int GetCalliConvention(EcmaAssembly assembly, EntityHandle signatureHandle)
        {
            var reader = assembly.MetadataReader;
            var blob = reader.GetBlobReader(
                reader.GetStandaloneSignature((StandaloneSignatureHandle)signatureHandle).Signature);
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric) blob.ReadCompressedInteger();
            blob.ReadCompressedInteger(); // parameter count

            // The return type starts with its custom modifiers (modopt 0x20, modreq 0x1F).
            while (blob.RemainingBytes > 0 && blob.ReadByte() is var b && (b is 0x1F or 0x20))
            {
                var modifier = blob.ReadTypeHandle();
                var name = modifier.Kind switch
                {
                    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)modifier).Name),
                    HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)modifier).Name),
                    _ => "",
                };
                if (name == "CallConvBbcMicroOS")
                    return CallConvention.BbcMicroOS;
                if (name == "CallConvC64Kernal")
                    return CallConvention.C64Kernal;
            }
            return CallConvention.Default;
        }

        /// <summary>
        /// callvirt → a direct call when the target is non-virtual (C# emits
        /// callvirt for every instance call, including non-virtual ones, for its
        /// null-check semantics); otherwise a vtable dispatch:
        ///   %vtable : ptr = mem.load.ptr (this - 2)     ; header holds vtable ptr
        ///   %fptr   : ptr = mem.load.ptr (%vtable + slot*2)
        ///   call.indirect %fptr, this, args…
        /// </summary>
        private void TranslateCallvirt(Handle methodHandle)
        {
            var callee = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);

            // A non-virtual method, a final one, or one of a sealed type (a string has
            // no vtable to dispatch through) has exactly one possible target.
            if (!callee.IsVirtual || callee.IsFinal || callee.DeclaringType.IsSealed)
            {
                TranslateDirectCall(callee);
                return;
            }

            var argVregs = PopCallArgs(callee);
            var thisPtr = argVregs[0];

            var returnType = ToIRType(callee.MethodSignature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            // The object header (the vtable pointer) sits PointerSize bytes below
            // the object reference.
            var headerAddr = EmitPointerSub(thisPtr, PointerSize);
            var vtablePtr = _function.CreateVirtualRegister(IRType.Pointer);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadPtr),
                new VirtualReg(vtablePtr, IsDefinition: true),
                new VirtualReg(headerAddr, IsDefinition: false));

            var slotIndex = _parent.GetVtableSlotIndex(callee);
            var slotAddr = EmitFieldAddress(vtablePtr, VtableHeaderSize + slotIndex * PointerSize);
            var fptr = _function.CreateVirtualRegister(IRType.Pointer);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadPtr),
                new VirtualReg(fptr, IsDefinition: true),
                new VirtualReg(slotAddr, IsDefinition: false));

            var defs = _builder.BuildCallIndirect(fptr, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                PushTyped(defs[0], returnType, callee.MethodSignature.ReturnType);
        }

        /// <summary>
        /// newobj &lt;ctor&gt; → allocate the object then run its constructor:
        ///   %ptr = call.func @ManagedHeap_Alloc(&lt;instanceSize&gt;, &amp;Vtable_&lt;type&gt;)
        ///   call.func @&lt;ctor&gt;(%ptr, args…)
        /// and push %ptr. ManagedHeap.Alloc reserves a 2-byte header (the vtable
        /// pointer) and returns a pointer to the object contents.
        /// </summary>
        private void TranslateNewobj(EntityHandle methodHandle)
        {
            var constructor = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);
            var type = constructor.DeclaringType;
            _parent.MarkTypeUsed(type);

            // Constructor args, excluding the `this` at Parameters[0] (we supply
            // the freshly-allocated pointer for that). Popped before the Alloc so
            // they are evaluated in IL order; the register allocator spills any
            // that turn out to be live across the Alloc / ctor calls.
            var userArgCount = constructor.Parameters.Length - 1;
            var userArgs = new int[userArgCount];
            for (var i = userArgCount - 1; i >= 0; i--)
            {
                userArgs[i] = Resize(PopMaterialised(), ToIRType(constructor.Parameters[i + 1].Type)).Vreg;
            }

            var size = _builder.BuildConstant(IRType.I16, type.InstanceSize);
            var vtablePtr = _builder.BuildMemSymbol(IlToMirTranslator.VtableName(type));
            var allocMethod = _parent.GetAllocMethod();
            _parent.EnqueueMethod(allocMethod);
            var ptr = _builder.BuildCall(allocMethod.UniqueName, [IRType.Pointer], size, vtablePtr)[0];

            _parent.EnqueueMethod(constructor);
            var ctorArgs = new int[userArgCount + 1];
            ctorArgs[0] = ptr;
            Array.Copy(userArgs, 0, ctorArgs, 1, userArgCount);
            _builder.BuildCall(constructor.UniqueName, [], ctorArgs);

            _stack.Push(new StackValue(ptr, IRType.Pointer));
        }

        /// <summary>
        /// The IR type and signedness of a value type that lives in a register: a
        /// primitive (a struct in DotNetro.CoreLib, named for it) or an enum (its
        /// underlying integer). Any other value type is a struct, which on the eval
        /// stack is the address of its bytes.
        /// </summary>
        private static bool TryGetScalar(EcmaType type, out IRType irType, out bool signed)
        {
            irType = IRType.Void;
            signed = false;

            if (type.IsEnum)
            {
                var underlying = type.EnumUnderlyingType;
                irType = ToIRType(underlying);
                signed = IsSignedClr(underlying);
                return true;
            }

            if (type.Assembly != type.Context.CoreAssembly || type.TypeDefinition.Namespace.IsNil
                || type.FullName is not ("System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16"
                    or "System.UInt16" or "System.Char" or "System.Int32" or "System.UInt32" or "System.Int64"
                    or "System.UInt64" or "System.IntPtr" or "System.UIntPtr"))
                return false;

            irType = type.Size switch { 1 => IRType.I8, 2 => IRType.I16, 4 => IRType.I32, _ => IRType.I64 };
            signed = type.FullName is "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64";
            return true;
        }

        /// <summary>
        /// box &lt;T&gt; → a heap object holding a copy of the value: the vtable of T in the
        /// header, the value as the contents. Boxing a reference type does nothing.
        ///   %obj = call.func @ManagedHeap_Alloc(sizeof(T), &amp;Vtable_&lt;T&gt;)
        ///   store the value (or copy the struct) into %obj
        /// </summary>
        private void TranslateBox(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            if (!type.IsValueType)
                return;

            _parent.MarkTypeUsed(type);

            var value = PopMaterialised();
            var size = _builder.BuildConstant(IRType.I16, type.InstanceSize);
            var vtablePtr = _builder.BuildMemSymbol(IlToMirTranslator.VtableName(type));
            var allocMethod = _parent.GetAllocMethod();
            _parent.EnqueueMethod(allocMethod);
            var ptr = _builder.BuildCall(allocMethod.UniqueName, [IRType.Pointer], size, vtablePtr)[0];

            if (TryGetScalar(type, out var irType, out _))
            {
                _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(irType)),
                    new VirtualReg(ptr, IsDefinition: false),
                    new VirtualReg(Resize(value, irType).Vreg, IsDefinition: false));
            }
            else
            {
                // A struct on the eval stack is its address.
                EmitStructCopy(ptr, value.Vreg, type.InstanceSize);
            }

            _stack.Push(new StackValue(ptr, IRType.Pointer));
        }

        /// <summary>
        /// unbox.any &lt;T&gt; → for a reference type a castclass; for a value type, check that
        /// the object is a boxed T and read the value out (a struct stays in the box and
        /// is pushed as its address, like any struct on the stack).
        /// </summary>
        private void TranslateUnboxAny(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            TranslateTypeTest(type, "CastClass");
            if (!type.IsValueType || !TryGetScalar(type, out var irType, out var signed))
                return;

            var address = _stack.Pop();
            var value = _function.CreateVirtualRegister(irType);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(irType)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(address.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(value, irType, signed));
        }

        private void TranslateTypeTest(EntityHandle typeHandle, string helperName) =>
            TranslateTypeTest(_method.DeclaringType.Assembly.ResolveType(typeHandle), helperName);

        /// <summary>
        /// isinst / castclass &lt;T&gt;: the object (or null) if it is a T, else null (isinst) or
        /// a halt (castclass; there are no exceptions yet). The test is TypeCheck's walk
        /// of the object's vtable chain looking for T's vtable. Everything is a
        /// System.Object, so that needs no test; a string has no vtable, which the
        /// helper takes a null target to mean.
        /// </summary>
        private void TranslateTypeTest(EcmaType type, string helperName)
        {
            if (type.IsInterface)
                throw new NotSupportedException(
                    $"IL→MIR: {helperName} against the interface {type.FullName} is not supported yet (method {_method.UniqueName}).");

            var isCore = type.Assembly == type.Context.CoreAssembly;
            if (isCore && type.FullName == "System.Object")
                return;

            var obj = AsAddress(_stack.Pop());
            int target;
            if (isCore && type.FullName == "System.String")
            {
                target = BuildZero(_builder, IRType.Pointer);
            }
            else
            {
                _parent.RequireVtable(type);
                target = _builder.BuildMemSymbol(IlToMirTranslator.VtableName(type));
            }

            var helper = _parent.GetTypeCheckMethod(helperName);
            _parent.EnqueueMethod(helper);
            var result = _builder.BuildCall(helper.UniqueName, [IRType.Pointer], obj.Vreg, target)[0];
            _stack.Push(new StackValue(result, IRType.Pointer));
        }

        /// <summary>dup → re-push the top stack value (same vreg).</summary>
        private void TranslateDup() => _stack.Push(_stack.Peek());

        /// <summary>
        /// sizeof &lt;type&gt; → push the type's size as an i32 constant (matches the
        /// IL `sizeof` result type). For IntPtr this is the 16-bit pointer size.
        /// </summary>
        private void TranslateSizeof(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var vreg = _builder.BuildConstant(IRType.I32, type.Size);
            _stack.Push(new StackValue(vreg, IRType.I32));
        }

        /// <summary>
        /// ldind.* → load a value from the address on top of stack.
        /// </summary>
        /// <remarks>
        /// Stack-type note: real IL zero-extends the loaded byte to i32 on the
        /// evaluation stack. We deliberately model the result as i8, not i32, so
        /// the value flows to consumers like PutChar(byte) as a single CC_MOS byte
        /// rather than a 4-byte value. Don't be surprised by the i8 stack type —
        /// it is intentional, and matches how every current caller uses the byte
        /// (compare-to-zero, pass to an i8 parameter). Revisit only if a future
        /// caller actually needs the zero-extended i32 semantics.
        ///
        /// ldind.i1/u1 → i8, ldind.i4/u4 → i32, ldind.i → i16 (a native int),
        /// ldind.ref → ptr. `signed` is the loaded value's signedness (how an i8
        /// widens later). The address may be a native int (AsAddress).
        /// </remarks>
        private void TranslateLdind(IRType type, bool signed)
        {
            var addr = AsAddress(_stack.Pop());
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(addr.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(value, type, signed));
        }

        /// <summary>
        /// stind.i1/i4/i/ref → store the value into the pointer, truncated to the
        /// store width (the IL stack holds an int32 for stind.i1). Stack layout
        /// (top first): value, then the destination pointer.
        /// </summary>
        private void TranslateStind(IRType type)
        {
            var value = Resize(PopMaterialised(), type);
            var addr = AsAddress(_stack.Pop());
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr.Vreg, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        // Arrays are heap objects: the reference points at a 2-byte length, the
        // elements follow it, and the (null) vtable slot sits below it like any
        // object header. No bounds checks are emitted. (ArrayLengthSize is shared
        // with string objects, which have the same layout.)

        /// <summary>
        /// Size in bytes of an array element of the type named by an IL token. The
        /// primitive element types resolve to the structs in DotNetro.CoreLib, whose
        /// single value field gives them their size; reference types are pointers.
        /// </summary>
        private int ElementSize(EntityHandle typeHandle) =>
            _method.DeclaringType.Assembly.ResolveType(typeHandle).Size;

        /// <summary>newarr &lt;T&gt; → %arr = call @ManagedHeap_AllocArray(length, length * sizeof(T))</summary>
        private void TranslateNewarr(EntityHandle typeHandle)
        {
            var elementSize = ElementSize(typeHandle);
            var length = Resize(PopMaterialised(), IRType.I16).Vreg;
            EmitAllocArray(length, EmitScaledIndex(length, elementSize));
        }

        /// <summary>
        /// String.AllocateString(length) (an InternalCall implemented here, like
        /// CoreCLR's FastAllocateString): a string is an array of `length` bytes plus
        /// a NUL terminator, and its reference is the pointer AllocArray returns.
        /// </summary>
        private void TranslateAllocateString()
        {
            var length = Resize(PopMaterialised(), IRType.I16).Vreg;
            var one = _builder.BuildConstant(IRType.I16, 1);
            var contentSize = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(contentSize, IsDefinition: true),
                new VirtualReg(length, IsDefinition: false),
                new VirtualReg(one, IsDefinition: false));
            EmitAllocArray(length, contentSize);
        }

        /// <summary>Call ManagedHeap.AllocArray and push the resulting reference.</summary>
        private void EmitAllocArray(int length, int contentSize)
        {
            var allocArray = _parent.GetAllocArrayMethod();
            _parent.EnqueueMethod(allocArray);
            var ptr = _builder.BuildCall(allocArray.UniqueName, [IRType.Pointer], length, contentSize)[0];
            _stack.Push(new StackValue(ptr, IRType.Pointer));
        }

        /// <summary>
        /// ldlen → the i16 length stored at the array reference (a native unsigned
        /// int; the usual following conv.i4 zero-extends it).
        /// </summary>
        private void TranslateLdlen()
        {
            var array = AsAddress(_stack.Pop());
            var length = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(length, IsDefinition: true),
                new VirtualReg(array.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(length, IRType.I16));
        }

        /// <summary>
        /// index * elementSize as an i16 (1 reuses the index; a power of two is
        /// strength-reduced to a shift by ArithSimplify, anything else is a
        /// multiply).
        /// </summary>
        private int EmitScaledIndex(int indexVreg, int elementSize)
        {
            if (elementSize == 1) return indexVreg;
            var sizeConst = _builder.BuildConstant(IRType.I16, elementSize);
            var scaled = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.MulI),
                new VirtualReg(scaled, IsDefinition: true),
                new VirtualReg(indexVreg, IsDefinition: false),
                new VirtualReg(sizeConst, IsDefinition: false));
            return scaled;
        }

        /// <summary>
        /// Pops the index and array reference (top first) and returns the address
        /// of that element: array + ArrayLengthSize + index * elementSize.
        /// </summary>
        private int PopElementAddress(int elementSize)
        {
            var index = Resize(PopMaterialised(), IRType.I16).Vreg;
            var array = AsAddress(_stack.Pop());
            var offset = EmitScaledIndex(index, elementSize);
            var elements = EmitFieldAddress(array.Vreg, ArrayLengthSize);
            return EmitPtrAdd(elements, offset);
        }

        private void TranslateLdelema(EntityHandle typeHandle)
        {
            var addr = PopElementAddress(ElementSize(typeHandle));
            _stack.Push(new StackValue(addr, IRType.Pointer));
        }

        private void TranslateLdelem(IRType type, bool signed)
        {
            var addr = PopElementAddress(type.SizeInBits / 8);
            _stack.Push(new StackValue(addr, IRType.Pointer));
            TranslateLdind(type, signed);
        }

        /// <summary>
        /// ldelem &lt;T&gt;: only primitive and reference element types (whose size is
        /// 1, 2 or 4 bytes) can be loaded as a single value.
        /// </summary>
        private void TranslateLdelem(EntityHandle typeHandle)
        {
            var elementType = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var type = ElementIRType(elementType, "ldelem");
            var signed = elementType.Assembly.Context.CoreAssembly == elementType.Assembly
                && elementType.FullName is "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64";
            TranslateLdelem(type, signed);
        }

        /// <summary>
        /// The IR type an `ldelem &lt;T&gt;` / `stelem &lt;T&gt;` token moves: a ptr for a
        /// reference-type element, otherwise the integer of the element's size.
        /// Only elements of 1, 2, 4 or 8 bytes can be moved as a single value.
        /// </summary>
        private IRType ElementIRType(EcmaType elementType, string opName)
        {
            if (IsReferenceLike(elementType)) return IRType.Pointer;

            return elementType.Size switch
            {
                1 => IRType.I8,
                2 => IRType.I16,
                4 => IRType.I32,
                8 => IRType.I64,
                _ => throw new NotSupportedException(
                    $"IL→MIR: {opName} of a {elementType.Size}-byte element is not supported yet."),
            };
        }

        /// <summary>stelem.*: stack (top first) is value, index, array.</summary>
        private void TranslateStelem(IRType type)
        {
            var value = _stack.Pop();
            var addr = PopElementAddress(type.SizeInBits / 8);
            _stack.Push(new StackValue(addr, IRType.Pointer));
            _stack.Push(value);
            TranslateStind(type);
        }

        private void TranslateStelem(EntityHandle typeHandle)
        {
            TranslateStelem(ElementIRType(_method.DeclaringType.Assembly.ResolveType(typeHandle), "stelem"));
        }

        /// <summary>
        /// Address `offset` bytes *below* a pointer (e.g. the object header at
        /// this-2): a mem.ptradd of the negated constant offset.
        /// </summary>
        private int EmitPointerSub(int baseVreg, int offset) =>
            EmitFieldAddress(baseVreg, -offset);

        private void TranslateRet()
        {
            if (_function.ReturnType is VoidType)
            {
                _builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return));
                return;
            }

            var value = Resize(PopMaterialised(), _function.ReturnType);
            _builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        private static ILOpCode ReadOpCode(ref BlobReader ilReader)
        {
            var opCodeByte = ilReader.ReadByte();
            return (ILOpCode)(opCodeByte == 0xFE
                ? 0xFE00 + ilReader.ReadByte()
                : opCodeByte);
        }
    }

    /// <summary>
    /// A value on the IL evaluation stack. Narrow integers stay narrow (a byte is
    /// i8, not the int32 ECMA-335 would widen it to), so byte-sized code stays
    /// byte-sized; IsSigned records whether an i8 came from a signed source
    /// (sbyte, int, conv.i1, conv.i4), which decides sign- vs zero-extension when it
    /// later meets a wider operand (see Resize / UnifyWidths).
    /// </summary>
    /// <remarks>
    /// A heap reference, `T*` or `ref T` has the ptr type; native int (IntPtr,
    /// conv.i) is an i16 integer, converted to / from ptr where IL uses it as an
    /// address (see AsAddress / AsInteger).
    /// </remarks>
    private readonly record struct StackValue(
        int Vreg, IRType Type, bool IsSigned = false)
    {
        public bool IsPointer => Type is IRPointerType;
    }
}
