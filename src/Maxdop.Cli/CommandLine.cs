using Maxdop.Core.Formatting;

namespace Maxdop.Cli;

/// <summary>What the process should do, once the arguments have been understood.</summary>
internal enum Mode
{
    /// <summary>Read stdin, write formatted text to stdout.</summary>
    Stdin,

    /// <summary>Format one file to stdout.</summary>
    ToStdout,

    /// <summary>Rewrite files in place.</summary>
    Write,

    /// <summary>Report whether files would change, touching nothing.</summary>
    Check,

    /// <summary>Record every file that would change, so the rest can be gated from now on.</summary>
    WriteBaseline,

    Help,
    Version,
}

internal sealed record CommandLine
{
    internal Mode Mode { get; init; }

    internal IReadOnlyList<string> Files { get; init; } = [];

    internal string? ConfigPath { get; init; }

    internal string? StdinFilePath { get; init; }

    /// <summary>Parser version from the command line, which overrides any config file.</summary>
    internal int? ParserVersion { get; init; }

    /// <summary>Path to read a newline- or NUL-separated file list from; <c>-</c> means stdin.</summary>
    internal string? FilesFrom { get; init; }

    /// <summary>
    /// Baseline of files allowed to remain unformatted.
    /// </summary>
    /// <remarks>
    /// Never discovered from the filesystem, only ever named. A baseline weakens what
    /// <c>--check</c> means, and a file that quietly turned up next to the config would weaken it
    /// without anyone deciding to — the one direction a safety tool must not drift on its own.
    /// </remarks>
    internal string? BaselinePath { get; init; }

    /// <summary>
    /// Parses arguments, or explains what is wrong with them.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than using a parsing library, for the reason the whole project is
    /// NativeAOT: <c>System.CommandLine</c> and friends lean on reflection, and the argument surface
    /// here is a dozen flags. The cost of the library would be measured in megabytes of binary.
    /// </remarks>
    internal static bool TryParse(string[] args, out CommandLine command, out string? error)
    {
        error = null;
        command = new CommandLine();

        var files = new List<string>();
        var write = false;
        var check = false;
        var writeBaseline = false;
        string? filesFrom = null;
        string? baselinePath = null;
        string? configPath = null;
        string? stdinFilePath = null;
        int? parserVersion = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    command = new CommandLine { Mode = Mode.Help };
                    return true;

                case "--version":
                    command = new CommandLine { Mode = Mode.Version };
                    return true;

                case "--write" or "-w":
                    write = true;
                    continue;

                case "--check":
                    check = true;
                    continue;

                case "--files-from":
                    if (!TryTakeValue(args, ref i, arg, out filesFrom, out error))
                    {
                        return false;
                    }

                    continue;

                case "--baseline":
                    if (!TryTakeValue(args, ref i, arg, out baselinePath, out error))
                    {
                        return false;
                    }

                    continue;

                case "--write-baseline":
                    writeBaseline = true;
                    continue;

                case "--config":
                    if (!TryTakeValue(args, ref i, arg, out configPath, out error))
                    {
                        return false;
                    }

                    continue;

                case "--stdin-filepath":
                    if (!TryTakeValue(args, ref i, arg, out stdinFilePath, out error))
                    {
                        return false;
                    }

                    continue;

                case "--parser-version":
                    if (!TryTakeValue(args, ref i, arg, out var version, out error))
                    {
                        return false;
                    }

                    if (!ParserFactory.TryParseVersion(version!, out var parsed))
                    {
                        error = $"--parser-version: \"{version}\" is not recognised. "
                            + "Use a product year (2016, 2019, 2022, 2025), a compatibility level (80…180), or fabricdw.";
                        return false;
                    }

                    parserVersion = parsed;
                    continue;

                case "--range":
                    // Reserved by the CLI contract so the flag cannot be given a different meaning
                    // later. Rejected
                    // rather than ignored: silently formatting the whole file when the caller asked
                    // for a range would corrupt an editor's "format selection".
                    error = "--range is reserved and not implemented yet. "
                        + "Range formatting needs the layout engine to reason about a partial document; "
                        + "formatting the whole file instead would silently overwrite text outside the range.";
                    return false;

                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        error = $"unknown option: {arg}";
                        return false;
                    }

