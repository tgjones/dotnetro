using Irie.Mir;

namespace Irie.Passes;

/// <summary>
/// Base class for analyses that compute information about a MirFunction.
/// Analyses are not passes — they are never added to a PassManager pipeline.
/// To run an analysis, call PassManager.GetAnalysis&lt;T, TResult&gt;(function).
/// </summary>
public abstract class MirFunctionAnalysis<TResult>
{
    public abstract TResult Compute(MirFunction function);
}
