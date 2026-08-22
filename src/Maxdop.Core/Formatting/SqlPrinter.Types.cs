using Maxdop.Core.Printing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Type conversions — <c>CAST</c>, <c>CONVERT</c>, <c>TRY_CAST</c>, <c>TRY_CONVERT</c> — and the
/// data type references they name.
/// </summary>
/// <remarks>
/// <c>CastCall</c> and <c>ConvertCall</c> together held 12% of the corpus's remaining verbatim text.
/// The value is not in the call itself, which is short, but in what it was blocking: passthrough is
/// subtree-scoped, so an unhandled <c>CAST</c> froze its whole argument —
/// <c>CAST(SUM(CONVERT(bigint, dp.wait_time)) / 1000 / 86400 AS …)</c> came out untouched in its
/// entirety. Handling the four calls is what lets the printer reach inside them.
/// <para>The four are siblings in ScriptDom rather than a hierarchy, so each needs its own dispatch
/// entry, but <c>CAST</c>/<c>TRY_CAST</c> and <c>CONVERT</c>/<c>TRY_CONVERT</c> share an
/// implementation. The introducing keyword is read from the tokens rather than written out, because
/// <c>CAST</c> and <c>TRY_CAST</c> lex as identifiers while <c>CONVERT</c> and <c>TRY_CONVERT</c>
/// have token types of their own — a distinction there is no reason for a handler to know.</para>
/// </remarks>
public sealed partial class SqlPrinter
{
    /// <summary><c>CAST(&lt;expression&gt; AS &lt;type&gt;)</c>.</summary>
    private Doc PrintCastLike(TSqlFragment call, ScalarExpression? parameter, DataTypeReference? dataType)
    {
        if (parameter is null || dataType is null)
        {
            return Passthrough(call);
        }

        // `CAST(` or `TRY_CAST(`, then `AS` between the operands, then `)`. Verified rather than
        // assumed, so any spelling this does not model becomes verbatim instead of mangled output.
        if (!Compact(SignificantTextBetween(call.FirstTokenIndex, EffectiveFirstToken(parameter) - 1))
                .EndsWith("CAST(", StringComparison.OrdinalIgnoreCase)
            || !TextBetween(parameter, dataType).Equals("AS", StringComparison.OrdinalIgnoreCase)
            || SignificantTextBetween(dataType.LastTokenIndex + 1, call.LastTokenIndex) != ")")
        {
            return Passthrough(call);
        }

        return Doc.Group(Doc.Concat(
            // `CAST(` and `TRY_CAST(` lex as identifiers; the region holds only the keyword and its
            // parenthesis, so it is a keyword position. Likewise the `AS` between the operands.
            Keywords(call.FirstTokenIndex, EffectiveFirstToken(parameter) - 1),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Print(parameter),
                Doc.Text(" "),
                Keywords(parameter.LastTokenIndex + 1, EffectiveFirstToken(dataType) - 1),
                Doc.Text(" "),
                Print(dataType))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    /// <summary><c>PARSE(&lt;string&gt; AS &lt;type&gt; [USING &lt;culture&gt;])</c>.</summary>
    /// <remarks>
    /// Shaped like <c>CAST</c> — operand, <c>AS</c>, type — with an optional <c>USING &lt;culture&gt;</c>
    /// after it, which is why it cannot share <see cref="PrintCastLike"/>: that helper requires the type
    /// to be the last thing before the closing parenthesis.
    /// </remarks>
    private Doc PrintParseCall(ParseCall call)
    {
        var value = call.StringValue;
        var dataType = call.DataType;

        if (value is null || dataType is null)
        {
            return Passthrough(call);
        }

        var afterType = call.Culture is null ? call.LastTokenIndex : EffectiveFirstToken(call.Culture) - 1;

        if (!Compact(SignificantTextBetween(call.FirstTokenIndex, EffectiveFirstToken(value) - 1))
                .Equals("PARSE(", StringComparison.OrdinalIgnoreCase)
            || !TextBetween(value, dataType).Equals("AS", StringComparison.OrdinalIgnoreCase)
            || (call.Culture is null
                ? SignificantTextBetween(dataType.LastTokenIndex + 1, call.LastTokenIndex) != ")"
                : !SignificantTextBetween(dataType.LastTokenIndex + 1, afterType)
                    .Equals("USING", StringComparison.OrdinalIgnoreCase)
                  || SignificantTextBetween(call.Culture.LastTokenIndex + 1, call.LastTokenIndex) != ")"))
        {
            return Passthrough(call);
        }

        var parts = new List<Doc>
        {
            Keywords(call.FirstTokenIndex, EffectiveFirstToken(value) - 1),
            Print(value),
            Doc.Text(" "),
            Keywords(value.LastTokenIndex + 1, EffectiveFirstToken(dataType) - 1),
            Doc.Text(" "),
            Print(dataType),
        };

        if (call.Culture is not null)
        {
            parts.Add(Doc.Text(" "));
            parts.Add(Keywords(dataType.LastTokenIndex + 1, afterType));
            parts.Add(Doc.Text(" "));
            parts.Add(Print(call.Culture));
        }

        parts.Add(Doc.Text(")"));

        return Doc.Group(Doc.Concat(parts));
    }

    /// <summary><c>CONVERT(&lt;type&gt;, &lt;expression&gt;[, &lt;style&gt;])</c>.</summary>
    private Doc PrintConvertLike(
        TSqlFragment call,
        DataTypeReference? dataType,
        ScalarExpression? parameter,
        ScalarExpression? style)
    {
        if (dataType is null || parameter is null)
        {
            return Passthrough(call);
        }

        // The type comes first here, the reverse of CAST — and the operands are comma-separated
        // rather than joined by AS, so the two cannot share one emission.
        var arguments = style is null
            ? new List<TSqlFragment> { dataType, parameter }
            : [dataType, parameter, style];

        if (!Compact(SignificantTextBetween(call.FirstTokenIndex, dataType.FirstTokenIndex - 1))
                .EndsWith("CONVERT(", StringComparison.OrdinalIgnoreCase)
            || !SeparatedBy(arguments)
            || SignificantTextBetween(arguments[^1].LastTokenIndex + 1, call.LastTokenIndex) != ")")
        {
            return Passthrough(call);
        }

        return Doc.Group(Doc.Concat(
            Keywords(call.FirstTokenIndex, dataType.FirstTokenIndex - 1),
            Doc.Indent(Doc.Concat(
                Doc.SoftLine,
                Doc.Join(Doc.Concat(Doc.Text(","), Doc.Line), arguments.Select(Print)))),
            Doc.SoftLine,
            Doc.Text(")")));
    }

    /// <summary>
    /// A data type name with any precision, scale or length — <c>NVARCHAR(50)</c>,
    /// <c>DECIMAL(18, 4)</c>, <c>dbo.MyType</c>, <c>XML</c>.
    /// </summary>
    /// <remarks>
    /// Emitted as one token slice. Splitting it into name and parameters would gain nothing: there
    /// is no layout decision to make inside a data type, and the six <c>DataTypeReference</c>
    /// subclasses spell their parameters differently enough that reassembling them is all risk.
    /// <para><b>A built-in type name is a keyword position; a user-defined one is not.</b> Both lex as
    /// <c>Identifier</c>, which is why <c>nvarchar</c> used to survive <see cref="KeywordCase.Upper"/>
    /// untouched. But ScriptDom has already made the distinction for us: a
    /// <c>SqlDataTypeReference</c> is one of the built-in types, so its name cannot be an object name
    /// and <see cref="Keywords"/> may recase it. A <c>UserDataTypeReference</c> names a real type in a
    /// real schema and is left exactly as written.</para>
    /// <para>Only the type name benefits — the length and precision inside the parentheses are numeric
    /// literals, which <see cref="Keywords"/> never touches. <c>NVARCHAR(MAX)</c> works because
    /// <c>MAX</c> lexes as an identifier in a position that cannot hold a name.</para>
    /// </remarks>
    private Doc PrintDataType(DataTypeReference type) =>
        NoCommentsIn(type.FirstTokenIndex, type.LastTokenIndex)
            ? type is SqlDataTypeReference
                ? Keywords(type.FirstTokenIndex, type.LastTokenIndex)
                : CasedTokens(type.FirstTokenIndex, type.LastTokenIndex)
            : Passthrough(type);
}
