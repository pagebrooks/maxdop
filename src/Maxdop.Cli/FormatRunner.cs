using Maxdop.Core.Formatting;
using Maxdop.Core.Text;

namespace Maxdop.Cli;

/// <summary>The worst thing that happened, which becomes the process exit code.</summary>
/// <remarks>
/// Ordered by severity so a run over many files can keep the maximum. The split that matters to CI:
/// <see cref="WouldChangeOrUnparseable"/> is about the <em>input</em> and leaves the file untouched,
/// while <see cref="Failed"/> means maxdop itself could not do its job and someone should hear about
/// it.
/// </remarks>
internal enum RunOutcome
{
    Ok = 0,
    WouldChangeOrUnparseable = 1,
    Failed = 2,
}

internal sealed class FormatRunner(
    Mode mode,
    FormatOptions fallbackOptions,
    string? configPath,
    int? parserVersionOverride,
    Baseline? baseline = null)
{
    private readonly Dictionary<string, FormatOptions> _optionsByDirectory = [];
    private readonly List<(string File, byte[] Bytes)> _baselineEntries = [];

    /// <summary>Files that would have failed --check but are covered by the baseline.</summary>
    internal int BaselinedCount { get; private set; }

    /// <summary>What a --write-baseline run should record.</summary>
    internal IReadOnlyList<(string File, byte[] Bytes)> BaselineEntries => _baselineEntries;

    internal RunOutcome RunStdin(string? stdinFilePath)
    {
        // stdin is decoded UTF-8 by contract: editors hand over a decoded buffer, so there are
        // no bytes to preserve here and no BOM to worry about.
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput(), Streams.Utf8))
        {
            input = reader.ReadToEnd();
        }

        if (!TryResolveOptions(stdinFilePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(stdinFilePath)), out var options, out var error))
        {
            Console.Error.WriteLine($"maxdop: {error}");
            return RunOutcome.Failed;
        }

        var result = SqlFormatter.Format(input, options);
        var label = stdinFilePath ?? "<stdin>";

        // Output goes out on every path, including refusal and parse failure, because FormatResult's
        // Output is the untouched input in those cases. An editor that got nothing back would show
        // the user an empty document.
        WriteStdout(result.Output);
        return Report(label, result, input);
    }

    internal RunOutcome RunFiles(IReadOnlyList<string> files)
    {
        var worst = RunOutcome.Ok;

        foreach (var file in files)
        {
            var outcome = RunFile(file);
            if (outcome > worst)
            {
                worst = outcome;
            }
        }

        return worst;
    }

    private RunOutcome RunFile(string file)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"maxdop: {file}: {e.Message}");
            return RunOutcome.Failed;
        }

        var encoding = SourceEncoding.Detect(bytes);

        // The byte-level safety gate, and the reason it comes before anything else: if decoding and
        // re-encoding does not reproduce the file exactly, then writing it back would change bytes
        // nobody asked to change — a Windows-1252 comment quietly becoming U+FFFD, for instance.
        // Refusing is the only correct answer, and it is the same argument as the token verifier one
        // level down.
        if (!encoding.CanRoundTrip(bytes))
        {
            // Exit 1, not 2. The encoding of a file is a fact about the input, the file is left
            // untouched, and the reader's next move is to go and look at it — which is exactly what
            // exit 1 already means for a file that will not parse. Calling it maxdop's problem
            // invited bug reports for files that are behaving exactly as intended, and, worse, put
            // it beyond what a baseline can forgive: two Windows-1252 files in ScriptDom's own test
            // suite were enough to make `--check` over that tree exit 2 permanently, with no way to
            // adopt around it short of an exclude rule you can only write once you have diagnosed it.
            Console.Error.WriteLine(
                $"maxdop: {file}: cannot be read as {encoding.Name} without changing its bytes, so it was left alone. "
                + "If it is a legacy code page (Windows-1252 and friends), re-save it as UTF-8 or UTF-16.");
            return RunOutcome.WouldChangeOrUnparseable;
        }

        var input = encoding.Decode(bytes);

        if (!TryResolveOptions(Path.GetDirectoryName(Path.GetFullPath(file)), out var options, out var error))
        {
            Console.Error.WriteLine($"maxdop: {error}");
            return RunOutcome.Failed;
        }

        var result = SqlFormatter.Format(input, options);
        var outcome = Report(file, result, input);

        // "Not clean" covers both halves of what a gate cares about: a file that would be
        // reformatted, and one that could not be parsed at all. A baseline forgives either, because
        // to someone adopting maxdop on an existing repository they are the same sentence — this
        // file is known to be a problem and is not today's problem.
        var notClean = result.Changed || outcome == RunOutcome.WouldChangeOrUnparseable;

        switch (mode)
        {
            case Mode.ToStdout:
                WriteStdout(result.Output);
                break;

            // Not `Status == Formatted`: a multi-batch file where some batches parsed and some did
            // not comes back partly formatted, and that output is still safe to write — every batch
            // in it is either fully verified or a byte-for-byte copy. A refusal never reaches here,
            // because a refused result's output *is* the input, so Changed is false.
            case Mode.Write when result.Changed:
                try
                {
                    // Not File.WriteAllBytes: that truncates before it writes, so a failed write
                    // destroys the file it was formatting. See AtomicFile.
                    AtomicFile.Write(file, encoding.Encode(result.Output));
                    Console.Error.WriteLine($"maxdop: {file} formatted");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"maxdop: {file}: could not write — {e.Message}");
                    return RunOutcome.Failed;
                }

                break;

            // A refusal is never forgiven, by a baseline or anything else. It means maxdop produced
            // output it could not prove equivalent to the input, which is a bug report, not a style
            // question, and burying it under an exemption list is how it would go unnoticed.
            case Mode.Check when notClean && outcome != RunOutcome.Failed:
                if (baseline is not null && baseline.Covers(file, bytes))
                {
                    BaselinedCount++;
                    return RunOutcome.Ok;
                }

                if (result.Changed)
                {
                    Console.Error.WriteLine($"maxdop: {file} would be reformatted");
                }

                return RunOutcome.WouldChangeOrUnparseable;

            case Mode.WriteBaseline when notClean && outcome != RunOutcome.Failed:
                _baselineEntries.Add((file, bytes));

                // Recording, not judging: a file that does not parse is exactly what a baseline is
                // for, so it must not also make the recording run exit nonzero.
                return RunOutcome.Ok;
        }

        return outcome;
    }

    /// <summary>
    /// Turns a format result into an outcome, saying on stderr what happened when it is not a plain
    /// success.
    /// </summary>
    private static RunOutcome Report(string file, FormatResult result, string input)
    {
        switch (result.Status)
        {
            case FormatStatus.ParseFailed when result.Changed:
                // A multi-batch file where only some batches parsed. Exit code 1 still means "the
                // input has a problem, look at it" — which is as true of a partly formatted file as
                // of an untouched one — but saying "left unchanged" here would be a lie.
                Console.Error.WriteLine($"maxdop: {file}: {First(result)}");
                foreach (var detail in result.Diagnostics.Skip(1))
                {
                    Console.Error.WriteLine($"maxdop:   {detail}");
                }

                return RunOutcome.WouldChangeOrUnparseable;

            case FormatStatus.ParseFailed:
                // Not maxdop's fault and not a crash: the file comes back untouched. The most common
                // cause by a distance is SQLCMD syntax, which no T-SQL parser accepts.
                Console.Error.WriteLine(
                    SqlcmdDirectives.Find(input) is { } directive
                        ? $"maxdop: {file}: contains SQLCMD syntax ({directive}), which is not T-SQL and cannot be parsed. Left unchanged."
                        : $"maxdop: {file}: could not be parsed, so it was left unchanged. {First(result)}");
                return RunOutcome.WouldChangeOrUnparseable;

            case FormatStatus.Refused:
                // maxdop produced output it could not prove equivalent to the input, and threw it
                // away. The file is safe; the formatter has a bug.
                Console.Error.WriteLine(
                    $"maxdop: {file}: formatted output failed verification, so the file was left unchanged. "
                    + $"This is a maxdop bug — please report it. {First(result)}");
                return RunOutcome.Failed;

            default:
                return RunOutcome.Ok;
        }
    }

    private static string First(FormatResult result) =>
        result.Diagnostics.Count > 0 ? result.Diagnostics[0] : string.Empty;

    /// <summary>
    /// Resolves the options for a file, caching per directory.
    /// </summary>
    /// <remarks>
    /// Cached because <c>--write</c> over a whole repo would otherwise re-read and re-parse the same
    /// <c>.maxdop.json</c> once per file, and walking up the tree touches the filesystem at every
    /// level.
    /// </remarks>
    private bool TryResolveOptions(string? directory, out FormatOptions options, out string? error)
    {
        error = null;

        var key = directory ?? string.Empty;
        if (_optionsByDirectory.TryGetValue(key, out var cached))
        {
            options = cached;
            return true;
        }

        options = fallbackOptions;

        // An explicit --config is used verbatim and its absence is an error; a discovered one is
        // optional by nature. Silently ignoring a --config path the user named would run with
        // settings they did not ask for.
        var path = configPath ?? ConfigFile.Discover(directory);
        if (configPath is not null && !File.Exists(configPath))
        {
            error = $"--config: {configPath} does not exist.";
            return false;
        }

        if (path is not null && !ConfigFile.TryLoad(path, options, out options, out error))
        {
            return false;
        }

        // The command line beats the config file, so a one-off run against a different grammar does
        // not mean editing the repo's shared settings.
        if (parserVersionOverride is { } version)
        {
            options = options with { ParserVersion = version };
        }

        _optionsByDirectory[key] = options;
        return true;
    }

    /// <summary>
    /// Writes text to stdout as UTF-8 with no BOM and no newline translation.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Console.Write</c>: the console writer can translate newlines and, on
    /// Windows, emit a preamble. Either would mean the bytes an editor receives are not the bytes the
    /// formatter produced, which shows up as phantom CRLF churn on every formatted file.
    /// </remarks>
    private static void WriteStdout(string text)
    {
        using var stdout = Console.OpenStandardOutput();
        var bytes = Streams.Utf8.GetBytes(text);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}
