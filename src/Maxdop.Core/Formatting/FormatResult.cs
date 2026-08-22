namespace Maxdop.Core.Formatting;

public enum FormatStatus
{
    /// <summary>Formatted successfully. Maps to CLI exit code 0.</summary>
    Formatted,

    /// <summary>
    /// Input did not parse. Maps to CLI exit code 1. Not an error in the formatter — a file with a
    /// syntax error is still a file worth not breaking.
    /// </summary>
    /// <remarks>
    /// The output is the unchanged input, <b>except</b> for a multi-batch file where some batches
    /// parsed and some did not: there the formatted batches are kept and the rest copied through
    /// verbatim (see <see cref="BatchFormatter"/>), so <see cref="FormatResult.Changed"/> is true
    /// and the file on disk does change. Exit code 1 means "the input has a problem, look at it",
    /// which is as true of a partly formatted file as of an untouched one.
    /// </remarks>
    ParseFailed,

    /// <summary>
    /// The formatter produced output it could not prove safe, so it returned the input
    /// unchanged. Maps to CLI exit code 2: this is a maxdop bug, and it should be reported.
    /// </summary>
    Refused,
}

/// <summary>
/// Outcome of a format call. <see cref="Output"/> is always safe to write to disk — on any
/// failure it is the original input, byte for byte.
/// </summary>
public sealed record FormatResult
{
    private FormatResult(
        string output,
        FormatStatus status,
        string? input,
        IReadOnlyList<string> diagnostics,
        string? rejectedOutput = null)
    {
        Output = output;
        Status = status;
        Diagnostics = diagnostics;
        RejectedOutput = rejectedOutput;
        Changed = !string.Equals(output, input ?? output, StringComparison.Ordinal);
    }

    /// <summary>Text to write. Never a partially formatted or damaged file.</summary>
    public string Output { get; }

    public FormatStatus Status { get; }

    /// <summary>Human-readable notes for stderr. Never written to stdout.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Whether output differs from input — what <c>--check</c> reports on.</summary>
    public bool Changed { get; }

    /// <summary>
    /// On <see cref="FormatStatus.Refused"/>, the text the formatter produced and then rejected.
    /// Null otherwise.
    /// </summary>
    /// <remarks>
    /// Diagnostics only. **Never write this to a file** — it is by definition output that failed
    /// verification. It exists because a refusal you cannot inspect is painful to fix: the
    /// diagnostic gives a line number in text the caller does not otherwise have, and this is what
    /// makes a bug report actionable.
    /// </remarks>
    public string? RejectedOutput { get; }

    internal static FormatResult Success(string output, string input) =>
        new(output, FormatStatus.Formatted, input, []);

    internal static FormatResult ParseError(string input, IReadOnlyList<string> diagnostics) =>
        new(input, FormatStatus.ParseFailed, input, diagnostics);

    /// <summary>
    /// Some batches formatted, some did not parse. The output is safe to write: every batch in it
    /// is either fully verified or a byte-for-byte copy of the input's.
    /// </summary>
    internal static FormatResult PartiallyFormatted(
        string output,
        string input,
        IReadOnlyList<string> diagnostics) =>
        new(output, FormatStatus.ParseFailed, input, diagnostics);

    internal static FormatResult Refuse(string input, string reason, string? rejectedOutput = null) =>
        new(input, FormatStatus.Refused, input, [reason], rejectedOutput);
}
