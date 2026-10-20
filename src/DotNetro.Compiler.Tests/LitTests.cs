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

    public static IEnumerable<Func<TestDataRow<string>>> LitTestSource()
    {
        var result = new List<Func<TestDataRow<string>>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Lit",
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => new TestDataRow<string>(litTestFile, Categories: [Path.GetFileNameWithoutExtension(litTestFile)]));
        }

        return result;
    }
}
