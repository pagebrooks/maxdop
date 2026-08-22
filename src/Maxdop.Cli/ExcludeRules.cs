using Maxdop.Core.Formatting;

namespace Maxdop.Cli;

/// <summary>
/// Answers "should maxdop skip this file", from the <c>exclude</c> list of the config that governs it.
/// </summary>
/// <remarks>
/// <para>Patterns are matched relative to the directory of the <c>.maxdop.json</c> that declared
/// them, not to the working directory. That is the only choice that survives being run from
/// somewhere else: a pattern reading <c>db/generated/**</c> means the same thing whether CI invokes
/// maxdop from the repository root or an editor invokes it from wherever the user opened it.</para>
/// <para>Resolution is cached per directory for the same reason option resolution is — a run over a
/// whole repository would otherwise re-read and re-parse the same config once per file.</para>
/// </remarks>
internal sealed class ExcludeRules(string? explicitConfigPath)
{
    private static readonly (string Root, string[] Patterns) None = (string.Empty, []);

    private readonly Dictionary<string, (string Root, string[] Patterns)> _byDirectory = [];

    /// <summary>
    /// Whether <paramref name="path"/> is excluded by the config governing it.
    /// </summary>
    /// <remarks>
    /// A config that cannot be read is reported rather than treated as "no exclusions": silently
    /// formatting files a broken config meant to protect is the failure worth avoiding here.
    /// </remarks>
    internal bool IsExcluded(string path, out string? error)
    {
        error = null;

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);

        if (!TryResolve(directory, out var rules, out error))
        {
            return false;
        }

        if (rules.Patterns.Length == 0)
        {
            return false;
        }

        var relative = Path.GetRelativePath(rules.Root, full);

        // A file above the config that declared the patterns cannot be described by them, and
        // GetRelativePath would hand back a `..` walk that no pattern is written to match.
        return !relative.StartsWith("..", StringComparison.Ordinal)
            && Glob.MatchesAny(rules.Patterns, relative);
    }

    private bool TryResolve(string? directory, out (string Root, string[] Patterns) rules, out string? error)
    {
        error = null;
        var key = directory ?? string.Empty;

        if (_byDirectory.TryGetValue(key, out rules))
        {
            return true;
        }

        rules = None;

        var configPath = explicitConfigPath ?? ConfigFile.Discover(directory);
        if (configPath is null)
        {
            _byDirectory[key] = rules;
            return true;
        }

        if (!ConfigFile.TryRead(configPath, out var config, out error))
        {
            return false;
        }

        var patterns = config.Exclude ?? [];
        rules = (Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty, patterns);

        _byDirectory[key] = rules;
        return true;
    }
}
