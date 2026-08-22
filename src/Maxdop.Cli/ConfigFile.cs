using System.Text.Json;
using System.Text.Json.Serialization;
using Maxdop.Core.Formatting;
using Maxdop.Core.Printing;

namespace Maxdop.Cli;

/// <summary>
/// The on-disk shape of <c>.maxdop.json</c>.
/// </summary>
/// <remarks>
/// <para>Deliberately flat and deliberately small — about ten options, capped on purpose. Flat
/// because the nesting inside <see cref="FormatOptions"/> is an implementation detail nobody editing
/// a config file should have to know; small because the headline feature is team consistency, and
/// fifty knobs are how a formatter stops being opinionated.</para>
/// <para>Every property is nullable so that "absent" is distinguishable from "set to the default
/// value". Without that, a config file could not express a setting whose value happens to equal the
/// default, and merging an explicit config over defaults would silently reset options.</para>
/// </remarks>
internal sealed record MaxdopConfig
{
    [JsonPropertyName("maxWidth")]
    public int? MaxWidth { get; init; }

    [JsonPropertyName("indentSize")]
    public int? IndentSize { get; init; }

    [JsonPropertyName("useTabs")]
    public bool? UseTabs { get; init; }

    [JsonPropertyName("keywordCase")]
    public string? KeywordCase { get; init; }

    [JsonPropertyName("leadingCommas")]
    public bool? LeadingCommas { get; init; }

    [JsonPropertyName("alwaysBreakSelectList")]
    public bool? AlwaysBreakSelectList { get; init; }

    [JsonPropertyName("alwaysBreakWhere")]
    public bool? AlwaysBreakWhere { get; init; }

    [JsonPropertyName("maxBlankLines")]
    public int? MaxBlankLines { get; init; }

    [JsonPropertyName("parserVersion")]
    public string? ParserVersion { get; init; }

    [JsonPropertyName("initialQuotedIdentifiers")]
    public bool? InitialQuotedIdentifiers { get; init; }

    /// <summary>
    /// Glob patterns for files maxdop should skip, relative to this config file's directory.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in a separate ignore file because there is already exactly one place a
    /// repository states how it wants its SQL treated, and a second file would be one more thing to
    /// find, commit and keep in step. It is not a <see cref="FormatOptions"/> setting: it decides
    /// which files are formatted, not how any of them is laid out, so it never reaches the formatter.
    /// </remarks>
    [JsonPropertyName("exclude")]
    public string[]? Exclude { get; init; }

    /// <summary>
    /// Folds this config over the defaults, reporting the first setting it cannot make sense of.
    /// </summary>
    /// <remarks>
    /// A bad value is an error rather than a silent fallback. A config file that a typo turns into
    /// "formatter runs with different settings than the team agreed" defeats the entire point of
    /// having one.
    /// </remarks>
    internal bool TryApply(FormatOptions baseline, out FormatOptions options, out string? error)
    {
        error = null;

        var keywordCase = baseline.KeywordCase;
        if (KeywordCase is not null)
        {
            switch (KeywordCase.ToLowerInvariant())
            {
                case "upper": keywordCase = Maxdop.Core.Formatting.KeywordCase.Upper; break;
                case "lower": keywordCase = Maxdop.Core.Formatting.KeywordCase.Lower; break;
                default:
                    options = baseline;
                    error = $"keywordCase must be \"upper\" or \"lower\", not \"{KeywordCase}\".";
                    return false;
            }
        }

        var parserVersion = baseline.ParserVersion;
        if (ParserVersion is not null && !ParserFactory.TryParseVersion(ParserVersion, out parserVersion))
        {
            options = baseline;
            error = $"parserVersion \"{ParserVersion}\" is not recognised.";
            return false;
        }

        if (MaxWidth is <= 0)
        {
            options = baseline;
            error = "maxWidth must be greater than zero.";
            return false;
        }

        if (IndentSize is < 0)
        {
            options = baseline;
            error = "indentSize cannot be negative.";
            return false;
        }

        if (MaxBlankLines is < 0)
        {
            options = baseline;
            error = "maxBlankLines cannot be negative.";
            return false;
        }

        options = baseline with
        {
            Print = baseline.Print with
            {
                MaxWidth = MaxWidth ?? baseline.Print.MaxWidth,
                IndentSize = IndentSize ?? baseline.Print.IndentSize,
                UseTabs = UseTabs ?? baseline.Print.UseTabs,
            },
            KeywordCase = keywordCase,
            LeadingCommas = LeadingCommas ?? baseline.LeadingCommas,
            AlwaysBreakSelectList = AlwaysBreakSelectList ?? baseline.AlwaysBreakSelectList,
            AlwaysBreakWhere = AlwaysBreakWhere ?? baseline.AlwaysBreakWhere,
            MaxBlankLines = MaxBlankLines ?? baseline.MaxBlankLines,
            ParserVersion = parserVersion,
            InitialQuotedIdentifiers = InitialQuotedIdentifiers ?? baseline.InitialQuotedIdentifiers,
        };

        return true;
    }
}

/// <summary>
/// Source-generated JSON contract for <see cref="MaxdopConfig"/>.
/// </summary>
/// <remarks>
/// Source generation rather than reflection because the shipping artifact is NativeAOT:
/// reflection-based <c>JsonSerializer</c> calls trip the AOT analyzer, and with
/// <c>TreatWarningsAsErrors</c> that is a build failure rather than a runtime surprise in a
/// customer's CI. Comments and trailing commas are allowed because people hand-edit this file.
/// </remarks>
[JsonSourceGenerationOptions(
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MaxdopConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

internal static class ConfigFile
{
    internal const string FileName = ".maxdop.json";

    /// <summary>
    /// Finds the nearest <c>.maxdop.json</c> at or above <paramref name="startDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Walking up from the *file being formatted* rather than from the working directory is what
    /// makes an editor integration behave: VS Code's process runs wherever the user opened it, so
    /// resolving against cwd would apply one repo's settings to another's files. This is why the
    /// extension has to pass <c>--stdin-filepath</c>.
    /// </remarks>
    internal static string? Discover(string? startDirectory)
    {
        var directory = startDirectory is null ? null : new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads and validates a config file without folding it over any defaults.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryLoad"/> because exclusion rules have to be known before a file is
    /// opened, which is well before there is a <see cref="FormatOptions"/> to merge into.
    /// </remarks>
    internal static bool TryRead(string path, out MaxdopConfig config, out string? error)
    {
        error = null;
        config = new MaxdopConfig();

        MaxdopConfig? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.MaxdopConfig);
        }
        catch (JsonException e)
        {
            error = $"{path}: not valid JSON — {e.Message}";
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {e.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = $"{path}: config file is empty.";
            return false;
        }

        config = parsed;
        return true;
    }

    internal static bool TryLoad(string path, FormatOptions baseline, out FormatOptions options, out string? error)
    {
        options = baseline;

        if (!TryRead(path, out var config, out error))
        {
            return false;
        }

        if (!config.TryApply(baseline, out options, out var applyError))
        {
            error = $"{path}: {applyError}";
            return false;
        }

        error = null;
        return true;
    }
}
