using DotLit;
using TUnit.Core.Interfaces;

namespace DotNetro.Compiler.Tests;

/// <summary>
/// Each lit test spawns several processes (a Roslyn compile per RUN line, dnrc, the
/// emulator), so TUnit's default of four tests per core oversubscribes the machine
/// until the run looks hung. Keep one test per core.
/// </summary>
public sealed class LitParallelLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}

[ParallelLimiter<LitParallelLimit>]
public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTest(string filePath)
    {
        LitTestRunner.Run(filePath, LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration()));
    }

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Lit",
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => litTestFile);
        }

        return result;
    }
}
