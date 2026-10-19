using Irie.MachineCode;
using Irie.Mir;

namespace Irie.Target;

/// <summary>
/// Target hook that produces a MachineCodeModule from a post-PseudoExpansion
/// MirModule. Called by the driver after passMgr.Run(); not part of the pass
/// pipeline because it produces a different module type than the pipeline
/// operates on.
/// </summary>
public abstract class MachineCodeEmitter
{
    public abstract MachineCodeModule Emit(MirModule module);
}
