using System.Text;

namespace Maxdop.Cli;

/// <summary>
/// The encoding used for stdin and stdout.
/// </summary>
/// <remarks>
/// UTF-8 with no BOM, and constructed here rather than using <see cref="Encoding.UTF8"/> because that
/// static <em>emits a preamble</em>: piping formatted SQL through it would prepend three bytes to
/// every result. Not throwing on invalid bytes, unlike the file path — a pipe has no file to protect,
/// and failing a keystroke-latency format request because a byte was odd would be worse than the
/// substitution.
/// </remarks>
internal static class Streams
{
    internal static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
