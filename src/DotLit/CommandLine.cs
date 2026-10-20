using System.Text;

namespace DotLit;

/// <summary>Splits a RUN command line into the stages of a pipeline, the way a POSIX shell would for the simple cases lit tests use.</summary>
internal static class CommandLine
{
    /// <summary>
    /// Splits <paramref name="commandLine"/> on unquoted <c>|</c> into stages of words, honouring
    /// single and double quotes. Returns null for anything else a shell would interpret (redirects,
    /// <c>;</c>, <c>&amp;</c>, variables, globs, …), which the caller then leaves to the shell.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>>? TryParsePipeline(string commandLine)
    {
        var stages = new List<IReadOnlyList<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;

        void EndWord()
        {
            if (inWord)
                words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            switch (c)
            {
                case ' ' or '\t':
                    EndWord();
                    break;
                case '|':
                    EndWord();
                    if (words.Count == 0)
                        return null;
                    stages.Add(words);
                    words = [];
                    break;
                case '\'':
                {
                    var end = commandLine.IndexOf('\'', i + 1);
                    if (end < 0)
                        return null;
                    word.Append(commandLine, i + 1, end - i - 1);
                    inWord = true;
                    i = end;
                    break;
                }
                case '"':
                {
                    for (i++; ; i++)
                    {
                        if (i >= commandLine.Length)
                            return null;
                        if (commandLine[i] == '"')
                            break;
                        if (commandLine[i] is '$' or '`' or '\\')
                            return null;
                        word.Append(commandLine[i]);
                    }
                    inWord = true;
                    break;
                }
                case ';' or '&' or '<' or '>' or '$' or '`' or '\\' or '(' or ')' or '{' or '}' or '*' or '?' or '~' or '#' or '!' or '\n':
                    return null;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }

        EndWord();
        if (words.Count == 0)
            return null;
        stages.Add(words);
        return stages;
    }
}
