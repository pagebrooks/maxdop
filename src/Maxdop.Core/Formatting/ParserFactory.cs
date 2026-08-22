using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Maxdop.Core.Formatting;

/// <summary>
/// Maps a configured parser version onto one of ScriptDom's grammars.
/// </summary>
/// <remarks>
/// All twelve grammars ship in the one assembly, so supporting every SQL Server compatibility
/// level from 2000 to 2025 costs essentially nothing — which is what makes `--parser-version`
/// a feature rather than a maintenance burden.
/// </remarks>
public static class ParserFactory
{
    /// <summary>Version numbers accepted by <see cref="FormatOptions.ParserVersion"/>.</summary>
    public static IReadOnlyList<int> SupportedVersions { get; } =
        [0, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180];

    /// <summary>Newest grammar available, and the default.</summary>
    public const int LatestVersion = 180;

    /// <summary>The Fabric Data Warehouse grammar, which has no compatibility-level number.</summary>
    public const int FabricDwVersion = 0;

    public static TSqlParser Create(FormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var quoted = options.InitialQuotedIdentifiers;
        return options.ParserVersion switch
        {
            FabricDwVersion => new TSqlFabricDWParser(quoted),
            80 => new TSql80Parser(quoted),
            90 => new TSql90Parser(quoted),
            100 => new TSql100Parser(quoted),
            110 => new TSql110Parser(quoted),
            120 => new TSql120Parser(quoted),
            130 => new TSql130Parser(quoted),
            140 => new TSql140Parser(quoted),
            150 => new TSql150Parser(quoted),
            160 => new TSql160Parser(quoted),
            170 => new TSql170Parser(quoted),
            180 => new TSql180Parser(quoted),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ParserVersion,
                $"Unsupported parser version. Supported: {string.Join(", ", SupportedVersions)}."),
        };
    }

    /// <summary>
    /// Maps a user-facing product year onto a grammar version, so config can say
    /// `"parserVersion": 2016` instead of `130`.
    /// </summary>
    public static bool TryParseVersion(string value, out int version)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (value.Trim().ToLowerInvariant())
        {
            case "fabricdw" or "fabric": version = FabricDwVersion; return true;
            case "latest": version = LatestVersion; return true;
            case "2000": version = 80; return true;
            case "2005": version = 90; return true;
            case "2008": version = 100; return true;
            case "2012": version = 110; return true;
            case "2014": version = 120; return true;
            case "2016": version = 130; return true;
            case "2017": version = 140; return true;
            case "2019": version = 150; return true;
            case "2022": version = 160; return true;
            case "2025": version = 170; return true;
            default:
                // Bare grammar numbers stay accepted, since ScriptDom's own naming uses them
                // and 180 has no announced product year yet.
                return int.TryParse(value, out version) && SupportedVersions.Contains(version);
        }
    }
}
