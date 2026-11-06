namespace Maxdop.Core.Formatting;

// `-- maxdop-ignore` (or `/* maxdop-ignore */`) on its own line leaves the next statement exactly as
// written. SqlPrinter.Print checks for it and passes the statement through instead of descending.
//
// Recognised: either comment form, any case, optional whitespace after the opener, and anything after
// the name as long as it is separated by whitespace or a colon — so a reason can ride along:
//
//     -- maxdop-ignore: columns mirror the vendor's spec
//
// Not recognised: `maxdop-ignored`, `maxdop-ignore-next`, or the name anywhere but first. A directive
// that fires on a comment merely *mentioning* it would be worse than none, because nothing on screen
// says the statement below was skipped.
internal static class IgnoreDirective
{
    internal const string Name = "maxdop-ignore";

    internal static bool IsDirective(string commentText)
    {
        ReadOnlySpan<char> body;
        if (commentText.StartsWith("--", StringComparison.Ordinal))
        {
            body = commentText.AsSpan(2);
        }
        else if (commentText.Length >= 4
            && commentText.StartsWith("/*", StringComparison.Ordinal)
            && commentText.EndsWith("*/", StringComparison.Ordinal))
        {
            body = commentText.AsSpan(2, commentText.Length - 4);
        }
        else
        {
            return false;
        }

        body = body.TrimStart();
        if (!body.StartsWith(Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = body[Name.Length..];
        return rest.IsEmpty || char.IsWhiteSpace(rest[0]) || rest[0] == ':';
    }
}
