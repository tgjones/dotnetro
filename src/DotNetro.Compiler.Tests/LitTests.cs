using DotLit;

namespace DotNetro.Compiler.Tests;

public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTest(string filePath)
    {
        LitTestRunner.Run(filePath, LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration()));
    }

    // The same programs compiled with the naive placement (every value in a home):
    // each RUN line that runs @dnrc gets `--placement naive`, and the DIFF against
    // the .NET run still has to hold.
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTestNaivePlacement(string filePath)
    {
        var configuration = LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration());
        var variables = configuration.Variables.SetItem("dnrc", configuration.Variables["dnrc"] + " --placement naive");
        LitTestRunner.Run(filePath, configuration with { Variables = variables });
    }

    // And with the beam placement (values kept in registers within blocks).
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTestBeamPlacement(string filePath)
    {
        var configuration = LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration());
        var variables = configuration.Variables.SetItem("dnrc", configuration.Variables["dnrc"] + " --placement beam");
        LitTestRunner.Run(filePath, configuration with { Variables = variables });
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
