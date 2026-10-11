using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using System.Text;

using DotNetro.Compiler.TypeSystem;

using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Dialects.Mem;
using Irie.Mir;

namespace DotNetro.Compiler;

// Walks the same IL as DotNetCompiler but, instead of emitting 6502 assembly
// text, builds an Irie MirModule via MirBuilder. The module is then handed to
// the Irie MOS6502 pass pipeline (see CompilerDriver) to produce program bytes.
//
// This is the step-7/8 skeleton: only the IL ops required for Print42 and
// AddInt32 are implemented. Unimplemented ops throw NotSupportedException so the
// gaps are visible as the translator grows (plan step 9).
internal sealed class IlToMirTranslator : IDisposable
{
    private readonly TypeSystemContext _typeSystemContext;
    private readonly EcmaAssembly _rootAssemblyContext;

    private readonly HashSet<EcmaMethod> _visitedMethods = [];
    private readonly Queue<EcmaMethod> _methodsToVisit = new();

    // Names of functions supplied by the hand-written runtime; the BFS walk must
    // not try to translate (or recurse into) these — they already exist as MIR.
    private readonly HashSet<string> _runtimeFunctionNames = [];

    private readonly string _runtimeText;

    // Module-level globals (string constants, static fields) collected during
    // the IL walk and appended to the module once translation completes. Keyed
    // by symbol name so the same string / static field is registered only once.
    private readonly Dictionary<string, MirGlobal> _globals = [];

    // Reference types newobj'd by the program. Each gets a `Vtable_<type>`
    // global; the object header (at obj-2) points at it.
    private readonly HashSet<EcmaType> _usedTypes = [];

    // Cached vtable layouts (slot index → method to dispatch). See GetVtableLayout.
    private readonly Dictionary<EcmaType, List<EcmaMethod>> _vtableLayouts = [];

    // Types whose static fields are touched (ldsfld/stsfld); their `.cctor`
    // must run at startup so the field has its initial value (e.g. ManagedHeap's
    // s_heapPtr = 0x1000). Static constructors discovered, in call order.
    private readonly HashSet<EcmaType> _staticFieldOwners = [];
    private readonly List<EcmaMethod> _staticConstructors = [];

    // Lazily-resolved ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr,
    // the runtime allocator newobj calls. Kept in C# and translated like any
    // other method (plan open-question 3 — the translated path).
    private EcmaMethod? _allocMethod;
    private EcmaMethod? _allocArrayMethod;

    // Lazily-resolved ConsoleHelper.WriteLineString(byte*); see GetWriteLineStringMethod.
    private EcmaMethod? _writeLineStringMethod;

    // Methods that must be emitted under a fixed MIR function name rather than
    // their UniqueName. Currently just the C# WriteLineString helper, pinned to
    // "WriteLineString" so the hand-written runtime can keep calling it by name.
    private readonly Dictionary<EcmaMethod, string> _emitNameOverrides = [];

    // Pointer size is 16-bit on the 6502.
    private const int PointerSize = 2;

    public IlToMirTranslator(string rootAssemblyPath, string runtimeText)
    {
        _runtimeText = runtimeText;
        _typeSystemContext = new TypeSystemContext(PointerSize);
        _rootAssemblyContext = _typeSystemContext.ResolveAssembly(rootAssemblyPath);
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

        // The whole hand-written runtime is always linked in (see EnsureRuntime
        // globals below), and @WriteLineBoolean unconditionally tail-calls
        // @WriteLineString. Since @WriteLineString is now the translated C# helper
        // rather than a runtime.irie function, enqueue it unconditionally so the
        // symbol is always defined even when the program never calls
        // Console.WriteLine(string) directly.
        EnqueueMethod(GetWriteLineStringMethod());

        do
        {
            while (_methodsToVisit.Count > 0)
                module.Functions.Add(TranslateMethod(_methodsToVisit.Dequeue()));

            EnqueueVtableSlotMethods();
            EnqueueStaticConstructors();
        } while (_methodsToVisit.Count > 0);

        // The entry function must lay out first (at `origin`) so the emulator's
        // `JSR $2000` lands on it. It performs BBC MODE 7 init, runs the static
        // constructors, calls Main, RTS — so it is built after the walk, once the
        // static-constructor list is known.
        module.Functions.Insert(0, BuildEntryFunction(entryPointMethod));

        // Append the runtime functions (oswrch, osasci, WriteLineInt32, …).
        module.Functions.AddRange(runtimeModule.Functions);

        // The whole runtime is always linked in, so the hand-written helpers
        // that reference module globals (@WriteLineBoolean → @True_String /
        // @False_String, @Beep → @Sound) are always present even when unused.
        // Register those globals unconditionally so the encoder can always
        // resolve the symbols.
        EnsureRuntimeGlobals();

        // Vtables for every newobj'd type (registered after the walk, once all
        // used types and their dispatch targets are known).
        RegisterVtableGlobals();

        // Append the string constants / static fields / vtables collected.
        module.Globals.AddRange(_globals.Values);

        return module;
    }

    internal void MarkTypeUsed(EcmaType type) => _usedTypes.Add(type);

    // ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr. Resolved on
    // first newobj; kept in C# and translated through the pipeline.
    internal EcmaMethod GetAllocMethod()
    {
        if (_allocMethod != null)
            return _allocMethod;

        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        _allocMethod = managedHeapType.GetMethod("Alloc",
            new MethodSignature<TypeDescription>(
                new SignatureHeader(), _typeSystemContext.IntPtr, 2, 0,
                [_typeSystemContext.IntPtr, _typeSystemContext.IntPtr]));
        return _allocMethod;
    }

    // ManagedHeap.AllocArray(IntPtr length, IntPtr contentSize) → IntPtr. Resolved
    // on first newarr.
    internal EcmaMethod GetAllocArrayMethod()
    {
        if (_allocArrayMethod != null)
            return _allocArrayMethod;

        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        _allocArrayMethod = managedHeapType.GetMethod("AllocArray",
            new MethodSignature<TypeDescription>(
                new SignatureHeader(), _typeSystemContext.IntPtr, 2, 0,
                [_typeSystemContext.IntPtr, _typeSystemContext.IntPtr]));
        return _allocArrayMethod;
    }

    // The software multiply / divide / remainder helpers in DotNetro.Runtime.MathHelper,
    // which Irie's legalizer calls by these stable names (compiler-rt naming). Each
    // is emitted under its own name so the call the legalizer builds links.
    private static readonly string[] MathHelperNames =
        [
            "__mulhi3", "__mulsi3", "__udivsi3", "__divsi3", "__umodsi3", "__modsi3",
            "__muldi3", "__udivdi3", "__divdi3", "__umoddi3", "__moddi3",
        ];

    private bool _mathHelpersRegistered;

