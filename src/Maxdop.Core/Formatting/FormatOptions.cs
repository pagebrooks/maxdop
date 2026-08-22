using Maxdop.Core.Printing;

namespace Maxdop.Core.Formatting;

public enum KeywordCase
{
    /// <summary>`SELECT`, `CREATE PROCEDURE`. The T-SQL community default.</summary>
    Upper,

    /// <summary>`select`, `create procedure`.</summary>
    Lower,
}

/// <summary>
/// The config surface — what `.maxdop.json` deserialises into. Kept deliberately small
/// (about ten options, capped deliberately): opinionated, with escape hatches, not 50 knobs.
/// </summary>
public sealed record FormatOptions
{
    public static FormatOptions Default { get; } = new();

    /// <summary>Layout parameters handed to <see cref="DocPrinter"/>.</summary>
    public PrintOptions Print { get; init; } = PrintOptions.Default;

    public KeywordCase KeywordCase { get; init; } = KeywordCase.Upper;

    /// <summary>
    /// Consecutive blank lines to preserve between statements and batches. Zero collapses all
    /// vertical whitespace; the default keeps one, which is what authors use to group related
    /// statements and is information the formatter has no business discarding.
    /// </summary>
    public int MaxBlankLines { get; init; } = 1;

    /// <summary>Commas at line start (`, col`) rather than line end (`col,`).</summary>
    public bool LeadingCommas { get; init; }

    /// <summary>
    /// Put every column of a multi-column SELECT list on its own line, even when they would fit
    /// on one. Teams that review SQL in diffs often want this: a one-line list makes adding a
    /// column look like a change to every column.
    /// </summary>
    public bool AlwaysBreakSelectList { get; init; }

    /// <summary>
    /// Put the keyword of a WHERE or HAVING on a line of its own and every top-level predicate on
    /// its own line beneath it, operators right-aligned so the predicates start in one column:
    /// <code>
    /// WHERE
    ///         o.OrderDate >= @Start
    ///     AND o.OrderDate &lt; @End
    /// </code>
    /// </summary>
    /// <remarks>
    /// The practical argument for it is editing rather than looks: with one predicate per line a
    /// filter can be commented out while working on a query. Note it cannot help the *first*
    /// predicate — commenting that leaves <c>WHERE AND …</c> — which is why the <c>WHERE 1 = 1</c>
    /// idiom exists. maxdop will not insert that for you; it is a rewrite, not a layout choice.
    /// </remarks>
    public bool AlwaysBreakWhere { get; init; }

    /// <summary>
    /// ScriptDom grammar version: 80, 90, 100…180, or 0 for the Fabric DW grammar. Teams pin
    /// this per repo so a 2016-target codebase is not silently reformatted using 2025 syntax
    /// rules: parser versioning is a feature, not an accident.
    /// </summary>
    public int ParserVersion { get; init; } = 180;

    /// <summary>
    /// How `"` is lexed at offset zero. Off matches sqlcmd/SSMS script defaults; on treats
    /// double quotes as identifier delimiters. This is a correctness switch, not a style one —
    /// it changes what the input <em>means</em>.
    /// </summary>
    public bool InitialQuotedIdentifiers { get; init; }
}
