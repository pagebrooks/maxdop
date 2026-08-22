using Maxdop.Core.Comments;
using Maxdop.Core.Formatting;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Tests;

/// <summary>
/// Ratchets on grammar coverage, measured against ScriptDom's type system rather than against a
/// corpus.
/// </summary>
/// <remarks>
/// A corpus can only ever sample the grammar: it tells you about the constructs someone happened to
/// write, and says nothing about the ones nobody in your sample used. ScriptDom's AST is a closed
/// set — every node derives from <see cref="TSqlFragment"/> — so the complete surface can simply be
/// enumerated, and the interesting numbers are its size and which parts of it the printer declines.
/// <para>Both tests are baselines, deliberately. Neither asserts a target; they assert that the
/// numbers only move when someone means them to, so a ScriptDom upgrade that adds node types, or a
/// change that quietly stops handling one, shows up as a failure rather than as silence.</para>
/// </remarks>
public class GrammarCoverageTests
{
    /// <summary>
    /// Node types the printer declines on the committed corpus — the handler shopping list.
    /// </summary>
    /// <remarks>
    /// Only subtree <em>roots</em> arrive here: a declined node's children are never dispatched, so
    /// these are exactly the types a new handler would have to cover. Shrinking this list is the
    /// point; growing it without intent is the regression this guards against.
    /// </remarks>
    /// <para>Empty, and worth keeping that way: every construct in the hand-written corpus is
    /// modelled today. A type appearing here means either a handler regressed or the corpus grew
    /// syntax nothing covers yet — both worth knowing the moment it happens.</para>
    private static readonly HashSet<string> DeclinedOnCorpus = [];

    /// <summary>
    /// Every concrete node type in the grammar, against a committed list of them.
    /// </summary>
    /// <remarks>
    /// Names, not a count. A parser upgrade that adds twelve node types should say <em>which</em>
    /// twelve — that list is the review, and the shortlist of syntax that might now need a handler.
    /// A count only says something moved and leaves the diffing to whoever is holding the failure.
    /// <para>Regenerate with <c>GrammarSurface.txt</c> when the bump is intentional, and put the new
    /// names in the commit message.</para>
    /// </remarks>
    [Fact]
    public void TheGrammarSurfaceMatchesTheCommittedList()
    {
        var actual = typeof(TSqlFragment).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TSqlFragment)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GrammarSurface.txt"))
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(expected, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var removed = expected.Except(actual, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            added.Count == 0 && removed.Count == 0,
            $"the grammar surface moved: {actual.Count} concrete node types, was {expected.Count}.\n"
                + $"  new syntax to consider ({added.Count}): {string.Join(", ", added)}\n"
                + $"  gone ({removed.Count}): {string.Join(", ", removed)}\n"
                + "  Regenerate GrammarSurface.txt once the upgrade is reviewed.");
    }

    [Fact]
    public void NoNewNodeTypeIsDeclinedOnTheCommittedCorpus()
    {
        var declined = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(AppContext.BaseDirectory, "corpus"),
            "*.sql",
            SearchOption.AllDirectories))
        {
            foreach (var name in DeclinedTypesIn(File.ReadAllText(path)))
            {
                declined.Add(name);
            }
        }

        var appeared = declined.Except(DeclinedOnCorpus, StringComparer.Ordinal).ToList();
        var fixedSince = DeclinedOnCorpus.Except(declined, StringComparer.Ordinal).ToList();

        Assert.True(
            appeared.Count == 0,
            $"newly declined node type(s): {string.Join(", ", appeared)}. Either a handler stopped "
                + "covering them or the corpus grew a construct nothing models yet.");

        Assert.True(
            fixedSince.Count == 0,
            $"no longer declined: {string.Join(", ", fixedSince)}. Good news — take them out of "
                + $"{nameof(DeclinedOnCorpus)} so the ratchet holds at the new level.");
    }

    private static IEnumerable<string> DeclinedTypesIn(string sql)
    {
        var options = FormatOptions.Default;
        var parser = ParserFactory.Create(options);
        using var reader = new StringReader(sql);
        var root = parser.Parse(reader, out var errors);

        // A corpus file that does not parse is the corpus's problem, and the negative-test fixtures
        // are there on purpose. Coverage is only meaningful for what the parser understood.
        if (errors.Count > 0 || root is null)
        {
            yield break;
        }

        var roots = new List<TSqlFragment>();
        var printer = new SqlPrinter(root, CommentAttacher.Attach(root), options, roots);
        _ = printer.Print(root);

        foreach (var node in roots)
        {
            // Identifiers, literals and variables are emitted verbatim by design and always sit
            // inside formatted output — never a coverage gap, and the same rule the corpus tool
            // uses to keep them out of its shopping list.
            if (!SqlPrinter.IsVerbatimByDesign(node))
            {
                yield return node.GetType().Name;
            }
        }
    }
}
