using DotLit;

namespace Irie.Tests;

public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTest(string filePath)
    {
        LitTestRunner.Run(filePath, LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration()));
    }

    // The corpus cases that run on the emulator (RUN-exec) use the default (beam)
    // placement in LitTest; run them again compiled with the naive placement
    // (every value in a home): irie-exec still has to agree with the interpreter
    // on every input.
    [Test]
    [MethodDataSource(nameof(ExecLitTestSource))]
    public void ExecLitTestNaivePlacement(string filePath)
    {
        var configuration = LitConfig.Load(filePath, LitConfig.CurrentBuildConfiguration());
        var variables = configuration.Variables.SetItem(
            "irie-exec", configuration.Variables["irie-exec"] + " --placement naive");
        LitTestRunner.Run(filePath, configuration with { Variables = variables });
    }

    public static IEnumerable<Func<string>> ExecLitTestSource() =>
        LitTestSource().Where(file =>
        {
            var text = File.ReadAllText(file());
            return text.Contains("RUN-exec:") && !text.Contains("--placement");
        });

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        foreach (var pattern in new[] { "*.irie", "*.mir", "*.s" })
        foreach (var litTestFile in Directory.EnumerateFiles("Lit", pattern, SearchOption.AllDirectories))
            result.Add(() => litTestFile);

        return result;
    }
}