                    files.Add(arg);
                    continue;
            }
        }

        if (write && check)
        {
            error = "--write and --check are mutually exclusive: one rewrites files, the other promises not to.";
            return false;
        }

        if (writeBaseline && (write || check))
        {
            error = "--write-baseline replaces --check rather than joining it: it records what would change "
                + "instead of failing on it. Run it on its own, then use --check --baseline afterwards.";
            return false;
        }

        // A baseline forgives files that would otherwise fail, which only means anything to --check.
        // Under --write it would have to mean "leave these badly formatted", turning a baseline into
        // a permanent exclusion list — a different feature wearing the same name.
        if (baselinePath is not null && write)
        {
            error = "--baseline applies to --check, not --write. It records which files may stay unformatted; "
                + "--write formats whatever it is given. Use exclude in .maxdop.json to skip files entirely.";
            return false;
        }

        if (baselinePath is not null && !check && !writeBaseline)
        {
            error = "--baseline needs --check or --write-baseline.";
            return false;
        }

        var wantsFiles = write || check || writeBaseline;

        if (wantsFiles && files.Count == 0 && filesFrom is null)
        {
            error = $"{(write ? "--write" : check ? "--check" : "--write-baseline")} needs at least one file or directory. "
                + "Reading stdin cannot rewrite anything; drop the flag to format stdin to stdout.";
            return false;
        }

        if (filesFrom is not null && !wantsFiles)
        {
            error = "--files-from needs --write, --check or --write-baseline: a list of files has nowhere "
                + "to go when the output is stdout.";
            return false;
        }

        // More than one file with nowhere to put the output would concatenate them into one stream,
        // which is never what anybody meant.
        if (files.Count > 1 && !wantsFiles)
        {
            error = "several files given without --write or --check. "
                + "Formatting them all to stdout would run them together; say what to do with the output.";
            return false;
        }

        var mode = (write, check, writeBaseline, files.Count) switch
        {
            (true, _, _, _) => Mode.Write,
            (_, true, _, _) => Mode.Check,
            (_, _, true, _) => Mode.WriteBaseline,
            (_, _, _, 0) => Mode.Stdin,
            _ => Mode.ToStdout,
        };

        command = new CommandLine
        {
            Mode = mode,
            Files = files,
            ConfigPath = configPath,
            StdinFilePath = stdinFilePath,
            ParserVersion = parserVersion,
            FilesFrom = filesFrom,
            BaselinePath = writeBaseline ? baselinePath ?? Baseline.DefaultFileName : baselinePath,
        };

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, string flag, out string? value, out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"{flag} needs a value.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }

    internal const string Usage = """
        maxdop — Max Degree of Prettiness for your T-SQL

        USAGE
          maxdop [options] < input.sql          format stdin to stdout
          maxdop [options] file.sql             format one file to stdout
          maxdop --write [options] <paths...>   rewrite files in place
          maxdop --check [options] <paths...>   exit nonzero if any file would change

          A path may be a file or a directory. Directories are searched for *.sql, so
          `maxdop --check src/` means the same thing in every shell — unlike a `**` glob,
          which a default bash silently collapses to one directory level.

        OPTIONS
          -w, --write               rewrite each file in place
              --check               report which files would change; write nothing
              --files-from <path>   read paths from a file, or from stdin with `-`;
                                    separated by newlines, or by NUL if any is present
              --baseline <path>     with --check, forgive files this baseline still covers
              --write-baseline      record every file that would change and exit 0
              --config <path>       use this config file instead of discovering one
              --stdin-filepath <p>  path stdin's content came from, used to find .maxdop.json
              --parser-version <v>  2008|2012|2014|2016|2017|2019|2022|2025|fabricdw, or 80…180
              --range <a:b>         reserved; not implemented
          -h, --help                this text
              --version             version and parser grammar

        CONFIG
          The nearest .maxdop.json at or above the file being formatted wins. Keys:
          maxWidth, indentSize, useTabs, keywordCase ("upper"|"lower"), leadingCommas,
          recaseBuiltInFunctions, alwaysBreakSelectList, alwaysBreakWhere, maxBlankLines,
          parserVersion, initialQuotedIdentifiers, exclude.

          "recaseBuiltInFunctions" (default true) gives built-in function names and
          global variables the configured keyword case: `getdate()` becomes `GETDATE()`,
          `@@rowcount` becomes `@@ROWCOUNT`. Only unqualified, undelimited calls are
          touched, never `dbo.MyFunc(...)` or `[len](...)`, and only documented global
          variables, never your own `DECLARE @@MyVar`.

          "exclude" is a list of globs, relative to the config file's own directory:
            { "exclude": ["db/generated/**", "*.gen.sql", "vendor/"] }
          `*` and `?` stay within one path segment, `**` spans them, a pattern with no
          slash matches at any depth, and naming a directory excludes what is under it.
          Matching is case-insensitive everywhere, so a repository behaves the same on a
          Windows laptop and a Linux runner.

        ADOPTING ON AN EXISTING CODEBASE
          --check on an established repository fails on everything at once, so:
            maxdop --write-baseline src/         record today's unformatted files
            maxdop --check --baseline .maxdop-baseline src/
          A baseline entry is the hash of a file's current, unformatted bytes. Editing the
          file changes the hash and the exemption ends, so the count only goes down.

        ONLY WHAT CHANGED
          maxdop has no dependency on git; hand it the list instead:
            git diff --name-only --diff-filter=ACM -z | maxdop --check --files-from -

        EXIT CODES
          0  formatted, or --check found nothing to change
          1  input could not be parsed, or --check found a file that would change;
             in both cases the file on disk is untouched
          2  maxdop's own problem: a refusal, bad arguments, or an unreadable file

        maxdop never rewrites a file it cannot prove it formatted safely: every output is
        re-parsed and token-compared against the input, and anything unrecognised is passed
        through byte-for-byte.
        """;
}
