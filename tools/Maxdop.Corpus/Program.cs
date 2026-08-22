using System.Diagnostics;
using Maxdop.Core.Comments;
using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Corpus;

/// <summary>
/// Runs the formatter over a tree of .sql files and reports where it stands.
/// </summary>
/// <remarks>
/// The distinction that matters: a <b>parse failure</b> is usually the input's problem (a
/// different dialect, SQLCMD directives, a construct newer than the chosen grammar), whereas a
/// <b>refusal</b> is always maxdop's problem — the formatter produced output it could not prove
/// safe. Refusals are the number to drive to zero; parse failures are triage.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Flags that take a value must consume it, or `--show 25` treats "25" as a path — which
        // showed up as a phantom crash and an inflated file count.
        string[] valuedFlags = ["--show", "--width", "--why"];
        string[] booleanFlags = ["--verbose", "--each-option"];
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (valuedFlags.Contains(args[i]))
                {
                    i++;
                    continue;
                }

                if (booleanFlags.Contains(args[i]))
                {
                    continue;
                }

                // Rejected rather than ignored. Silently skipping an unrecognised flag is how
                // `--histogram 25` became a phantom crash: the flag was dropped and its value
                // became a path. A misspelt flag should fail loudly, not change the measurement.
                Console.Error.WriteLine($"unknown flag: {args[i]}");
                return 2;
            }

            paths.Add(args[i]);
        }

        if (paths.Count == 0)
        {
            Console.Error.WriteLine(
                "usage: maxdop-corpus <directory|file> [...] [--verbose] [--show N] [--width N] [--why NodeType] [--each-option]");
            return 2;
        }

        var verbose = args.Contains("--verbose");
        var show = ArgValue(args, "--show", 10);
        // Defaulted from the product's own option rather than a literal, so the harness cannot drift
        // from what users actually get. It had: every coverage figure was measured at 120 while the
        // CLI ships 100, which made a fully-formatted file look like it still needed reformatting.
        var width = ArgValue(args, "--width", PrintOptions.Default.MaxWidth);
        var options = FormatOptions.Default with { Print = PrintOptions.Default with { MaxWidth = width } };

        // A path that exists as neither file nor directory is a typo, and reporting it as a "crash"
        // is actively misleading: crashes are the one bucket that means maxdop mangled something.
        var missing = paths.Where(p => !Directory.Exists(p) && !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"no such file or directory: {string.Join(", ", missing)}");
            return 2;
        }

        var files = paths
            .SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*.sql", SearchOption.AllDirectories)
                : [p])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (args.Contains("--each-option"))
        {
            return RunEachOption(files, options);
        }

        Console.WriteLine($"{files.Count} .sql file(s); max width {width}");
        Console.WriteLine();

        var report = new Report { WhyType = ArgText(args, "--why") };
        var stopwatch = Stopwatch.StartNew();

        foreach (var file in files)
        {
            Examine(file, options, report);
        }

        stopwatch.Stop();
        report.Print(stopwatch.Elapsed, show, verbose);

        // Refusals, crashes and instability are maxdop defects. Parse failures are not.
        return report.Refused.Count + report.Crashed.Count + report.Unstable.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs the whole corpus once per configuration option, reporting only the safety numbers.
    /// </summary>
    /// <remarks>
    /// Every option changes layout, and layout is what the round-trip, comment and idempotency gates
    /// are checked against — so an option that is never exercised is a whole formatter nobody has
    /// measured. That is not hypothetical: <c>leadingCommas</c> was not idempotent for any file with
    /// a comment before a list item, 12 of them in this corpus, and it survived because every corpus
    /// run had been made with default options.
    /// <para>
    /// Cheap enough to be worth doing: one pass per option over ~1,100 files, and only the numbers
    /// that mean "maxdop has a bug" are printed.
    /// </para>
    /// </remarks>
    private static int RunEachOption(List<string> files, FormatOptions baseline)
    {
        (string Name, FormatOptions Options)[] variants =
        [
            ("(defaults)", baseline),
            ("leadingCommas", baseline with { LeadingCommas = true }),
            ("alwaysBreakSelectList", baseline with { AlwaysBreakSelectList = true }),
            ("alwaysBreakWhere", baseline with { AlwaysBreakWhere = true }),
            ("keywordCase=lower", baseline with { KeywordCase = KeywordCase.Lower }),
            ("maxBlankLines=0", baseline with { MaxBlankLines = 0 }),
            ("initialQuotedIdentifiers", baseline with { InitialQuotedIdentifiers = true }),
            ("useTabs", baseline with { Print = baseline.Print with { UseTabs = true } }),
            ("indentSize=2", baseline with { Print = baseline.Print with { IndentSize = 2 } }),

            // Narrow width is not a config key anyone is likely to set, but it is the cheapest way
            // to force every group in the corpus to break, which is where layout bugs live.
            ("maxWidth=60", baseline with { Print = baseline.Print with { MaxWidth = 60 } }),
        ];

        Console.WriteLine($"{files.Count} .sql file(s), once per option");
        Console.WriteLine();
        Console.WriteLine($"{"option",-26}{"formatted",10}{"refused",10}{"crashed",10}{"unstable",10}");

        var defects = 0;
        foreach (var (name, options) in variants)
        {
            var report = new Report();
            foreach (var file in files)
            {
                Examine(file, options, report);
            }

            var bad = report.Refused.Count + report.Crashed.Count + report.Unstable.Count;
            defects += bad;

            Console.WriteLine(
                $"{name,-26}{report.Formatted,10}{report.Refused.Count,10}{report.Crashed.Count,10}{report.Unstable.Count,10}"
                + (bad > 0 ? "  <-- defect" : string.Empty));

            foreach (var (file, detail) in report.Refused.Concat(report.Crashed).Concat(report.Unstable).Take(5))
            {
                Console.WriteLine($"    {Path.GetFileName(file)}: {detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(defects == 0
            ? "every option: 0 refused, 0 crashed, 0 unstable"
            : $"{defects} defect(s) across options");

        return defects > 0 ? 1 : 0;
    }

    private static void Examine(string file, FormatOptions options, Report report)
    {
        string input;
        try
        {
            input = File.ReadAllText(file);
        }
        catch (IOException e)
        {
            report.Crashed.Add((file, $"read failed: {e.Message}"));
            return;
        }

        if (input.Trim().Length == 0)
        {
            report.Empty++;
            return;
        }

        // ScriptDom's test suite includes ~90 files that exist to be rejected, marked by a prose
        // first line. Counting them as parse failures lets them mask real regressions, so they get
        // their own bucket. Corpus-specific convention, deliberately narrow.
        if (input.TrimStart().StartsWith("This is not a valid", StringComparison.OrdinalIgnoreCase))
        {
            report.ExpectedFailure++;
            return;
        }

        report.TotalBytes += input.Length;

        FormatResult result;
        try
        {
            result = SqlFormatter.Format(input, options);
        }
        catch (Exception e)
        {
            // Any exception is a defect: the contract is that a file is always returned intact.
            report.Crashed.Add((file, $"{e.GetType().Name}: {e.Message}"));
            return;
        }

        switch (result.Status)
        {
            case FormatStatus.ParseFailed:
                report.ParseFailed.Add((file, FirstDiagnostic(result)));
                return;

            case FormatStatus.Refused:
                report.Refused.Add((file, FirstDiagnostic(result)));
                if (Environment.GetEnvironmentVariable("MAXDOP_DUMP_REJECTED") is not null)
                {
                    Console.Error.WriteLine($"--- rejected output for {file} ---");
                    Console.Error.WriteLine(result.RejectedOutput);
                    Console.Error.WriteLine("--- end ---");
                }

                return;
        }

        report.Formatted++;
        if (!result.Changed)
        {
            report.AlreadyFormatted++;
        }

        // Idempotency: format(format(x)) must equal format(x). An instability here means the
        // output is not a fixed point, which shows up for users as a file that keeps changing.
        try
        {
            var second = SqlFormatter.Format(result.Output, options);
            if (second.Status != FormatStatus.Formatted)
            {
                report.Unstable.Add((file, $"second pass returned {second.Status}: {FirstDiagnostic(second)}"));
            }
            else if (!string.Equals(second.Output, result.Output, StringComparison.Ordinal))
            {
                report.Unstable.Add((file, FirstDifference(result.Output, second.Output)));
            }
        }
        catch (Exception e)
        {
            report.Unstable.Add((file, $"second pass threw {e.GetType().Name}: {e.Message}"));
        }

        MeasurePassthrough(input, file, options, report);
    }

    /// <summary>
    /// Re-runs the printer alone to record which node types were emitted verbatim, and how much
    /// text each accounts for.
    /// </summary>
    /// <remarks>
    /// Ranking by <em>significant tokens</em> rather than node count is the point. A node count
    /// says how often a handler is missing; a token count says how much of the file it leaves
    /// untouched, which is what decides whether the formatter is useful yet. One
    /// <c>CreateTableStatement</c> can be a hundred tokens while a hundred
    /// <c>IntegerLiteral</c>s are a hundred — and only the first is worth a handler.
    /// </remarks>
    private static void MeasurePassthrough(string input, string file, FormatOptions options, Report report)
    {
        try
        {
            var parser = ParserFactory.Create(options);
            using var reader = new StringReader(input);
            var root = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                return;
            }

            var roots = new List<TSqlFragment>();
            var printer = new SqlPrinter(root, CommentAttacher.Attach(root), options, roots);
            _ = printer.Print(root);

            foreach (var (word, count) in printer.KeywordSliceIdentifiers)
            {
                report.KeywordSliceIdentifiers.TryGetValue(word, out var seen);
                report.KeywordSliceIdentifiers[word] = seen + count;
            }

            report.PassthroughNodes += printer.PassthroughCount;
            report.TotalNodes += printer.NodeCount;

            var tokens = root.ScriptTokenStream;
            var fileTokens = CountSignificant(tokens, 0, tokens.Count - 1);
            report.TotalTokens += fileTokens;
            var fileVerbatim = 0;

            foreach (var node in roots)
            {
                if (node.FirstTokenIndex < 0 || node.LastTokenIndex < node.FirstTokenIndex)
                {
                    continue;
                }

                var weight = CountSignificant(tokens, node.FirstTokenIndex, node.LastTokenIndex);

                // A value leaf is verbatim by design and always sits inside formatted output, so
                // its tokens are not unformatted work — they are counted separately and excluded
                // from both the coverage gap and the handler ranking.
                if (SqlPrinter.IsVerbatimByDesign(node))
                {
                    report.ByDesignTokens += weight;
                    continue;
                }

                report.PassthroughTokens += weight;
                fileVerbatim += weight;
                report.Record(node.GetType().Name, weight, file);
                report.RecordGuard(
                    printer.PassthroughGuards.GetValueOrDefault(node, "?"),
                    node.GetType().Name,
                    weight,
                    file);

                if (report.WhyType is not null
                    && string.Equals(node.GetType().Name, report.WhyType, StringComparison.OrdinalIgnoreCase))
                {
                    report.WhySamples.Add((file, weight, Snippet(tokens, node)));
                }
            }

            if (fileTokens > 0)
            {
                report.PerFileCoverage.Add((double)(fileTokens - fileVerbatim) / fileTokens * 100);
            }
        }
        catch (Exception)
        {
            // Already counted as formatted; this is only instrumentation.
        }
    }

    /// <summary>
    /// The opening source text of a passed-through node, whitespace collapsed, for the
    /// <c>--why</c> report.
    /// </summary>
    /// <remarks>
    /// The head of the construct is what identifies which guard fired — the target and column list
    /// of an INSERT, the argument list of a RAISERROR — so this shows the start rather than trying
    /// to summarise a construct that may run to hundreds of tokens.
    /// </remarks>
    private static string Snippet(IList<TSqlParserToken> tokens, TSqlFragment node)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = node.FirstTokenIndex; i <= Math.Min(node.LastTokenIndex, tokens.Count - 1); i++)
        {
            var text = tokens[i].Text ?? string.Empty;
            if (tokens[i].TokenType is TSqlTokenType.WhiteSpace
                or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(text);
            if (builder.Length > 220)
            {
                builder.Append(" …");
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static int CountSignificant(IList<TSqlParserToken> tokens, int fromIndex, int toIndex)
    {
        var count = 0;
        var to = Math.Min(toIndex, tokens.Count - 1);
        for (var i = Math.Max(0, fromIndex); i <= to; i++)
        {
            var type = tokens[i].TokenType;
            if (type is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile
                or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            {
                count++;
            }
        }

        return count;
    }

    private static string FirstDiagnostic(FormatResult result) =>
        result.Diagnostics.Count > 0 ? result.Diagnostics[0] : "?";

    private static string FirstDifference(string first, string second)
    {
        var i = 0;
        while (i < first.Length && i < second.Length && first[i] == second[i])
        {
            i++;
        }

        var line = first.Take(i).Count(c => c == '\n') + 1;
        return $"not idempotent; diverges at output line {line}: {Excerpt(first, i)} vs {Excerpt(second, i)}";
    }

    private static string Excerpt(string text, int at)
    {
        var start = Math.Max(0, at - 10);
        var length = Math.Min(40, text.Length - start);
        return length <= 0
            ? "<end>"
            : "\"" + text.Substring(start, length).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static int ArgValue(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }

    private static string? ArgText(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class Report
    {
        internal int Formatted { get; set; }

        internal int AlreadyFormatted { get; set; }

        internal int Empty { get; set; }

        /// <summary>Files that exist to be rejected, so their failure is not a signal.</summary>
        internal int ExpectedFailure { get; set; }

        internal long TotalBytes { get; set; }

        internal int PassthroughNodes { get; set; }

        internal int TotalNodes { get; set; }

        internal long PassthroughTokens { get; set; }

        internal long TotalTokens { get; set; }

        /// <summary>
        /// Tokens under nodes that are verbatim by design — identifiers, literals, variables.
        /// Not a coverage gap: no handler will ever be written for them.
        /// </summary>
        internal long ByDesignTokens { get; set; }

        /// <summary>
        /// Per-file coverage percentages. The corpus-wide figure is token-weighted and so is
        /// dominated by a handful of enormous procedures; the median says what a typical file
        /// looks like. Both are true and they answer different questions.
        /// </summary>
        internal List<double> PerFileCoverage { get; } = [];

        private readonly Dictionary<string, (int Nodes, long Tokens, HashSet<string> Files)> _byType = [];

        /// <summary>
        /// Node type to report individual passthrough instances for, or null.
        /// </summary>
        /// <remarks>
        /// The histogram says <em>which</em> construct is costing the most coverage; it says nothing
        /// about <em>why</em>. That gap cost real time: OUTPUT clauses were assumed to be most of
        /// what kept INSERT at rank 1, and handling them moved it by 0.1 points, because the actual
        /// blocker was something else entirely. Reading the constructs is the only way to know.
        /// </remarks>
        internal string? WhyType { get; set; }

        internal List<(string File, int Tokens, string Snippet)> WhySamples { get; } = [];

        private readonly Dictionary<string, (int Nodes, long Tokens, HashSet<string> Files, HashSet<string> Types)>
            _byGuard = [];

        /// <summary>
        /// Attributes declined text to the exact <c>Passthrough(…)</c> call site that declined it.
        /// </summary>
        /// <remarks>
        /// The complement to the node-type histogram. A type at the top of that ranking might need a
        /// handler written, or might already have one that keeps bailing out — and the fix is
        /// completely different in each case. Guard attribution is what tells them apart, and three
        /// of the largest coverage gains so far came from guards that were declining more than they
        /// were protecting.
        /// </remarks>
        internal void RecordGuard(string guard, string typeName, int tokens, string file)
        {
            if (!_byGuard.TryGetValue(guard, out var entry))
            {
                entry = (0, 0, [], []);
            }

            entry.Files.Add(file);
            entry.Types.Add(typeName);
            _byGuard[guard] = (entry.Nodes + 1, entry.Tokens + tokens, entry.Files, entry.Types);
        }

        private void PrintGuards(int show)
        {
            if (_byGuard.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"=== declined text by guard — which bail-out, not which node type ({_byGuard.Count} guards) ===");
            Console.WriteLine("     rank  guard (handler:line)                  nodes    tokens   %verbatim  files  node types");

            var rank = 0;
            foreach (var (guard, entry) in _byGuard.OrderByDescending(e => e.Value.Tokens).Take(show))
            {
                rank++;
                var share = PassthroughTokens > 0 ? (double)entry.Tokens / PassthroughTokens * 100 : 0;
                var types = entry.Types.Count <= 2
                    ? string.Join(", ", entry.Types.OrderBy(t => t, StringComparer.Ordinal))
                    : $"{entry.Types.Count} types";

                Console.WriteLine(
                    $"     {rank,4}  {guard,-36} {entry.Nodes,6:N0} {entry.Tokens,9:N0}"
                    + $"  {share,8:F1}%  {entry.Files.Count,5}  {types}");
            }
        }

        internal void Record(string typeName, int tokens, string file)
        {
            if (!_byType.TryGetValue(typeName, out var entry))
            {
                entry = (0, 0, []);
            }

            entry.Files.Add(file);
            _byType[typeName] = (entry.Nodes + 1, entry.Tokens + tokens, entry.Files);
        }

        private void PrintHistogram(int show)
        {
            if (_byType.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"=== passthrough by node type — roots of verbatim subtrees ({_byType.Count} types) ===");
            Console.WriteLine("     rank  node type                             nodes    tokens   %verbatim  files");

            var ranked = _byType.OrderByDescending(e => e.Value.Tokens).ToList();
            var rank = 0;
            long shownTokens = 0;

            foreach (var (type, entry) in ranked.Take(show))
            {
                rank++;
                shownTokens += entry.Tokens;
                var share = PassthroughTokens > 0 ? (double)entry.Tokens / PassthroughTokens * 100 : 0;
                Console.WriteLine($"     {rank,4}  {type,-36} {entry.Nodes,6:N0} {entry.Tokens,9:N0}  {share,8:F1}%  {entry.Files.Count,5}");
            }

            if (ranked.Count > show)
            {
                var rest = PassthroughTokens - shownTokens;
                var restShare = PassthroughTokens > 0 ? (double)rest / PassthroughTokens * 100 : 0;
                Console.WriteLine($"           {$"+ {ranked.Count - show} more types",-36} {string.Empty,6} {rest,9:N0}  {restShare,8:F1}%");
            }

            PrintGuards(show);
            PrintKeywordSliceIdentifiers(show);
            PrintWhy(show);

            // The cumulative view is what makes this actionable: it says how few handlers it takes
            // to cover most of the remaining text.
            Console.WriteLine();
            long cumulative = 0;
            var milestones = new[] { 50.0, 75.0, 90.0 };
            var next = 0;
            for (var i = 0; i < ranked.Count && next < milestones.Length; i++)
            {
                cumulative += ranked[i].Value.Tokens;
                var percent = PassthroughTokens > 0 ? (double)cumulative / PassthroughTokens * 100 : 0;
                while (next < milestones.Length && percent >= milestones[next])
                {
                    Console.WriteLine($"  top {i + 1,3} types cover {milestones[next]:F0}% of verbatim text");
                    next++;
                }
            }
        }

        /// <summary>
        /// The largest individual instances of <see cref="WhyType"/>, biggest first.
        /// </summary>
        private void PrintWhy(int show)
        {
            if (WhyType is null)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"=== largest {WhyType} passthroughs ({WhySamples.Count} total) ===");

            foreach (var (file, tokens, snippet) in WhySamples.OrderByDescending(s => s.Tokens).Take(show))
            {
                Console.WriteLine($"  [{tokens,6:N0} tokens] {Path.GetFileName(file)}");
                Console.WriteLine($"      {snippet}");
            }
        }

        /// <summary>
        /// Identifier token texts that appear inside keyword slices, corpus-wide.
        /// </summary>
        /// <remarks>
        /// Printed by <c>--keywords</c>. This is the population the non-reserved-keyword vocabulary is
        /// drawn from *and* the population it must leave alone — index names and collations live in the
        /// same slices — so the two have to be told apart by reading them, not by recalling which words
        /// T-SQL reserves.
        /// </remarks>
        internal Dictionary<string, int> KeywordSliceIdentifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

        private void PrintKeywordSliceIdentifiers(int show)
        {
            if (KeywordSliceIdentifiers.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"=== identifier-typed words inside keyword slices ({KeywordSliceIdentifiers.Count} distinct) ===");

            foreach (var (word, count) in KeywordSliceIdentifiers.OrderByDescending(e => e.Value).Take(show))
            {
                Console.WriteLine($"  {count,8:N0}  {word}");
            }
        }

        internal List<(string File, string Detail)> ParseFailed { get; } = [];

        internal List<(string File, string Detail)> Refused { get; } = [];

        internal List<(string File, string Detail)> Crashed { get; } = [];

        internal List<(string File, string Detail)> Unstable { get; } = [];

        internal void Print(TimeSpan elapsed, int show, bool verbose)
        {
            var total = Formatted + ParseFailed.Count + Refused.Count + Crashed.Count + Empty + ExpectedFailure;

            Console.WriteLine("=== results ===");
            Line("formatted", Formatted, total);
            Line("  of which already formatted", AlreadyFormatted, total);
            Line("expected failure (negative tests)", ExpectedFailure, total);
            Line("parse failed (input's problem)", ParseFailed.Count, total);
            Line("REFUSED (maxdop's problem)", Refused.Count, total);
            Line("CRASHED (maxdop's problem)", Crashed.Count, total);
            Line("NOT IDEMPOTENT (maxdop's problem)", Unstable.Count, total);
            Line("empty", Empty, total);

            Console.WriteLine();

            // Token-weighted coverage is the honest number. The node ratio below understates
            // passthrough, because a passed-through subtree's descendants are never dispatched
            // and so never counted.
            if (TotalTokens > 0)
            {
                var formatted = TotalTokens - PassthroughTokens;
                Console.WriteLine($"coverage    : {formatted:N0} of {TotalTokens:N0} significant tokens under formatted "
                    + $"constructs ({(double)formatted / TotalTokens * 100:F1}% corpus-wide, token-weighted)");
                Console.WriteLine($"              {PassthroughTokens:N0} unformatted "
                    + $"({(double)PassthroughTokens / TotalTokens * 100:F1}%) awaiting handlers; "
                    + $"{ByDesignTokens:N0} ({(double)ByDesignTokens / TotalTokens * 100:F1}%) verbatim by design "
                    + "(identifiers, literals, variables — never a coverage gap)");

                if (PerFileCoverage.Count > 0)
                {
                    var sorted = PerFileCoverage.Order().ToList();
                    var median = sorted[sorted.Count / 2];
                    var fullyFormatted = sorted.Count(c => c >= 99.9);
                    Console.WriteLine($"              per-file median {median:F1}%; "
                        + $"{fullyFormatted:N0} of {sorted.Count:N0} files fully formatted; "
                        + $"p25 {sorted[sorted.Count / 4]:F1}%, p75 {sorted[sorted.Count * 3 / 4]:F1}%");
                }
            }

            if (TotalNodes > 0)
            {
                Console.WriteLine($"dispatched  : {PassthroughNodes:N0} of {TotalNodes:N0} dispatched nodes "
                    + $"({(double)PassthroughNodes / TotalNodes * 100:F1}%) were verbatim subtree roots");
            }

            Console.WriteLine($"throughput  : {TotalBytes / 1024.0 / 1024.0:F1} MB in {elapsed.TotalSeconds:F1}s "
                + $"({TotalBytes / 1024.0 / Math.Max(0.001, elapsed.TotalSeconds):F0} KB/s)");

            PrintHistogram(show > 2 ? show : 25);

            Section("CRASHES", Crashed, show, verbose);
            Section("REFUSALS", Refused, show, verbose);
            Section("IDEMPOTENCY FAILURES", Unstable, show, verbose);
            Section("parse failures", ParseFailed, show, verbose);
        }

        private static void Line(string label, int count, int total)
        {
            var percent = total > 0 ? (double)count / total * 100 : 0;
            Console.WriteLine($"  {label,-36} {count,6:N0}  {percent,5:F1}%");
        }

        private static void Section(string title, List<(string File, string Detail)> items, int show, bool verbose)
        {
            if (items.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"=== {title} ({items.Count}) ===");

            // Group by the shape of the message so 200 instances of one bug read as one bug.
            foreach (var group in items.GroupBy(i => Signature(i.Detail)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  [{group.Count(),4}] {group.Key}");
                foreach (var (file, detail) in group.Take(verbose ? show : 2))
                {
                    Console.WriteLine($"         {Path.GetFileName(file)}: {Trim(detail, 150)}");
                }
            }
        }

        /// <summary>Collapses a diagnostic to its shape, so identical bugs group together.</summary>
        private static string Signature(string detail)
        {
            var digitsStripped = new string(detail.Select(c => char.IsDigit(c) ? '#' : c).ToArray());
            var quoted = digitsStripped.IndexOf('"', StringComparison.Ordinal);
            if (quoted > 0)
            {
                digitsStripped = digitsStripped[..quoted];
            }

            return Trim(digitsStripped, 120);
        }

        private static string Trim(string text, int max)
        {
            var single = text.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
            return single.Length <= max ? single : single[..max] + "…";
        }
    }
}
