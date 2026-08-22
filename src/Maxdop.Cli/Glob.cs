namespace Maxdop.Cli;

/// <summary>
/// Matches the patterns in <c>.maxdop.json</c>'s <c>exclude</c> list against repo-relative paths.
/// </summary>
/// <remarks>
/// <para>Hand-written rather than translated to <see cref="System.Text.RegularExpressions.Regex"/>,
/// which nothing else in this project links. Under NativeAOT the regex engine is a megabyte-scale
/// dependency to pull in for a few dozen glob patterns, and the compiled engine needs the dynamic
/// code generation AOT forbids in the first place.</para>
/// <para><b>Matching is case-insensitive on every platform, deliberately.</b> The point of a
/// committed config file is that a repository behaves the same way whoever opens it; a pattern that
/// excluded a file on the Windows developer's machine and missed it on the Linux CI runner would be
/// the exact failure the config file exists to prevent.</para>
/// <para>The syntax is the familiar gitignore subset — but note that nothing here reads
/// <c>.gitignore</c> itself, and maxdop has no dependency on git.</para>
/// <list type="bullet">
/// <item><c>*</c> matches within one path segment; <c>?</c> matches one character in one segment.</item>
/// <item><c>**</c> matches zero or more whole segments, so <c>db/**/gen.sql</c> spans directories.</item>
/// <item>A pattern with no <c>/</c> matches at any depth: <c>*.gen.sql</c> finds them anywhere.</item>
/// <item>A pattern containing <c>/</c> is anchored to the directory of the config that declared it.</item>
/// <item>Naming a directory excludes everything beneath it, with or without a trailing slash.</item>
/// </list>
/// </remarks>
internal static class Glob
{
    /// <summary>
    /// Whether <paramref name="relativePath"/> is matched by <paramref name="pattern"/>.
    /// </summary>
    /// <param name="pattern">A pattern from an <c>exclude</c> list.</param>
    /// <param name="relativePath">
    /// A path relative to the directory the pattern was declared in, using either separator.
    /// </param>
    internal static bool Matches(string pattern, string relativePath)
    {
        var patternSegments = Split(pattern.Trim(), out var anchored);
        if (patternSegments.Length == 0)
        {
            return false;
        }

        // A bare name applies at any depth, the way `*.log` does in a .gitignore. A pattern that
        // spells out a path is anchored instead, because someone who wrote `db/gen` meant that one.
        if (!anchored && patternSegments.Length == 1)
        {
            patternSegments = ["**", .. patternSegments];
        }

        var pathSegments = Split(relativePath, out _);

        // Naming a directory excludes what is inside it. Rather than a separate rule for patterns
        // with a trailing slash, every ancestor of the path is offered to the pattern: if `generated`
        // matches the first segment of `generated/dbo/x.sql`, the file is excluded.
        for (var depth = pathSegments.Length; depth >= 1; depth--)
        {
            if (MatchSegments(patternSegments, 0, pathSegments, 0, depth))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any pattern matches, for the common "is this file excluded" question.</summary>
    internal static bool MatchesAny(IReadOnlyList<string> patterns, string relativePath)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (Matches(patterns[i], relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Split(string value, out bool anchored)
    {
        var normalised = value.Replace('\\', '/');
        anchored = normalised.StartsWith('/');

        // Empty entries drop out, which also handles a trailing slash, a leading one, and the `//`
        // that turns up when a pattern is pasted together from two halves.
        return normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Matches pattern segments against the first <paramref name="limit"/> path segments.
    /// </summary>
    private static bool MatchSegments(string[] pattern, int p, string[] path, int s, int limit)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                // A trailing `**` has nothing left to constrain, so everything below here matches.
                if (p + 1 == pattern.Length)
                {
                    return true;
                }

                // Zero or more segments: try every split point rather than assuming the shortest.
                // `**` is rare and paths are shallow, so the branching costs nothing in practice.
                for (var next = s; next <= limit; next++)
                {
                    if (MatchSegments(pattern, p + 1, path, next, limit))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s >= limit || !MatchSegment(pattern[p], path[s]))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == limit;
    }

    /// <summary>
    /// Matches one segment, where <c>*</c> and <c>?</c> never cross a separator.
    /// </summary>
    /// <remarks>
    /// Iterative with a single backtrack point rather than recursion: the pathological patterns that
    /// make naive wildcard matching exponential (<c>a*a*a*a*b</c>) are cheap here, and a config file
    /// is not a place where a stack overflow should be reachable.
    /// </remarks>
    private static bool MatchSegment(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, resume = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool SameChar(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