    // Link in math helper `name`: pin every helper's emitted name (the helpers call
    // each other, and calls resolve names through GetEmitNameOverride), then
    // enqueue the one the program needs. Demand-driven so a program with no
    // general multiply or divide carries none of this code.
    internal void RequireMathHelper(string name)
    {
        var mathHelperType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "MathHelper");
        if (!_mathHelpersRegistered)
        {
            _mathHelpersRegistered = true;
            foreach (var helperName in MathHelperNames)
                _emitNameOverrides[mathHelperType.GetMethod(helperName)] = helperName;
        }
        EnqueueMethod(mathHelperType.GetMethod(name));
    }

    // The pinned MIR name for `method`, or null when it is emitted under its UniqueName.
    internal string? GetEmitNameOverride(EcmaMethod method) =>
        _emitNameOverrides.TryGetValue(method, out var pinned) ? pinned : null;

    // True if `name` matches a function supplied by the hand-written runtime
    // (oswrch, osasci, WriteLineInt32, …). Used to validate InternalCall routing.
    internal bool IsRuntimeFunction(string name) => _runtimeFunctionNames.Contains(name);

    // ConsoleHelper.WriteLineString(byte*) → void, the C# replacement for the old
    // hand-written @WriteLineString MIR. Resolved on first Console.WriteLine(string)
    // and translated through the pipeline like any other method, but emitted under
    // the stable MIR name "WriteLineString" so the runtime @WriteLineBoolean (which
    // tail-calls @WriteLineString) still links. See _emitNameOverrides.
    internal EcmaMethod GetWriteLineStringMethod()
    {
        if (_writeLineStringMethod != null)
            return _writeLineStringMethod;

        var consoleHelperType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ConsoleHelper");
        _writeLineStringMethod = consoleHelperType.GetMethod("WriteLineString");
        _emitNameOverrides[_writeLineStringMethod] = "WriteLineString";
        return _writeLineStringMethod;
    }

    // The dispatch table for `type`: slot index → the method that index resolves
    // to for an instance of `type`. Built base-first (so inherited slots keep
    // their index) then extended with `type`'s own NewSlot virtuals; overrides
    // replace the inherited slot in place.
    //
    // Recursion stops at the root (BaseType == null, i.e. System.Object): the
    // BCL's virtuals (ToString/Equals/…) are never dispatched by the supported
    // corpus, so they get no slots. This also makes slot indices a pure function
    // of metadata — independent of which methods turn out to be "used" — so the
    // translator can emit the right offset at each callvirt without a whole-
    // program fixpoint over the used set.
    private List<EcmaMethod> GetVtableLayout(EcmaType type)
    {
        if (_vtableLayouts.TryGetValue(type, out var cached))
            return cached;

        // The root (System.Object, BaseType == null) contributes no slots: its
        // virtuals (Equals/GetHashCode/ToString/…) are never dispatched by the
        // supported corpus, and enumerating them would pull their (unsupported)
        // bodies into the program. Stopping here also makes slot indices a pure
        // function of metadata, independent of which methods turn out "used".
        List<EcmaMethod> slots;
        if (type.BaseType == null)
        {
            slots = [];
        }
        else
        {
            slots = new List<EcmaMethod>(GetVtableLayout(type.BaseType));

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
                            $"IL→MIR: override {method.UniqueName} has no user vtable slot " +
                            $"(overriding a BCL virtual method is not supported yet).");
                    slots[index] = method;
                }
            }
        }

        _vtableLayouts[type] = slots;
        return slots;
    }

    // The slot index a callvirt of `callee` reads from the object's vtable.
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

    // Enqueue every dispatch target reachable through a used type's vtable so the
    // override bodies (e.g. Derived.MyMethod) are translated, not just the
    // statically-resolved callvirt callee.
    private void EnqueueVtableSlotMethods()
    {
        foreach (var type in _usedTypes.ToArray())
            foreach (var method in GetVtableLayout(type))
                EnqueueMethod(method);
    }

    // Enqueue the `.cctor` of each type whose static fields were touched, and
    // record it (in discovery order) so __entry can call them before Main.
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

    // Emit a `Vtable_<type>` global per used type: a packed array of 2-byte
    // little-endian function pointers, one per dispatch slot (plain pointers —
    // no RTS-trick offset; the indirect-call trampoline does JMP (zp)).
    // Symbol name for a type's vtable global. Plain pointers, no RTS-trick offset.
    private static string VtableName(EcmaType type) => $"Vtable_{type.EncodedName}";

    private void RegisterVtableGlobals()
    {
        foreach (var type in _usedTypes)
        {
            var name = VtableName(type);
            if (_globals.ContainsKey(name))
                continue;

            var slots = GetVtableLayout(type);
            var items = slots
                .Select(method => (MirDataItem)new DataSymbolRef(method.UniqueName))
                .ToArray();

            _globals[name] = new MirGlobal(
                SymbolName:  name,
                Type:        IRType.Pointer,
                SizeInBytes: slots.Count * PointerSize,
                Initializer: new MirInitializer(items));
        }
    }

    // Intern a string constant as a module-level MirGlobal and return its
    // symbol name. Mirrors DotNetCompiler.CompileLdstr: the key is
    // `string{token:X8}` and the payload is the ASCII bytes followed by CR
    // (0x0D) then NUL (0x00) — i.e. the legacy `.cstring "...", 13` format.
    // @WriteLineString loops over the bytes calling osasci (which expands CR to
    // CR/LF) until it reaches the NUL terminator.
    private string RegisterStringGlobal(string stringKey, string value)
    {
        if (!_globals.ContainsKey(stringKey))
        {
            var ascii = Encoding.ASCII.GetBytes(value);
            var bytes = new byte[ascii.Length + 2];
            Array.Copy(ascii, bytes, ascii.Length);
            bytes[ascii.Length] = 0x0D;     // CR, matching `.cstring "...", 13`
            bytes[ascii.Length + 1] = 0x00; // NUL terminator

            _globals[stringKey] = new MirGlobal(
                SymbolName:  stringKey,
                Type:        IRType.Pointer,
                SizeInBytes: bytes.Length,
                Initializer: new MirInitializer([new DataBytes(bytes)]));
        }

        return stringKey;
    }

    // The runtime @WriteLineBoolean references two fixed string globals,
    // @True_String / @False_String, holding "True" / "False" in the same
    // CR+NUL format @WriteLineString consumes. They are registered here (rather
    // than declared in runtime.irie) so the exact byte payload and size match
    // every other interned string. Registered on demand the first time a
    // Console.WriteLine(bool) call is translated.
    private const string TrueStringSymbol = "True_String";
    private const string FalseStringSymbol = "False_String";
    private const string SoundSymbol = "Sound";

    // The OSWORD 7 (SOUND) control block @Beep points X/Y at: four little-endian
    // 16-bit fields — channel = 1, amplitude = -15, pitch = 0, duration = 4.
    // Byte-for-byte identical to the legacy generator's `.word`/`.short` block.
    private static readonly byte[] SoundControlBlock =
        [0x01, 0x00, 0xF1, 0xFF, 0x00, 0x00, 0x04, 0x00];

    private void EnsureRuntimeGlobals()
    {
        RegisterStringGlobal(TrueStringSymbol, "True");
        RegisterStringGlobal(FalseStringSymbol, "False");

        if (!_globals.ContainsKey(SoundSymbol))
        {
            _globals[SoundSymbol] = new MirGlobal(
                SymbolName:  SoundSymbol,
                Type:        IRType.Pointer,
                SizeInBytes: SoundControlBlock.Length,
                Initializer: new MirInitializer([new DataBytes(SoundControlBlock)]));
        }
    }

    // Register a static field as a zero-initialized (.bss-style) MirGlobal and
    // return its symbol name. Mirrors DotNetCompiler's GetStaticFieldName:
    // `{field.Owner.EncodedName}_{field.Name}`.
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

    // Synthesize the program entry point:
    //   func @__entry : () -> void {
    //     call.func @oswrch(22)   ; MODE 7
    //     call.func @oswrch(7)    ; MODE 7
    //     call.func @<Main>()
    //     core.return
    //   }
    private MirFunction BuildEntryFunction(EcmaMethod entryPointMethod)
    {
        var function = new MirFunction("__entry", [], IRType.Void);
        var entryBlock = function.CreateBlock();
        var builder = new MirBuilder(function);
        builder.SetInsertionPointAtEnd(entryBlock);

        EmitModeSeven(function, builder);

        // Run static constructors before Main (e.g. ManagedHeap's s_heapPtr =
        // 0x1000). They are void and parameterless (.cctor).
        foreach (var cctor in _staticConstructors)
            builder.BuildCall(cctor.UniqueName, []);

        // Top-level statements compile to `<Main>$(string[] args)`. The args
        // array is unused by the programs we support, but the call's arity must
        // still match the callee, so pass a null (constant-0) pointer per param.
        var argVregs = new int[entryPointMethod.Parameters.Length];
        for (var i = 0; i < entryPointMethod.Parameters.Length; i++)
        {
            var type = ToIRType(entryPointMethod.Parameters[i].Type);
            argVregs[i] = builder.BuildConstant(type, 0);
        }

        builder.BuildCall(entryPointMethod.UniqueName, [], argVregs);

        builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return));
        return function;
    }

    private static void EmitModeSeven(MirFunction function, MirBuilder builder)
    {
        // VDU 22, 7 selects MODE 7 on the BBC Micro. oswrch takes a single i8.
        EmitOswrch(function, builder, 22);
        EmitOswrch(function, builder, 7);
    }

    private static void EmitOswrch(MirFunction function, MirBuilder builder, long value)
    {
        var c = builder.BuildConstant(IRType.I8, value);
        builder.BuildCall("oswrch", [], c);
    }

    private MirFunction TranslateMethod(EcmaMethod method)
    {
        var paramTypes = method.Parameters
            .Select(p => ToIRType(p.Type))
            .ToArray();
        var returnType = ToIRType(method.MethodSignature.ReturnType);

        var name = _emitNameOverrides.TryGetValue(method, out var pinned) ? pinned : method.UniqueName;
        var function = new MirFunction(name, paramTypes, returnType);
        var methodTranslator = new MethodTranslator(this, method, function);
        methodTranslator.Translate();
        return function;
    }

    // Whether a CLR type is a signed integer (so a value of it sign-extends when
    // widened). Native int / pointers are deliberately treated as unsigned:
    // they hold addresses.
    private static bool IsSignedClr(TypeDescription type) =>
        type is PrimitiveType
        {
            PrimitiveTypeCode: PrimitiveTypeCode.SByte or PrimitiveTypeCode.Int16
                or PrimitiveTypeCode.Int32 or PrimitiveTypeCode.Int64
        };

    // Whether values of a CLR type are addresses: every i16-represented type
    // except the 16-bit integers.
    private static bool IsPointerClr(TypeDescription type) =>
        ToIRType(type).Equals(IRType.I16)
        && type is not PrimitiveType
        {
            PrimitiveTypeCode: PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16
                or PrimitiveTypeCode.Char
        };

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
                // A managed string / object is a heap reference: a 16-bit pointer
                // on the 6502 (the pointer @ReadLine returns / @WriteLineString
                // consumes; the `this` of System.Object::.ctor, base-called by
                // every user constructor).
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => IRType.I16,
                _ => throw new NotSupportedException(
                    $"IL→MIR: primitive type {primitive.PrimitiveTypeCode} is not supported yet."),
            };
        }

        // Reference types (including arrays) are heap pointers: 16-bit on the
        // 6502. A reference-type EcmaType (class) — `this`, a class-typed field,
        // a class parameter/return — is likewise a 16-bit heap pointer.
        if (type is SZArrayType or TypeSystem.PointerType or ByReferenceType
            || type is EcmaType { IsValueType: false })
            return IRType.I16;

        throw new NotSupportedException(
            $"IL→MIR: type {type} is not supported yet.");
    }

    // Translates a single method body. One instance per method so the eval stack
    // and local SSA-name maps are per-method.
    private sealed class MethodTranslator
    {
        private readonly IlToMirTranslator _parent;
        private readonly EcmaMethod _method;
        private readonly MirFunction _function;
        private readonly MirBuilder _builder;

        // Mirrors the IL evaluation stack: vreg IDs (paired with their type so
        // we can size arith ops). Runs alongside the IL semantics. Values still
        // on the stack at a block boundary (e.g. the arms of `c ? a : b`) travel
        // as extra block parameters; see _blockStackParams.
        private readonly Stack<StackValue> _stack = new();

        // Block → the block parameters carrying the eval stack on entry
        // (bottom of stack first). Created by the first edge into the block, so
        // they follow the local parameters in the block's parameter list; every
        // later edge must supply the same number of values (IL guarantees a
        // consistent stack depth at a join).
        private readonly Dictionary<MirBlock, StackValue[]> _blockStackParams = [];

        // Primitive SSA locals: local index → current vreg ID (and its type).
        // Re-seeded to the current block's local parameter vregs at each block
        // entry so reads observe the merged value flowing in along every edge.
        private readonly Dictionary<int, StackValue> _locals = [];

        // IL has no basic blocks; we discover them by pre-scanning for branch
        // targets and fall-through-after-branch points (see DiscoverBlocks).
        // Maps an IL offset that starts a block → the MirBlock for it.
        private readonly Dictionary<int, MirBlock> _ilOffsetToBlock = [];

        // The IL offsets that start a block, in ascending order. Used to find
        // the next block when emitting a fall-through terminator.
        private int[] _blockStartOffsets = [];

        // Per-local IRType (local index → type), used to size the per-block
        // local parameters and the entry zero-init constants. Only meaningful
        // for SSA (non-FrameSlot) locals; FrameSlot locals carry a placeholder.
        private IRType[] _localTypes = [];

        // Per-local CLR type and FrameSlot classification. A local is FrameSlot-
        // bound (lives in a .bss-style MirGlobal rather than an SSA vreg) when it
        // is a value-type struct or is address-taken via ldloca. Its FrameSlot's
        // Index equals the local index; ldloc/stloc/ldloca on it go through
        // memory (mem.frame_addr + mem.load/store, or a struct byte copy).
        private TypeDescription[] _localClrTypes = [];
        private bool[] _localIsFrameSlot = [];

        // Non-entry blocks carry one parameter vreg per local (in local-index
        // order) so the loop variable flows across edges as a block argument
        // (MIR's PHI substitute). Maps a block → its local parameter vregs.
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
            _localTypes = new IRType[locals.Length];
            _localClrTypes = new TypeDescription[locals.Length];
            _localIsFrameSlot = new bool[locals.Length];

            // Address-taken locals (those with an `ldloca`) and value-type
            // (struct) locals are FrameSlot-bound; everything else is a pure SSA
            // vreg. Decide upfront so ldloc/stloc/ldarg know which path to take.
            // Cross-call values flow as ordinary SSA vregs and reach the RA as
            // live-across-call values: the RA places them in callee-saved
            // RC20..RC31 (excluded from the JSR clobber set) and the
            // PrologueEpilogueInsertionPass saves/restores them.
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
                // FrameSlot locals live in memory (a zero-init .bss global), not
                // an SSA vreg — they are seeded by the program's own initobj /
                // stores, so skip the SSA zero-init for them.
                if (_localIsFrameSlot[i]) continue;
                var zero = _builder.BuildConstant(_localTypes[i], 0);
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

                    case ILOpCode.Ldnull:
                        _stack.Push(StackValue.Ptr(_builder.BuildConstant(IRType.Pointer, 0)));
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
                    case ILOpCode.Stfld:
                        TranslateStfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Initobj:
                        TranslateInitobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

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

                    case ILOpCode.Newobj:
                        TranslateNewobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Dup:
                        TranslateDup();
                        break;

                    case ILOpCode.Sizeof:
                        TranslateSizeof(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Conv_i:
                    case ILOpCode.Conv_u:
                        TranslateConv(IRType.I16, signed: false, pointer: true);
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
                    case ILOpCode.Stind_i:
                    case ILOpCode.Stind_ref:
                        TranslateStind(IRType.I16);
                        break;

                    case ILOpCode.Ldind_i1: TranslateLdind(IRType.I8, signed: true); break;
                    case ILOpCode.Ldind_u1: TranslateLdind(IRType.I8, signed: false); break;
                    case ILOpCode.Ldind_i2: TranslateLdind(IRType.I16, signed: true); break;
                    case ILOpCode.Ldind_u2: TranslateLdind(IRType.I16, signed: false); break;
                    case ILOpCode.Ldind_i4: TranslateLdind(IRType.I32, signed: true); break;
                    case ILOpCode.Ldind_u4: TranslateLdind(IRType.I32, signed: false); break;
                    case ILOpCode.Ldind_i8: TranslateLdind(IRType.I64, signed: true); break;
                    case ILOpCode.Ldind_i:
                    case ILOpCode.Ldind_ref:
                        TranslateLdind(IRType.I16, signed: false, pointer: true);
                        break;

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
                    case ILOpCode.Ldelem_i:
                    case ILOpCode.Ldelem_ref:
                        TranslateLdelem(IRType.I16, signed: false, pointer: true);
                        break;
                    case ILOpCode.Ldelem:
                        TranslateLdelem(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Stelem_i1: TranslateStelem(IRType.I8); break;
                    case ILOpCode.Stelem_i2: TranslateStelem(IRType.I16); break;
                    case ILOpCode.Stelem_i4: TranslateStelem(IRType.I32); break;
                    case ILOpCode.Stelem_i8: TranslateStelem(IRType.I64); break;
                    case ILOpCode.Stelem_i:
                    case ILOpCode.Stelem_ref:
                        TranslateStelem(IRType.I16);
                        break;
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

        // Pre-scan the IL once to find every basic-block leader: offset 0, every
        // branch target, and every instruction immediately following a branch
        // (the not-taken fall-through). A MirBlock is created per leader; the
        // map lets branch ops resolve targets, including forward references.
        private void DiscoverBlocks()
        {
            _blockStartOffsets = ComputeBlockLeaders();
            foreach (var offset in _blockStartOffsets)
                _ilOffsetToBlock[offset] = _function.CreateBlock();

            DiscoverBlocksLocalParams();
        }

        // The set of basic-block leader IL offsets (offset 0, every branch
        // target, and every instruction immediately after a branch), ascending.
        // Used both by DiscoverBlocks (to create MirBlocks) and by the cross-call
        // liveness analysis, which runs before classification (hence before the
        // MirBlocks exist).
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

        // Wire up the per-block local parameter vregs (the SSA/PHI substitute),
        // computed from local liveness. Split out of DiscoverBlocks so the
        // leader/block creation can be read on its own.
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

        // Classic backwards local-liveness fixpoint over the discovered blocks.
        // Returns, per block-start offset, the set of local indices that are
        // live on entry (read along some path before being overwritten). Used
        // to decide which locals each block carries as parameters.
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

        private static void RecordUse(int local, HashSet<int> useBeforeDef, HashSet<int> defined)
        {
            if (!defined.Contains(local))
                useBeforeDef.Add(local);
        }

        // Advance an IL reader to a given offset (block discovery helper).
        private static void AdvanceTo(ref BlobReader reader, int offset)
        {
            while (reader.Offset < offset && reader.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref reader);
                SkipBranchOrOperand(ref reader, opCode);
            }
        }

        // Like SkipOperand but also consumes branch displacement bytes (used
        // when fast-forwarding to a block start, where branches are just bytes).
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

        // On entering a discovered block, rebind every local to that block's
        // parameter vreg (the merged value) and set the builder's insertion
        // point. The eval stack becomes the block's stack parameters, if any
        // (whatever the previous block left behind has been passed on its edges).
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

        // Emit `cf.br target(localArgs)` passing the current SSA value of every
        // local as a block argument, in local-index order.
        private void EmitBranchTo(MirBlock from, MirBlock target)
        {
            _builder.SetInsertionPointAtEnd(from);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(target, EdgeArgsFor(target)));
        }

        // Block arguments for an edge to `target`: locals, then the eval stack.
        private MirOperand[] EdgeArgsFor(MirBlock target) =>
            [.. LocalArgsFor(target), .. StackArgsFor(target)];

        // The values left on the eval stack (bottom first), as block arguments
        // for `target`. The first edge into a block fixes its stack parameters'
        // types; a later edge with a different width is resized to match. Emits
        // any resize at the builder's insertion point, i.e. before the branch.
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

        // Block arguments for an edge to `target`: the current value of each
        // live-in local, in ascending local-index order (matching the order
        // the target declared its parameters in DiscoverBlocks).
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

        // Advance a scanning reader past an opcode's inline operand bytes during
        // block discovery (we only care about branch displacements there).
        private static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
        {
            switch (opCode)
            {
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s:
                case ILOpCode.Stloc_s: case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s:
                case ILOpCode.Starg_s:
                    reader.ReadByte();
                    break;
                case ILOpCode.Ldc_i4: case ILOpCode.Call: case ILOpCode.Callvirt:
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

        // The block currently being emitted into. Normally tracks the IL
        // basic-block leader, but a value-used compare (TranslateCompare's diamond)
        // splits the current block and redirects this to the merge block so
        // subsequent IL emits after the materialised 0/1 result.
        private MirBlock _currentBlock = null!;

        // The IL basic block being translated (the block that starts at a leader
        // offset). Unlike _currentBlock it is never one of the synthetic blocks
        // (compare diamonds, switch tests), so it is what a conditional branch's
        // fall-through successor is found from.
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

        // add / sub / mul / div / rem: the integer binary ops whose result depends on
        // the bits above the operands' width (see the byte promotion below). Irie
        // strength-reduces a multiply (and unsigned divide / remainder) by a
        // constant power of two and legalizes the rest as a call to a math helper,
        // which RequireLibcallHelper links in.
        private void TranslateIntegerBinary(ArithOp op)
        {
            // ECMA-335 keeps bytes and 16-bit integers on the stack as int32, so
            // `byte + byte` carries into bit 8. Widen here and let Irie narrow it
            // back when only the low byte is demanded (e.g. a following conv.u1):
            // the dead high-byte adds are deleted by the legalizer.
            var right = PromoteNarrow(_stack.Pop());
            var left = PromoteNarrow(_stack.Pop());
            if (left.IsPointer != right.IsPointer)
            {
                // Pointer arithmetic: an i16 pointer plus an integer index/offset.
                // IL leaves the index as i32 (no conv) and relies on the pointer's
                // width (also for sub) — e.g. `q++` on a byte* emits `ldloc q; ldc.i4.1; add`.
                // Resize the integer side to the pointer's i16 width.
                if (left.IsPointer) right = Resize(right, IRType.I16);
                else left = Resize(left, IRType.I16);
            }
            else if (!left.Type.Equals(right.Type))
            {
                UnifyWidths(ref left, ref right);
            }

            var isPointer = op switch
            {
                ArithOp.AddI => left.IsPointer || right.IsPointer,
                ArithOp.SubI => left.IsPointer && !right.IsPointer,
                _ => false,
            };

            if (op is ArithOp.MulI or ArithOp.UDivI or ArithOp.SDivI or ArithOp.URemI or ArithOp.SRemI)
                RequireLibcallHelper(op, left, right);

            var result = _function.CreateVirtualRegister(left.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(left.Vreg, IsDefinition: false),
                new VirtualReg(right.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, left.Type, IsPointer: isPointer));
        }

        // Link in the math helper Irie's legalizer will call for this multiply /
        // divide / remainder (MOS6502LegalizerInfo.LowerLibcall picks the same
        // name from the same op and width). A multiply or unsigned divide /
        // remainder by a constant power of two is strength-reduced by Irie's
        // ArithSimplifyPass and needs no helper, so it is skipped here — keep the
        // two rules in step, or the symbol will be missing at link time.
        private void RequireLibcallHelper(ArithOp op, StackValue left, StackValue right)
        {
            var isSigned = op is ArithOp.SDivI or ArithOp.SRemI;
            if (!isSigned
                && (IsPowerOfTwoConstant(right) || (op == ArithOp.MulI && IsPowerOfTwoConstant(left))))
                return;

            var name = op switch
            {
                ArithOp.MulI => left.Type.SizeInBits switch
                {
                    <= 16 => "__mulhi3",
                    <= 32 => "__mulsi3",
                    _ => "__muldi3",
                },
                ArithOp.UDivI => left.Type.SizeInBits == 64 ? "__udivdi3" : "__udivsi3",
                ArithOp.SDivI => left.Type.SizeInBits == 64 ? "__divdi3" : "__divsi3",
                ArithOp.URemI => left.Type.SizeInBits == 64 ? "__umoddi3" : "__umodsi3",
                _ => left.Type.SizeInBits == 64 ? "__moddi3" : "__modsi3",
            };
            _parent.RequireMathHelper(name);
        }

        // Whether `value` is an arith.constant (possibly narrowed by a cast.trunc,
        // as a narrowing Resize leaves it) that is a positive power of two at its width.
        private bool IsPowerOfTwoConstant(StackValue value)
        {
            var def = _function.GetDefinition(value.Vreg);
            if (def is { Operands: [_, VirtualReg source] }
                && def.Opcode == CastDialect.OpRef(CastOp.Trunc))
                def = _function.GetDefinition(source.Id);

            if (def is not { Operands: [_, Immediate imm] }
                || def.Opcode != ArithDialect.OpRef(ArithOp.Constant))
                return false;

            var bits = value.Type.SizeInBits;
            var factor = bits >= 64 ? imm.Value : imm.Value & ((1L << bits) - 1);
            return factor > 0 && (factor & (factor - 1)) == 0;
        }

        // Whether local `index` is a block parameter of any block, i.e. its value
        // flows across a block boundary rather than dying in the block that set it.
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

        // Push a value loaded from storage of CLR type `clr`, flagging
        // pointer-like types (references, pointers, IntPtr) as such.
        private void PushTyped(int vreg, IRType type, TypeDescription clr) =>
            _stack.Push(new StackValue(vreg, type, IsSignedClr(clr), IsPointerClr(clr)));

        private void TranslateLdloc(int index)
        {
            if (_localIsFrameSlot[index])
            {
                var addr = _builder.BuildFrameAddr(index);
                if (_localClrTypes[index] is EcmaType { IsValueType: true })
                {
                    // Loading a struct local pushes its address; the value is
                    // consumed in place (ldfld) or copied on the next stloc.
                    _stack.Push(StackValue.Ptr(addr));
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

        // ldloca.N → push the address of the local's frame slot (a managed
        // pointer). The local was forced FrameSlot-bound during classification.
        private void TranslateLdloca(int index)
        {
            if (!_localIsFrameSlot[index])
                throw new InvalidOperationException(
                    $"IL→MIR: ldloca of non-FrameSlot local {index} (method {_method.UniqueName}).");
            var addr = _builder.BuildFrameAddr(index);
            _stack.Push(StackValue.Ptr(addr));
        }

        private void TranslateLdarg(int index)
        {
            if (index >= _parameterVregs.Length)
                throw new NotSupportedException(
                    $"IL→MIR: ldarg {index} out of range (method {_method.UniqueName}).");
            var type = ToIRType(_method.Parameters[index].Type);

            PushTyped(_parameterVregs[index], type, _method.Parameters[index].Type);
        }

        // br / br.s → cf.br to the target block, passing current locals as args.
        private void TranslateBr(MirBlock from, int target)
        {
            EmitBranchTo(from, BlockAt(target));
        }

        // and / or / xor: both operands must have the same integer width. A
        // value-used compare (i1) is first materialised to its 0/1 i8, so
        // `(a < b) & (c < d)` combines two bytes.
        private void TranslateBitwise(ArithOp op)
        {
            var right = PopMaterialised();
            var left = PopMaterialised();
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

        // shl / shr / shr.un: shift the value by an int32 amount. A byte operand is
        // first widened to int32, as ECMA-335's stack promotion would. The amount
        // becomes an i8 vreg: a constant is re-emitted as an i8 constant so the
        // legalizer can read it; anything else is truncated (and, for now, rejected
        // by the legalizer).
        private void TranslateShift(ArithOp op)
        {
            var amount = PopMaterialised();
            var value = PopMaterialised();
            value = PromoteNarrow(value);

            int amountVreg;
            if (_function.GetDefinition(amount.Vreg) is
                { Opcode: var amountOpcode, Operands: [_, Immediate amountImm] }
                && amountOpcode == ArithDialect.OpRef(ArithOp.Constant))
                amountVreg = _builder.BuildConstant(IRType.I8, amountImm.Value & 0xFF);
            else
                amountVreg = Resize(amount, IRType.I8).Vreg;

            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(op),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false),
                new VirtualReg(amountVreg, IsDefinition: false));
            _stack.Push(value with { Vreg = result });
        }

        // not: bitwise complement, `x ^ -1` at the operand's width.
        private void TranslateNot()
        {
            var value = PromoteNarrow(PopMaterialised());
            var allOnes = _builder.BuildConstant(value.Type, -1);
            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.Xor),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false),
                new VirtualReg(allOnes, IsDefinition: false));
            _stack.Push(new StackValue(result, value.Type));
        }

        // neg: two's-complement negation, `0 - x` at the operand's width.
        private void TranslateNeg()
        {
            var value = PromoteNarrow(PopMaterialised());
            var zero = _builder.BuildConstant(value.Type, 0);
            var result = _function.CreateVirtualRegister(value.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(zero, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, value.Type));
        }

        // A byte or 16-bit integer on the IL evaluation stack is an int32
        // (ECMA-335 §III.1.1): widen an i8 / i16 integer to i32 by its signedness
        // before an operation whose result depends on the bits above its width
        // (neg, not, add, sub, shifts, compares). Pointers stay i16.
        private StackValue PromoteNarrow(StackValue value) =>
            !value.IsPointer && value.Type.SizeInBits is 8 or 16
                ? Resize(value, IRType.I32)
                : value;

        // Convert an integer stack value to `to`'s width: truncate when narrowing,
        // otherwise zero- or sign-extend according to the value's signedness. A
        // same-width value is returned as is. (i1 never reaches here — callers
        // materialise a pending compare first, see PopMaterialised.)
        private StackValue Resize(StackValue value, IRType to)
        {
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
            return value with { Vreg = result, Type = to, IsPointer = false };
        }

        // Binary operands of different widths (an i8 byte against an i32 constant)
        // are widened to the wider one, as ECMA-335's implicit int32 promotion
        // would, each side extending by its own signedness.
        private void UnifyWidths(ref StackValue left, ref StackValue right)
        {
            if (left.Type.SizeInBits < right.Type.SizeInBits) left = Resize(left, right.Type);
            else if (right.Type.SizeInBits < left.Type.SizeInBits) right = Resize(right, left.Type);
        }

        // conv.i1 / conv.u1 / conv.i4 / conv.u4 / conv.i / conv.u: convert to the
        // target width (truncating or extending by the source's signedness).
        // `signed` is the result's signedness for narrow results: conv.i1 yields
        // a sign-carrying byte, conv.u1 a zero-extending one.
        // `pointer` marks the result as a native int (conv.i / conv.u).
        private void TranslateConv(IRType to, bool signed, bool pointer = false)
        {
            var result = Resize(PopMaterialised(), to);
            _stack.Push(result with { IsSigned = signed, IsPointer = pointer });
        }

        // conv.i8 / conv.u8: widen to 64 bits. The *opcode* picks the extension of
        // an int32 (conv.i8 sign-extends, conv.u8 zero-extends), whatever the
        // value's own signedness; a byte or 16-bit value is first widened to the
        // int32 it already is on the IL stack, by its own signedness.
        private void TranslateConvToI64(bool signed)
        {
            var value = PopMaterialised();
            if (value.Type.SizeInBits < 32) value = Resize(value, IRType.I32);
            var result = Resize(value with { IsSigned = signed }, IRType.I64);
            _stack.Push(result with { IsSigned = signed, IsPointer = false });
        }

        // Pops the stack, turning a pending i1 compare into a concrete 0/1 i8.
        private StackValue PopMaterialised()
        {
            var value = _stack.Pop();
            return value.Type.Equals(IRType.I1)
                ? new StackValue(MaterialiseCompare(value.Vreg), IRType.I8)
                : value;
        }

        // ceq/cgt/cgt.un/clt/clt.un → arith.cmpi <pred>, %a, %b → i1, pushed onto
        // the eval stack.
        //
        // The cmpi is left unmaterialised: if an immediately-following
        // brtrue/brfalse consumes it, TranslateCondBranch builds a cf.cond_br on
        // it and Irie's isel fuses the pair (loop-condition path). If instead the
        // i1 is consumed as a *value* (e.g. passed to Console.WriteLine(bool)),
        // TranslateCall materialises it into a concrete 0/1 i8 via a cond_br
        // diamond (see MaterialiseCompare). An i1 *operand* (comparing two
        // compares) is materialised to its 0/1 i8 first.
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

            var right = PromoteNarrow(PopMaterialised());
            var left = PromoteNarrow(PopMaterialised());
            UnifyWidths(ref left, ref right);

            var result = _builder.BuildCmpI(predicate, left.Vreg, right.Vreg);
            _stack.Push(new StackValue(result, IRType.I1));
        }

        // `i1 == 0` → not(i1), `i1 == 1` → i1 (in either operand order); null when
        // the operands are not an i1 and a constant 0/1.
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

        // The ECMA-335 compare-and-branch family (beq, bge, bgt, ble, blt, bne.un,
        // bge.un, bgt.un, ble.un, blt.un, each with a short form): the signed /
        // unsigned / equality predicate the fused compare tests. Float
        // unordered semantics of the `.un` forms are not modelled (no floats).
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

        // Displacement of a branch whose operand is a 1-byte (short) or 4-byte
        // offset from the end of the instruction.
        private static int ReadBranchTarget(ref BlobReader reader, bool isShort)
            => isShort ? reader.ReadSByte() + reader.Offset : reader.ReadInt32() + reader.Offset;

        // A switch's jump table as absolute IL offsets. The displacements are
        // relative to the end of the whole instruction (after the last entry).
        private static int[] ReadSwitchTargets(ref BlobReader reader)
        {
            var count = reader.ReadUInt32();
            var targets = new int[count];
            for (var i = 0; i < count; i++) targets[i] = reader.ReadInt32();
            for (var i = 0; i < count; i++) targets[i] += reader.Offset;
            return targets;
        }

        // switch: Irie has no multiway branch, so lower the jump table to a chain
        // of equality tests (llvm-mos does likewise for small tables; it has no
        // indirect-jump table lowering either):
        //
        //   <from>:  %c0 = cmpi eq %v, 0 ; cond_br %c0, case0, test1
        //   test1:   %c1 = cmpi eq %v, 1 ; cond_br %c1, case1, test2
        //   ...
        //   testN:   cond_br %cN, caseN, <fall-through>
        //
        // A value outside 0..N-1 (as unsigned) matches no test and falls through.
        // Cases that already target the fall-through block need no test.
        private void TranslateSwitch(MirBlock from, int[] targets)
        {
            var fallThrough = NextBlockAfterIlBlock();
            var selector = PromoteNarrow(PopMaterialised());

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

        // beq/bge/…: pop two values, compare with `predicate`, branch to `target`
        // when it holds, else fall through. The cmpi is built directly at the end
        // of the block so isel fuses it with the cf.cond_br (no i1 is
        // materialised), exactly as for `clt; brtrue`.
        private void TranslateCompareBranch(MirBlock from, int target, ArithCmpPredicate predicate)
        {
            // Resolve the fall-through from the block the branch started in: an
            // i1 operand's materialisation (PopMaterialised) moves _currentBlock
            // to a synthetic merge block with no IL offset.
            var fallThrough = NextBlockAfterIlBlock();
            TranslateCompare(predicate);
            var cond = _stack.Pop();

            _builder.SetInsertionPointAtEnd(_currentBlock);
            EmitCondBr(cond.Vreg, BlockAt(target), fallThrough);
        }

        // Build the cond_br diamond that turns an i1 compare result (defined by a
        // preceding arith.cmpi at the end of the current block) into a concrete
        // 0/1 i8:
        //
        //   <current>:                              ; cmpi already emitted here
        //       %c : i1 = arith.cmpi <pred>, %a, %b
        //       cf.cond_br %c, trueLeg, falseLeg     ; appended here
        //   trueLeg:  cf.br merge(1 : i8)
        //   falseLeg: cf.br merge(0 : i8)
        //   merge(%r : i8):                         ; becomes the new current block
        //
        // The cmpi+cond_br pair is selected by isel exactly as the fused branch
        // path. Returns the merge block's i8 parameter vreg (the 0/1 result).
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

        // brtrue/brfalse → cf.cond_br on the i1 condition. The taken edge goes
        // to the branch target; the not-taken edge falls through to the next
        // block (the instruction immediately after the branch — a discovered
        // leader). Irie's isel fuses a preceding arith.cmpi into the cond_br.
        private void TranslateCondBranch(MirBlock from, int target, bool branchIfTrue)
        {
            var cond = _stack.Pop();

            var fallThrough = NextBlockAfterIlBlock();
            var targetBlock = BlockAt(target);

            _builder.SetInsertionPointAtEnd(from);

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

        // cf.cond_br %cond, trueEdge(locals), falseEdge(locals) at the builder's
        // insertion point, threading the current locals as block arguments.
        private void EmitCondBr(int condVreg, MirBlock trueEdge, MirBlock falseEdge)
        {
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.CondBr),
                new VirtualReg(condVreg, IsDefinition: false),
                new BlockTarget(trueEdge, EdgeArgsFor(trueEdge)),
                new BlockTarget(falseEdge, EdgeArgsFor(falseEdge)));
        }

        // The block that starts at the next leader offset after the current IL
        // basic block — the fall-through successor of a conditional branch.
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

        // ldstr "..." → intern the string as a MirGlobal, push a `mem.symbol`
        // pointer (i16) to its bytes. WriteLineString consumes that pointer.
        private void TranslateLdstr(int token)
        {
            var value = _method.MetadataReader.GetUserString(
                MetadataTokens.UserStringHandle(token));
            var stringKey = $"string{token:X8}";
            _parent.RegisterStringGlobal(stringKey, value);

            var ptr = _builder.BuildMemSymbol(stringKey);
            _stack.Push(StackValue.Ptr(ptr));
        }

        // ldsfld <field> → load the static field's value via its symbolic
        // address: %p = mem.symbol @<owner>_<field>; %v = mem.load.iN %p.
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

        // stsfld <field> → store the top of stack into the static field's
        // symbolic address: %p = mem.symbol @<owner>_<field>; mem.store.iN %p, %v.
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

        // ldfld <field> → load the field from base + offset. The base (top of
        // stack) is a pointer / managed reference / struct address (all i16).
        private void TranslateLdfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var obj = _stack.Pop();
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(addr, IsDefinition: false));
            PushTyped(value, type, field.Type);
        }

        // stfld <field> → store top-of-stack into (base + offset). Stack layout
        // (top first): value, then the object pointer / struct address.
        private void TranslateStfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var value = Resize(PopMaterialised(), type);
            var obj = _stack.Pop();
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        // initobj <type> → zero the <size> bytes at the pointer on top of stack.
        private void TranslateInitobj(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var ptr = _stack.Pop();
            var zero = _builder.BuildConstant(IRType.I8, 0);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.Fill),
                new VirtualReg(ptr.Vreg, IsDefinition: false),
                new VirtualReg(zero, IsDefinition: false),
                new Immediate(type.InstanceSize));
        }

        // Address of a field at `offset` bytes from `baseVreg` (an i16 pointer).
        // Offset 0 reuses the base directly; otherwise emit a 16-bit add of a
        // constant offset (the legalizer narrows it to byte-wise arithmetic).
        private int EmitFieldAddress(int baseVreg, int offset)
        {
            if (offset == 0) return baseVreg;
            var offsetConst = _builder.BuildConstant(IRType.I16, offset);
            var addr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(addr, IsDefinition: true),
                new VirtualReg(baseVreg, IsDefinition: false),
                new VirtualReg(offsetConst, IsDefinition: false));
            return addr;
        }

        // Copy `size` bytes from the struct at `srcAddr` to the struct at
        // `destAddr` (both i16 pointers), one byte at a time. Used by stloc of a
        // value-type local (an IL struct assignment / copy).
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

        // Pre-scan the IL once for the set of locals that are address-taken
        // (have an `ldloca`). Those are forced FrameSlot-bound regardless of
        // type, alongside the value-type locals.
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
                    SkipOperand(ref scan, opCode);
            }
            return result;
        }

        private static MemOp LoadOpFor(IRType type) => type.SizeInBits switch
        {
            8 => MemOp.LoadI8,
            16 => MemOp.LoadI16,
            32 => MemOp.LoadI32,
            64 => MemOp.LoadI64,
            _ => throw new NotSupportedException(
                $"IL→MIR: mem.load of {type} is not supported yet."),
        };

        private static MemOp StoreOpFor(IRType type) => type.SizeInBits switch
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

        // A direct (non-virtual) call: pop the args (including `this` for instance
        // methods), emit call.func, push the return value.
        private void TranslateDirectCall(EcmaMethod callee)
        {
            // [MethodImpl(InternalCall)] methods (e.g. osasci) have no IL body —
            // they are provided by the hand-written runtime. Route the call to the
            // matching runtime symbol (the method name) and don't enqueue anything.
            if (callee.IsInternalCall)
            {
                if (!_parent.IsRuntimeFunction(callee.Name))
                    throw new NotSupportedException(
                        $"IL→MIR: [MethodImpl(InternalCall)] method {callee.UniqueName} has no " +
                        $"matching runtime function @{callee.Name} in runtime.irie.");
                EmitDirectCall(callee, callee.Name);
                return;
            }

            // Console.WriteLine(string) is implemented by the C# helper
            // ConsoleHelper.WriteLineString — translate it (emitted under the
            // stable name @WriteLineString) and call it.
            if (callee.UniqueName == "System_Console_WriteLine_String")
            {
                _parent.EnqueueMethod(_parent.GetWriteLineStringMethod());
                EmitDirectCall(callee, "WriteLineString");
                return;
            }

            // Other special-cased BCL methods map to hand-written runtime functions
            // (their real IL bodies are NOT translated/enqueued — the runtime
            // MIR replaces them).
            var pinnedName = _parent.GetEmitNameOverride(callee);
            var calleeName = callee.UniqueName switch
            {
                "System_Console_WriteLine_Int32" => "WriteLineInt32",
                "System_Console_WriteLine_Boolean" => "WriteLineBoolean",
                "System_Console_ReadLine" => "ReadLine",
                "System_Console_Beep" => "Beep",
                _ => pinnedName ?? callee.UniqueName,
            };
            // A C# method emitted under a pinned name (the math helpers) is still
            // ours to translate, unlike a special-cased BCL method.
            var isSpecialCased = pinnedName is null && !ReferenceEquals(calleeName, callee.UniqueName);

            // Only enqueue methods we are responsible for translating; runtime
            // functions (and special-cased BCL methods) are skipped.
            if (!isSpecialCased)
                _parent.EnqueueMethod(callee);

            EmitDirectCall(callee, calleeName);
        }

        // Pop one vreg per callee parameter, emit `call.func <symbolName>`, and
        // push the return value (if any). The symbol may differ from the callee's
        // UniqueName (special-cased BCL methods, InternalCall runtime wrappers).
        private void EmitDirectCall(EcmaMethod callee, string symbolName)
        {
            var argVregs = PopCallArgs(callee);
            var returnType = ToIRType(callee.MethodSignature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            var defs = _builder.BuildCall(symbolName, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                PushTyped(defs[0], returnType, callee.MethodSignature.ReturnType);
        }

        // Pop one vreg per callee parameter (Parameters[0] is `this` for instance
        // methods), in reverse so the deepest stack entry maps to parameter 0.
        private int[] PopCallArgs(EcmaMethod callee)
        {
            var argVregs = new int[callee.Parameters.Length];
            for (var i = callee.Parameters.Length - 1; i >= 0; i--)
            {
                // An i1 compare result passed as a value (rather than consumed by
                // a branch) is materialised into a concrete 0/1 i8 here, via the
                // cond_br diamond. The argument's expected type is i8 (Boolean
                // maps to i8 in ToIRType), so the materialised i8 matches. An
                // argument of another width (an int32 constant for a byte
                // parameter) is converted to the parameter's width.
                argVregs[i] = Resize(PopMaterialised(), ToIRType(callee.Parameters[i].Type)).Vreg;
            }
            return argVregs;
        }

        // callvirt → a direct call when the target is non-virtual (C# emits
        // callvirt for every instance call, including non-virtual ones, for its
        // null-check semantics); otherwise a vtable dispatch:
        //   %vtable : i16 = mem.load.i16 (this - 2)     ; header holds vtable ptr
        //   %fptr   : i16 = mem.load.i16 (%vtable + slot*2)
        //   call.indirect %fptr, this, args…
        private void TranslateCallvirt(Handle methodHandle)
        {
            var callee = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);

            if (!callee.IsVirtual)
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
            var vtablePtr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(vtablePtr, IsDefinition: true),
                new VirtualReg(headerAddr, IsDefinition: false));

            var slotIndex = _parent.GetVtableSlotIndex(callee);
            var slotAddr = EmitFieldAddress(vtablePtr, slotIndex * PointerSize);
            var fptr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(fptr, IsDefinition: true),
                new VirtualReg(slotAddr, IsDefinition: false));

            var defs = _builder.BuildCallIndirect(fptr, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                PushTyped(defs[0], returnType, callee.MethodSignature.ReturnType);
        }

        // newobj <ctor> → allocate the object then run its constructor:
        //   %ptr = call.func @ManagedHeap_Alloc(<instanceSize>, &Vtable_<type>)
        //   call.func @<ctor>(%ptr, args…)
        // and push %ptr. ManagedHeap.Alloc reserves a 2-byte header (the vtable
        // pointer) and returns a pointer to the object contents.
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

            _stack.Push(StackValue.Ptr(ptr));
        }

        // dup → re-push the top stack value (same vreg).
        private void TranslateDup() => _stack.Push(_stack.Peek());

        // sizeof <type> → push the type's size as an i32 constant (matches the
        // IL `sizeof` result type). For IntPtr this is the 16-bit pointer size.
        private void TranslateSizeof(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var vreg = _builder.BuildConstant(IRType.I32, type.Size);
            _stack.Push(new StackValue(vreg, IRType.I32));
        }

        // ldind.* → load a value from the i16 pointer on top of stack.
        //
        // Stack-type note: real IL zero-extends the loaded byte to i32 on the
        // evaluation stack. We deliberately model the result as i8, not i32, so
        // the value flows to consumers like osasci(byte) as a single CC_MOS byte
        // rather than a 4-byte value. Don't be surprised by the i8 stack type —
        // it is intentional, and matches how every current caller uses the byte
        // (compare-to-zero, pass to an i8 parameter). Revisit only if a future
        // caller actually needs the zero-extended i32 semantics.
        //
        // ldind.i1/u1 → i8, ldind.i4/u4 → i32, ldind.i/ref → i16 pointer. `signed`
        // is the loaded value's signedness (how an i8 widens later).
        //
        // `pointer` marks an i16 result as an address (ldind.i / ldind.ref) rather
        // than a 16-bit integer (ldind.i2 / ldind.u2).
        private void TranslateLdind(IRType type, bool signed, bool pointer = false)
        {
            var addr = _stack.Pop();
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(addr.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(value, type, signed, pointer));
        }

        // stind.i1/i4/i/ref → store the value into the pointer, truncated to the
        // store width (the IL stack holds an int32 for stind.i1). Stack layout
        // (top first): value, then the destination pointer.
        private void TranslateStind(IRType type)
        {
            var value = Resize(PopMaterialised(), type);
            var addr = _stack.Pop();
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr.Vreg, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        // Arrays are heap objects: the reference points at a 2-byte length, the
        // elements follow it, and the (null) vtable slot sits below it like any
        // object header. No bounds checks are emitted.
        private const int ArrayLengthSize = 2;

        // Size in bytes of an array element of the type named by an IL token.
        // Primitive element types arrive as System.* type references, which have
        // no useful layout of their own, so they are sized by name.
        private int ElementSize(EntityHandle typeHandle)
        {
            var assembly = _method.DeclaringType.Assembly;
            if (typeHandle.Kind == HandleKind.TypeReference)
            {
                var reader = _method.MetadataReader;
                var reference = reader.GetTypeReference((TypeReferenceHandle)typeHandle);
                if (reader.GetString(reference.Namespace) == "System")
                {
                    switch (reader.GetString(reference.Name))
                    {
                        case "Byte": case "SByte": case "Boolean": return 1;
                        case "Int16": case "UInt16": case "Char": return 2;
                        case "Int32": case "UInt32": return 4;
                        case "Int64": case "UInt64": return 8;
                        case "IntPtr": case "UIntPtr": case "Object": case "String":
                            return PointerSize;
                    }
                }
            }
            return assembly.ResolveType(typeHandle).Size;
        }

        // newarr <T> → %arr = call @ManagedHeap_AllocArray(length, length * sizeof(T))
        private void TranslateNewarr(EntityHandle typeHandle)
        {
            var elementSize = ElementSize(typeHandle);
            var length = Resize(PopMaterialised(), IRType.I16).Vreg;
            var contentSize = EmitScaledIndex(length, elementSize);

            var allocArray = _parent.GetAllocArrayMethod();
            _parent.EnqueueMethod(allocArray);
            var ptr = _builder.BuildCall(allocArray.UniqueName, [IRType.Pointer], length, contentSize)[0];
            _stack.Push(StackValue.Ptr(ptr));
        }

        // ldlen → the i16 length stored at the array reference (a native unsigned
        // int; the usual following conv.i4 zero-extends it).
        private void TranslateLdlen()
        {
            var array = _stack.Pop();
            var length = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(length, IsDefinition: true),
                new VirtualReg(array.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(length, IRType.I16));
        }

        // index * elementSize as an i16 (1 reuses the index; a power of two is
        // strength-reduced to a shift by ArithSimplify, anything else is a
        // multiply).
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

        // Pops the index and array reference (top first) and returns the address
        // of that element: array + ArrayLengthSize + index * elementSize.
        private int PopElementAddress(int elementSize)
        {
            var index = Resize(PopMaterialised(), IRType.I16).Vreg;
            var array = _stack.Pop();
            var offset = EmitScaledIndex(index, elementSize);
            var elements = EmitFieldAddress(array.Vreg, ArrayLengthSize);
            var addr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(addr, IsDefinition: true),
                new VirtualReg(elements, IsDefinition: false),
                new VirtualReg(offset, IsDefinition: false));
            return addr;
        }

        private void TranslateLdelema(EntityHandle typeHandle)
        {
            var addr = PopElementAddress(ElementSize(typeHandle));
            _stack.Push(StackValue.Ptr(addr));
        }

        private void TranslateLdelem(IRType type, bool signed, bool pointer = false)
        {
            var addr = PopElementAddress(type.SizeInBits / 8);
            _stack.Push(StackValue.Ptr(addr));
            TranslateLdind(type, signed, pointer);
        }

        // ldelem <T>: only primitive and reference element types (whose size is
        // 1, 2 or 4 bytes) can be loaded as a single value.
        private void TranslateLdelem(EntityHandle typeHandle)
        {
            var type = ElementSize(typeHandle) switch
            {
                1 => IRType.I8,
                2 => IRType.I16,
                4 => IRType.I32,
                8 => IRType.I64,
                _ => throw new NotSupportedException(
                    $"IL→MIR: ldelem of a {ElementSize(typeHandle)}-byte element is not supported yet."),
            };
            var name = typeHandle.Kind == HandleKind.TypeReference
                ? _method.MetadataReader.GetString(_method.MetadataReader
                    .GetTypeReference((TypeReferenceHandle)typeHandle).Name)
                : null;
            TranslateLdelem(type, signed: name is "SByte" or "Int16" or "Int32" or "Int64",
                pointer: type.Equals(IRType.I16) && name is not ("Int16" or "UInt16" or "Char"));
        }

        // stelem.*: stack (top first) is value, index, array.
        private void TranslateStelem(IRType type)
        {
            var value = _stack.Pop();
            var addr = PopElementAddress(type.SizeInBits / 8);
            _stack.Push(StackValue.Ptr(addr));
            _stack.Push(value);
            TranslateStind(type);
        }

        private void TranslateStelem(EntityHandle typeHandle)
        {
            var type = ElementSize(typeHandle) switch
            {
                1 => IRType.I8,
                2 => IRType.I16,
                4 => IRType.I32,
                8 => IRType.I64,
                _ => throw new NotSupportedException(
                    $"IL→MIR: stelem of a {ElementSize(typeHandle)}-byte element is not supported yet."),
            };
            TranslateStelem(type);
        }

        // Address `offset` bytes *below* an i16 pointer (e.g. the object header
        // at this-2). Mirrors EmitFieldAddress but subtracts.
        private int EmitPointerSub(int baseVreg, int offset)
        {
            var offsetConst = _builder.BuildConstant(IRType.I16, offset);
            var addr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                new VirtualReg(addr, IsDefinition: true),
                new VirtualReg(baseVreg, IsDefinition: false),
                new VirtualReg(offsetConst, IsDefinition: false));
            return addr;
        }

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

    // A value on the IL evaluation stack. Narrow integers stay narrow (a byte is
    // i8, not the int32 ECMA-335 would widen it to), so byte-sized code stays
    // byte-sized; IsSigned records whether an i8 came from a signed source
    // (sbyte, int, conv.i1, conv.i4), which decides sign- vs zero-extension when it
    // later meets a wider operand (see Resize / UnifyWidths).
    //
    // IsPointer marks an address or native int (a heap reference, managed
    // pointer, IntPtr, ...). Its type is i16, the same as a short/ushort/char,
    // so the flag is what makes `add`/`sub` treat it as address arithmetic and
    // stops 16-bit integers from being mistaken for one.
    private readonly record struct StackValue(
        int Vreg, IRType Type, bool IsSigned = false, bool IsPointer = false)
    {
        public static StackValue Ptr(int vreg) => new(vreg, IRType.Pointer, IsPointer: true);
    }
}
