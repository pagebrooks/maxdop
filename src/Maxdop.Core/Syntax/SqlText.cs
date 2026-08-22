using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Syntax;

/// <summary>
/// Recovers original source text from the token stream.
/// </summary>
/// <remarks>
/// This is the primitive behind graceful passthrough. The spike proved the
/// token stream is lossless — concatenating every token's text reproduces the input byte for
/// byte — so any construct without a handler can be emitted exactly as the author wrote it by
/// slicing its token range. That is what makes "unhandled syntax degrades to leaving it alone"
/// a guarantee rather than an aspiration, and it is why the formatter can ship before it
/// covers all of T-SQL.
/// </remarks>
public static class SqlText
{
    /// <summary>Source text of <paramref name="fragment"/>, exactly as written.</summary>
    /// <returns>Empty string if the fragment has no assigned token range.</returns>
    public static string Of(TSqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        var tokens = fragment.ScriptTokenStream;
        return tokens is null ? string.Empty : Slice(tokens, fragment.FirstTokenIndex, fragment.LastTokenIndex);
    }

    /// <summary>
    /// Concatenates tokens from <paramref name="firstIndex"/> to <paramref name="lastIndex"/>
    /// inclusive. Out-of-range or inverted bounds yield an empty string rather than throwing:
    /// some ScriptDom nodes carry an unset <c>[-1..-1]</c> range, and a formatter must never
    /// crash on one.
    /// </summary>
    public static string Slice(IList<TSqlParserToken> tokens, int firstIndex, int lastIndex)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (firstIndex < 0 || lastIndex < firstIndex)
        {
            return string.Empty;
        }

        var from = Math.Min(firstIndex, tokens.Count - 1);
        var to = Math.Min(lastIndex, tokens.Count - 1);
        if (from < 0 || to < from)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            builder.Append(tokens[i].Text);
        }

        return builder.ToString();
    }
}
