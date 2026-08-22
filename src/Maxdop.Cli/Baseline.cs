using System.Security.Cryptography;
using System.Text;

namespace Maxdop.Cli;

/// <summary>
/// The set of files allowed to be unformatted, so a formatter can be adopted without one enormous
/// commit.
/// </summary>
/// <remarks>
/// <para>The adoption problem this exists for: pointing <c>--check</c> at an established codebase
/// fails on every file at once, so nobody turns it on. The usual escape is to reformat everything in
/// a single commit, which destroys <c>git blame</c>, cannot be reviewed, and conflicts with every
/// branch in flight.</para>
/// <para>An entry records the SHA-256 of the file's bytes <em>as they are today</em>, unformatted.
/// <c>--check</c> forgives a file whose bytes still hash to that value. Edit it at all and the hash
/// stops matching, so the file has to be formatted to pass — which is the whole trick: the count only
/// ever goes down, and it goes down as people touch code they were already touching.</para>
/// <para>Keying on content rather than on history is also what keeps maxdop independent of git. A
/// baseline is a plain sorted text file, reviewable in a pull request and mergeable line by line;
/// nothing here shells out to a version control system or knows one exists.</para>
/// </remarks>
internal sealed class Baseline
{
    internal const string DefaultFileName = ".maxdop-baseline";

    private const string Header = "# maxdop baseline — files allowed to stay unformatted, newest format wins.";
    private const string Explanation =
        "# Each line is the SHA-256 of the file's current, unformatted bytes. Editing a file changes\n"
        + "# its hash, which removes it from this list and requires it to be formatted. Regenerate with\n"
        + "# `maxdop --write-baseline`. Entries only ever need deleting, never adding by hand.";

    private readonly Dictionary<string, string> _hashByPath;
    private readonly string _root;

    private Baseline(string root, Dictionary<string, string> hashByPath)
    {
        _root = root;
        _hashByPath = hashByPath;
    }

    /// <summary>Entries that were never consulted, meaning the file is gone or is now formatted.</summary>
    internal HashSet<string> Unused { get; } = [];

    internal int Count => _hashByPath.Count;

    /// <summary>
    /// Loads a baseline, treating a missing file as an empty one.
    /// </summary>
    /// <remarks>
    /// Missing is not an error so that the first <c>--check --baseline</c> run in a repository behaves
    /// exactly like a run without one, rather than failing on a file the user has not created yet.
    /// </remarks>
    internal static bool TryLoad(string path, out Baseline baseline, out string? error)
    {
        error = null;
        var root = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            baseline = new Baseline(root, entries);
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            baseline = new Baseline(root, entries);
            error = $"{path}: {e.Message}";
            return false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // "<hash><space><space><path>", the shape `sha256sum` writes, so the file can be checked
            // with ordinary tools. Split on the first run of spaces and keep the rest verbatim,
            // because a path may contain spaces.
            var split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split <= 0)
            {
                baseline = new Baseline(root, entries);
                error = $"{path}: line {i + 1} is not \"<hash>  <path>\".";
                return false;
            }

            entries[Normalise(line[(split + 2)..].Trim())] = line[..split].Trim();
        }

        baseline = new Baseline(root, entries);
        foreach (var key in entries.Keys)
        {
            baseline.Unused.Add(key);
        }

        return true;
    }

    /// <summary>
    /// Whether this file is allowed to be unformatted, given the bytes just read from it.
    /// </summary>
    internal bool Covers(string file, byte[] bytes)
    {
        var key = KeyFor(file);
        if (!_hashByPath.TryGetValue(key, out var recorded))
        {
            return false;
        }

        Unused.Remove(key);
        return string.Equals(recorded, Hash(bytes), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes a baseline covering exactly the files given.
    /// </summary>
    internal static bool TryWrite(string path, IReadOnlyList<(string File, byte[] Bytes)> entries, out string? error)
    {
        error = null;
        var root = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

        var rows = new List<(string Path, string Hash)>(entries.Count);
        foreach (var (file, bytes) in entries)
        {
            rows.Add((Relative(root, file), Hash(bytes)));
        }

        // By path, not by the composed line — which would sort by the hash that starts it, so
        // regenerating after a single edit would reshuffle the whole file and bury the one real
        // change. Sorting at all is what makes two machines that walked the tree in different orders
        // write the same bytes.
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        var text = new StringBuilder();
        text.Append(Header).Append('\n').Append(Explanation).Append('\n');
        foreach (var (path_, hash) in rows)
        {
            text.Append(hash).Append("  ").Append(path_).Append('\n');
        }

        try
        {
            // '\n' throughout rather than Environment.NewLine: this file is committed and read on
            // every platform, and a baseline that churns its line endings on a Windows regeneration
            // would conflict with itself in every pull request.
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: could not write — {e.Message}";
            return false;
        }

        return true;
    }

    private string KeyFor(string file) => Relative(_root, file);

    private static string Relative(string root, string file) =>
        Normalise(Path.GetRelativePath(root, Path.GetFullPath(file)));

    /// <summary>Forward slashes always, so a baseline written on Windows matches on Linux CI.</summary>
    private static string Normalise(string path) => path.Replace('\\', '/');

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
