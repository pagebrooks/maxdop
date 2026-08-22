namespace Maxdop.Core.Printing;

/// <summary>
/// Layout parameters for <see cref="DocPrinter"/>. These are the subset of
/// <c>.maxdop.json</c> options that the generic layout engine consumes; everything else
/// (keyword case, comma position, semicolon policy) is decided by node handlers when they
/// build the <see cref="Doc"/>, not here.
/// </summary>
public sealed record PrintOptions
{
    public static readonly PrintOptions Default = new();

    /// <summary>Column the layout engine tries to keep lines within.</summary>
    public int MaxWidth { get; init; } = 100;

    /// <summary>Columns added per level of <see cref="Doc.Indent"/>.</summary>
    public int IndentSize { get; init; } = 4;

    /// <summary>Emit a tab per indent level instead of <see cref="IndentSize"/> spaces.</summary>
    public bool UseTabs { get; init; }

    /// <summary>
    /// Columns a tab is assumed to occupy, for width arithmetic only. Only consulted when
    /// <see cref="UseTabs"/> is set.
    /// </summary>
    public int TabWidth { get; init; } = 4;

    /// <summary>
    /// Line terminator. The CLI sets this from the detected terminator of the input file so
    /// a CRLF file stays CRLF, an encoding invariant.
    /// </summary>
    public string NewLine { get; init; } = "\n";
}
