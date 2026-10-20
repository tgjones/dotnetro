namespace DotLit.Tests;

public class CommandLineTests
{
    [Test]
    public async Task SplitsPipelineIntoStagesOfWords()
    {
        var stages = CommandLine.TryParsePipeline("a --x 1 | b  -y");

        await Assert.That(stages).IsNotNull();
        await Assert.That(stages!.Select(stage => string.Join(",", stage))).IsEquivalentTo(["a,--x,1", "b,-y"]);
    }

    [Test]
    public async Task QuotedWordsKeepSpacesAndPipes()
    {
        var stages = CommandLine.TryParsePipeline("""tool --exec "f(*, 0..3) | g" 'a b'""");

        await Assert.That(stages).IsNotNull();
        await Assert.That(stages![0]).IsEquivalentTo(["tool", "--exec", "f(*, 0..3) | g", "a b"]);
    }

    [Test]
    [Arguments("a > out.txt")]
    [Arguments("a; b")]
    [Arguments("a && b")]
    [Arguments("a $HOME")]
    [Arguments("a | ")]
    [Arguments("a 'unterminated")]
    public async Task LeavesShellSyntaxToTheShell(string commandLine)
    {
        await Assert.That(CommandLine.TryParsePipeline(commandLine)).IsNull();
    }
}
