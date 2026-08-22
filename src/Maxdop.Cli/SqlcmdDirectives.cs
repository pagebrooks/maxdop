namespace Maxdop.Cli;

/// <summary>
/// Recognises SQLCMD directives, so "could not parse" can say why.
/// </summary>
/// <remarks>
/// <c>:setvar</c>, <c>:r</c> and <c>:connect</c> are sqlcmd/SSMS client commands, not T-SQL, and no
/// T-SQL parser accepts them. They are common in deployment scripts, so without this
/// the message would be a bare syntax error at a line that looks perfectly fine to the author.
/// <para>Checked only <em>after</em> a parse has already failed. Scanning first would risk a false
/// positive skipping a file that would otherwise have formatted — the text could sit inside a string
/// literal — and here the worst a false positive can do is word the message badly.</para>
/// </remarks>
internal static class SqlcmdDirectives
{
    private static readonly string[] Known =
    [
        "setvar", "connect", "on", "out", "error", "exit", "quit", "help", "list", "listvar",
        "reset", "ed", "serverlist", "perftrace", "xml", "r",
    ];

    /// <summary>The first SQLCMD directive in the text, or null.</summary>
    internal static string? Find(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.AsSpan().TrimStart();

            // A single colon then a letter. `::` is excluded because that is T-SQL — a type method
            // call like `geography::Point(…)` — and would otherwise match.
            if (trimmed.Length < 2 || trimmed[0] != ':' || !char.IsAsciiLetter(trimmed[1]))
            {
                continue;
            }

            var word = trimmed[1..];
            var end = 0;
            while (end < word.Length && char.IsAsciiLetter(word[end]))
            {
                end++;
            }

            var directive = word[..end].ToString();
            foreach (var known in Known)
            {
                if (directive.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    return ":" + directive.ToLowerInvariant();
                }
            }
        }

        return null;
    }
}
