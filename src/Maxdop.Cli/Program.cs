using System.Reflection;
using Maxdop.Core.Formatting;

namespace Maxdop.Cli;

/// <summary>
/// The shipping entry point.
/// </summary>
/// <remarks>
/// Exit codes are the contract with CI and are deliberately coarse: <b>0</b> nothing to do,
/// <b>1</b> the input's problem and the file is untouched, <b>2</b> maxdop's problem. The middle case
/// covers both "could not parse" and "<c>--check</c> found a file that would change", because from a
/// pipeline's point of view they are the same instruction: look at your file. A <b>2</b> means the
/// formatter itself failed and wants a bug report.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Stack for the thread everything runs on.
    /// </summary>
    /// <remarks>
    /// ScriptDom's parser recurses over nesting depth, and a stack overflow in .NET is
    /// <em>uncatchable</em> — the process dies where it stands, which is the one failure mode the
    /// "your file is returned intact" promise cannot survive. The catch below cannot help.
    /// <para>How much stack the work gets is otherwise the host's choice, and it differs: a main
    /// thread on Linux gets 8 MB, while a secondary thread on macOS gets a fraction of that. The same
    /// file then formats on one platform and aborts on another — which is exactly how this was found,
    /// as a macOS-only CI failure on a 400-deep test that passes everywhere else. Asking for the
    /// stack explicitly makes the depth maxdop survives a property of maxdop.</para>
    /// <para>16 MB is about 12,000 levels of nesting at the ~1.3 KB per level measured here, against
    /// a deepest-real-world figure in four digits. It is reserved, not committed, so it costs address
    /// space rather than memory.</para>
    /// </remarks>
    private const int WorkerStackBytes = 16 * 1024 * 1024;

    private static int Main(string[] args)
    {
        // One thread for the whole process, so this costs a single thread start rather than anything
        // per file.
        var exitCode = (int)RunOutcome.Failed;
        var worker = new Thread(() => exitCode = Run(args), WorkerStackBytes);

        worker.Start();
        worker.Join();

        return exitCode;
    }

    private static int Run(string[] args)
    {
        if (!CommandLine.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine($"maxdop: {error}");
            Console.Error.WriteLine("Try 'maxdop --help'.");
            return (int)RunOutcome.Failed;
        }

        switch (command.Mode)
        {
            case Mode.Help:
                Console.Out.WriteLine(CommandLine.Usage);
                return (int)RunOutcome.Ok;

            case Mode.Version:
                Console.Out.WriteLine($"maxdop {Version()}");
                Console.Out.WriteLine($"T-SQL grammar {ParserFactory.LatestVersion} (ScriptDom), overridable with --parser-version");
                return (int)RunOutcome.Ok;
        }

        try
        {
            if (command.Mode == Mode.Stdin)
            {
                var stdinRunner = new FormatRunner(
                    command.Mode, FormatOptions.Default, command.ConfigPath, command.ParserVersion);

                return (int)stdinRunner.RunStdin(command.StdinFilePath);
            }

            return (int)RunOverFiles(command);
        }
        catch (Exception e)
        {
            // Last line of defence. The library's contract is that a file is always returned intact,
            // so reaching here is a bug — but exiting 2 with a message beats a stack trace and a
            // half-written file, and it keeps the "never destroy files" promise at the process
            // boundary as well as inside the formatter.
            Console.Error.WriteLine($"maxdop: internal error — {e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine("This is a maxdop bug. No file was modified after this point.");
            return (int)RunOutcome.Failed;
        }
    }

    /// <summary>
    /// Expands the paths given into files, then formats or checks them.
    /// </summary>
    private static RunOutcome RunOverFiles(CommandLine command)
    {
        var expander = new PathExpander(new ExcludeRules(command.ConfigPath));

        foreach (var path in command.Files)
        {
            if (!expander.TryAdd(path, named: true, out var error))
            {
                Console.Error.WriteLine($"maxdop: {error}");
                return RunOutcome.Failed;
            }
        }

        if (command.FilesFrom is { } list)
        {
            if (!FileList.TryRead(list, out var listed, out var listError))
            {
                Console.Error.WriteLine($"maxdop: {listError}");
                return RunOutcome.Failed;
            }

            foreach (var path in listed)
            {
                if (!expander.TryAdd(path, named: false, out var error))
                {
                    Console.Error.WriteLine($"maxdop: {error}");
                    return RunOutcome.Failed;
                }
            }
        }

        expander.Finish();

        if (expander.ExcludedCount > 0)
        {
            Console.Error.WriteLine($"maxdop: {expander.ExcludedCount} file(s) excluded by .maxdop.json");
        }

        if (expander.Files.Count == 0)
        {
            // Not an error, and deliberately so: `git diff … | maxdop --check --files-from -` on a
            // pull request that touched no SQL is the single most common way to get here, and failing
            // it would make the gate unusable. Said out loud, though, because the other way to get
            // here is a path that matched nothing, and a silent success is what that must not be.
            Console.Error.WriteLine("maxdop: no .sql files to check.");
            return RunOutcome.Ok;
        }

        // CommandLine rejects several *arguments* aimed at stdout, but one argument is now a whole
        // directory, so the count it checked is not the count that matters. Without this, `maxdop
        // src/` runs every file in the tree together into one stream — the exact thing that guard
        // exists to prevent, and it cannot see it from the command line alone.
        if (command.Mode == Mode.ToStdout && expander.Files.Count > 1)
        {
            Console.Error.WriteLine(
                $"maxdop: that matched {expander.Files.Count} files, and formatting them all to stdout "
                + "would run them together. Use --write to rewrite them, or --check to test them.");
            return RunOutcome.Failed;
        }

        Baseline? baseline = null;
        if (command.Mode == Mode.Check && command.BaselinePath is { } baselinePath)
        {
            if (!Baseline.TryLoad(baselinePath, out baseline, out var error))
            {
                Console.Error.WriteLine($"maxdop: {error}");
                return RunOutcome.Failed;
            }
        }

        var runner = new FormatRunner(
            command.Mode,
            FormatOptions.Default,
            command.ConfigPath,
            command.ParserVersion,
            baseline);

        var outcome = runner.RunFiles(expander.Files);

        if (command.Mode == Mode.WriteBaseline)
        {
            return WriteBaseline(command.BaselinePath!, runner, outcome);
        }

        ReportBaselineUse(runner, baseline);
        return outcome;
    }

    private static RunOutcome WriteBaseline(string path, FormatRunner runner, RunOutcome outcome)
    {
        if (outcome == RunOutcome.Failed)
        {
            // A refusal means the formatter has a bug on one of these files. Writing the baseline
            // anyway would record a hash for a file whose correct output nobody knows yet.
            Console.Error.WriteLine($"maxdop: {path} not written — a file was refused, which is a maxdop bug.");
            return outcome;
        }

        if (!Baseline.TryWrite(path, runner.BaselineEntries, out var error))
        {
            Console.Error.WriteLine($"maxdop: {error}");
            return RunOutcome.Failed;
        }

        Console.Error.WriteLine(
            $"maxdop: {path} written with {runner.BaselineEntries.Count} file(s). "
            + "Check them from now on with --check --baseline.");
        return RunOutcome.Ok;
    }

    private static void ReportBaselineUse(FormatRunner runner, Baseline? baseline)
    {
        if (baseline is null)
        {
            return;
        }

        if (runner.BaselinedCount > 0)
        {
            Console.Error.WriteLine($"maxdop: {runner.BaselinedCount} file(s) still unformatted, allowed by the baseline.");
        }

        // Entries nothing matched are files that have since been formatted or deleted. Harmless —
        // a re-broken file would have a different hash and stop being covered — but worth saying, or
        // the list never shrinks and stops meaning anything.
        if (baseline.Unused.Count > 0)
        {
            var entries = baseline.Unused.Count == 1 ? "entry is" : "entries are";
            Console.Error.WriteLine(
                $"maxdop: {baseline.Unused.Count} baseline {entries} no longer needed and can be deleted.");
        }
    }

    private static string Version() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { } informational && informational.Length > 0
            ? informational.Split('+')[0]
            : "0.0.0-dev";
}
