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

    public static IEnumerable<Func<TestDataRow<string>>> LitTestSource()
    {
        var result = new List<Func<TestDataRow<string>>>();

        foreach (var pattern in new[] { "*.irie", "*.mir", "*.s" })
        foreach (var litTestFile in Directory.EnumerateFiles("Lit", pattern, SearchOption.AllDirectories))
            result.Add(() => new TestDataRow<string>(litTestFile, Categories: [Path.GetFileNameWithoutExtension(litTestFile)]));

        return result;
    }
}
