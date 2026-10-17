using Irie.Dialects.Call;
using Irie.Mir;

namespace Irie.Passes.Analyses;

// The module's call graph, at any pipeline point: it recognises both the pre-isel
// `call.func` / `call.indirect` forms and the lowered target forms (`mos6502.jsr.abs
// @f`, and the indirect-call shim's `jsr.abs @__call_indirect_trampoline`).
//
// An edge i → j exists when function i directly calls function j. An INDIRECT call
// (a `call.indirect`, a call with no symbol, or a call of the indirect-call
// trampoline) may reach any ADDRESS-TAKEN function — one whose symbol is
// referenced by something other than a call: a vtable or other global initializer
// (`DataSymbolRef`), or an address materialisation (`mem.symbol @f`, an
// `lda.imm.sym{lo,hi} @f`). A function whose address is never taken can only be
// reached by a direct call, so it is not an indirect target. This is the closed
// world the compiler sees; it is exact for DotNetro programs and conservative
// (never unsound) otherwise.
public sealed class CallGraph
{
    // The symbol of the runtime's indirect-call trampoline. Lowering turns an
    // indirect call into `jsr.abs @__call_indirect_trampoline`, which dispatches
    // with `JMP (zp)`; to the call graph that jsr IS the indirect call.
    public const string IndirectCallTrampoline = "__call_indirect_trampoline";

    public IReadOnlyList<MirFunction> Functions { get; }
    public IReadOnlyDictionary<string, int> IndexByName { get; }

    // Direct callees only (indirect calls not expanded).
    public HashSet<int>[] DirectCallees { get; }

    // Direct callees plus, for every indirect call site, all address-taken functions.
    public HashSet<int>[] Callees { get; }

    public bool[] HasIndirectCall { get; }
    public bool[] SelfRecursive { get; }
    public bool[] AddressTaken { get; }

    // Strongly connected components of `Callees`. Ids are assigned in Tarjan
    // completion order, so every callee's SCC has a lower id than its caller's
    // (unless they share one): ascending id is a bottom-up (callee-first) order.
    public int[] SccId { get; }
    public int[] SccSize { get; }

    private CallGraph(MirModule module)
    {
        Functions = module.Functions;
        var n = Functions.Count;
        var indexByName = new Dictionary<string, int>(n);
        for (var i = 0; i < n; i++)
            indexByName[Functions[i].Name] = i;
        IndexByName = indexByName;

        DirectCallees = new HashSet<int>[n];
        Callees = new HashSet<int>[n];
        HasIndirectCall = new bool[n];
        SelfRecursive = new bool[n];
        AddressTaken = new bool[n];

        foreach (var global in module.Globals)
            if (global.Initializer is { } init)
                foreach (var item in init.Items)
                    if (item is DataSymbolRef r && indexByName.TryGetValue(r.SymbolName, out var target))
                        AddressTaken[target] = true;

        for (var i = 0; i < n; i++)
        {
            DirectCallees[i] = [];
            foreach (var block in Functions[i].Blocks)
                foreach (var instr in block.Instructions)
                    Scan(i, instr);
        }

        for (var i = 0; i < n; i++)
        {
            Callees[i] = [.. DirectCallees[i]];
            if (!HasIndirectCall[i]) continue;
            for (var j = 0; j < n; j++)
                if (AddressTaken[j])
                    Callees[i].Add(j);
            if (Callees[i].Contains(i))
                SelfRecursive[i] = true;
        }

        SccId = TarjanSccs(Callees, out var sccSize);
        SccSize = sccSize;
    }

    public static CallGraph Build(MirModule module) => new(module);

    private void Scan(int caller, MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        var isCall = dialect.Prefix == "call"
            ? (CallOp)instr.Opcode.Code is CallOp.Func or CallOp.Indirect
            : !dialect.IsSideEffectFree(instr.Opcode.Code) && HasSymbolOperand(instr);

        if (!isCall)
        {
            // Not a call: a function symbol here is an address-taking use.
            foreach (var op in instr.Operands)
                if (op is Symbol s && IndexByName.TryGetValue(s.Name, out var target))
                    AddressTaken[target] = true;
            return;
        }

        string? callee = null;
        foreach (var op in instr.Operands)
            if (op is Symbol s) { callee = s.Name; break; }

        if (callee is null || callee == IndirectCallTrampoline)
        {
            // An indirect dispatch, or a call to a ROM address (no symbol): treat
            // both as indirect; a ROM routine never calls back, so over-approximating
            // costs only precision.
            HasIndirectCall[caller] = true;
            return;
        }

        if (!IndexByName.TryGetValue(callee, out var calleeIndex))
            return; // external symbol, not in this module

        DirectCallees[caller].Add(calleeIndex);
        if (calleeIndex == caller)
            SelfRecursive[caller] = true;
    }

    private static bool HasSymbolOperand(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is Symbol)
                return true;
        return false;
    }

    // Tarjan's strongly-connected-components algorithm. Returns an array mapping
    // each function index to its SCC id, and (out) an array of SCC sizes indexed
    // by SCC id. Iterative to avoid deep recursion on large modules.
    private static int[] TarjanSccs(HashSet<int>[] adjacency, out int[] sccSize)
    {
        var n = adjacency.Length;
        var index = new int[n];
        var lowlink = new int[n];
        var onStack = new bool[n];
        var visited = new bool[n];
        var sccId = new int[n];
        Array.Fill(sccId, -1);

        var stack = new Stack<int>();
        var nextIndex = 0;
        var nextScc = 0;
        var sizes = new List<int>();

        // Iterative DFS frame: the node and an enumerator position over its
        // successors.
        for (var start = 0; start < n; start++)
        {
            if (visited[start]) continue;

            var work = new Stack<(int node, List<int> succ, int pos)>();
            visited[start] = true;
            index[start] = lowlink[start] = nextIndex++;
            stack.Push(start);
            onStack[start] = true;
            work.Push((start, [.. adjacency[start]], 0));

            while (work.Count > 0)
            {
                var (node, succ, pos) = work.Pop();

                var recursed = false;
                while (pos < succ.Count)
                {
                    var w = succ[pos];
                    pos++;
                    if (!visited[w])
                    {
                        visited[w] = true;
                        index[w] = lowlink[w] = nextIndex++;
                        stack.Push(w);
                        onStack[w] = true;
                        // Re-push current frame at its advanced position, then
                        // descend into w.
                        work.Push((node, succ, pos));
                        work.Push((w, [.. adjacency[w]], 0));
                        recursed = true;
                        break;
                    }
                    if (onStack[w])
                        lowlink[node] = Math.Min(lowlink[node], index[w]);
                }

                if (recursed) continue;

                // All successors processed: if node is an SCC root, pop the SCC.
                if (lowlink[node] == index[node])
                {
                    var size = 0;
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        sccId[member] = nextScc;
                        size++;
                    } while (member != node);
                    sizes.Add(size);
                    nextScc++;
                }

                // Propagate lowlink to the parent frame (now on top of work).
                if (work.Count > 0)
                {
                    var parent = work.Peek();
                    lowlink[parent.node] = Math.Min(lowlink[parent.node], lowlink[node]);
                }
            }
        }

        sccSize = [.. sizes];
        return sccId;
    }
}
