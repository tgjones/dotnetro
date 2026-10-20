using System.Text;

namespace DotLit;

/// <summary>
/// Per-run console redirection, so the tools a lit test runs can execute inside the test process
/// (see <see cref="InProcessTool"/>) instead of as one process each. <see cref="Console.Out"/>,
/// <see cref="Console.Error"/> and <see cref="Console.In"/> are replaced, once, by routers that
/// write to and read from the streams of the innermost <see cref="Run"/> scope of the calling
/// (async) context, and pass through to the real console outside any scope. Tools reach the raw
/// byte streams through <see cref="OpenStandardInput"/> and friends rather than <see cref="Console"/>.
/// </summary>
public static class ToolConsole
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly object InstallLock = new();
    private static bool _installed;

    /// <summary>Runs <paramref name="body"/> with the console bound to the given streams and returns its result.</summary>
    public static T Run<T>(Stream stdin, Stream stdout, Stream stderr, Func<T> body)
    {
        EnsureInstalled();
        var previous = Current.Value;
        Current.Value = new Scope(stdin, stdout, stderr);
        try
        {
            return body();
        }
        finally
        {
            Current.Value = previous;
        }
    }

    /// <summary>Like <see cref="Console.OpenStandardInput()"/>, but reads the current scope's stdin.</summary>
    public static Stream OpenStandardInput() =>
        Current.Value is { } scope ? new NonClosingStream(scope.Stdin) : Console.OpenStandardInput();

    /// <summary>Like <see cref="Console.OpenStandardOutput()"/>, but writes the current scope's stdout.</summary>
    public static Stream OpenStandardOutput() =>
        Current.Value is { } scope ? new NonClosingStream(scope.Stdout) : Console.OpenStandardOutput();

    /// <summary>Like <see cref="Console.OpenStandardError()"/>, but writes the current scope's stderr.</summary>
    public static Stream OpenStandardError() =>
        Current.Value is { } scope ? new NonClosingStream(scope.Stderr) : Console.OpenStandardError();

    private static void EnsureInstalled()
    {
        lock (InstallLock)
        {
            if (_installed)
                return;
            Console.SetOut(new RoutedWriter(static () => Current.Value?.Out, Console.Out));
            Console.SetError(new RoutedWriter(static () => Current.Value?.Err, Console.Error));
            Console.SetIn(new RoutedReader(static () => Current.Value?.In, Console.In));
            _installed = true;
        }
    }

    private sealed class Scope(Stream stdin, Stream stdout, Stream stderr)
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public Stream Stdin { get; } = stdin;
        public Stream Stdout { get; } = stdout;
        public Stream Stderr { get; } = stderr;

        public TextReader In { get; } = new StreamReader(stdin, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        public TextWriter Out { get; } = new StreamWriter(new NonClosingStream(stdout), Utf8) { AutoFlush = true };
        public TextWriter Err { get; } = new StreamWriter(new NonClosingStream(stderr), Utf8) { AutoFlush = true };
    }

    /// <summary>A stream that ignores <c>Dispose</c>, since tools dispose the stdout/stdin they open.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    private sealed class RoutedWriter(Func<TextWriter?> current, TextWriter fallback) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        private TextWriter Target => current() ?? fallback;
        public override void Write(char value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
        public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer);
        public override void Write(string? value) => Target.Write(value);
        public override void Flush() => Target.Flush();
    }

    private sealed class RoutedReader(Func<TextReader?> current, TextReader fallback) : TextReader
    {
        private TextReader Source => current() ?? fallback;
        public override int Peek() => Source.Peek();
        public override int Read() => Source.Read();
        public override int Read(char[] buffer, int index, int count) => Source.Read(buffer, index, count);
        public override int Read(Span<char> buffer) => Source.Read(buffer);
        public override string? ReadLine() => Source.ReadLine();
        public override string ReadToEnd() => Source.ReadToEnd();
    }
}
