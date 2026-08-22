using System.IO.Enumeration;

namespace Maxdop.Cli;

/// <summary>
/// Turns the paths a caller gave into the list of files to format.
/// </summary>
/// <remarks>
/// <para>maxdop used to leave this to the shell, and the documented
/// <c>maxdop --check src/**/*.sql</c> quietly did the wrong thing as a result: <c>globstar</c> is off
/// in a default bash — including the shell GitHub Actions runs — so <c>**</c> collapses to a single
/// <c>*</c> and matches exactly one directory level. CI went green having looked at a fraction of the
/// repository, which is worse than not running at all, because it manufactures confidence. Windows
/// shells expand nothing, so the same command arrived as a literal string. Owning discovery is what
/// makes <c>maxdop --check src/</c> mean the same thing on every platform and every shell.</para>
/// <para>Exclusions apply to <em>every</em> source of paths — a directory walk, <c>--files-from</c>,
/// and files named directly on the command line alike. One rule is easier to reason about than three,
/// and the case that rule gets wrong (someone naming an excluded file and wondering why nothing
/// happened) is covered by saying so rather than by carving out an exception.</para>
/// </remarks>
internal sealed class PathExpander(ExcludeRules excludes)
{
    private const string SqlExtension = ".sql";

    /// <summary>
    /// Files found while walking directories, with dot-directories such as <c>.git</c> skipped.
    /// </summary>
    /// <remarks>
    /// <para><c>AttributesToSkip</c> adds <c>ReparsePoint</c> to the default hidden-and-system, which
    /// on Unix already covers anything whose name begins with a dot. Symlinks are skipped because
    /// .NET follows them while recursing and has no cycle detection, so a link inside the tree walks
    /// straight out of it: <c>maxdop --write src/</c> over a checkout containing
    /// <c>src/link -&gt; /elsewhere</c> would rewrite files that are not in <c>src/</c>, or in the
    /// repository at all. That matters most where the tree is least trusted — a CI job formatting a
    /// pull request from a fork. Not following links is also what ripgrep and Prettier do by default,
    /// and a symlink named directly on the command line still works, because it never reaches this
    /// walk.</para>
    /// <para>Dot-directories are skipped <em>by name</em> rather than by relying on
    /// <c>FileAttributes.Hidden</c>, which is not portable: .NET reports any Unix name beginning with
    /// a dot as hidden, while on Windows the attribute has to have been set on the directory. Git
    /// does set it on a real <c>.git</c>, so leaning on the attribute mostly worked and failed
    /// exactly where it was least visible — a Windows-only CI failure on a repository whose
    /// <c>.git</c> was created by something other than git. Deciding from the name gives one answer
    /// on every platform, which is the same reason matching is case-insensitive: a repository must
    /// not check differently because of where CI happens to run it.</para>
    /// </remarks>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly List<string> _files = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Files to format, in a stable order.</summary>
    internal IReadOnlyList<string> Files => _files;

    /// <summary>How many files a directory walk or <c>--files-from</c> list dropped as excluded.</summary>
    internal int ExcludedCount { get; private set; }

    /// <summary>
    /// Adds one path: a directory to walk, or a single file.
    /// </summary>
    /// <param name="path">The path as the caller wrote it, kept for messages.</param>
    /// <param name="named">
    /// Whether a person wrote this path rather than it coming out of a walk. Only affects whether an
    /// exclusion is announced; it does not change what gets excluded.
    /// </param>
    internal bool TryAdd(string path, bool named, out string? error)
    {
        error = null;

        if (Directory.Exists(path))
        {
            return TryAddDirectory(path, out error);
        }

        if (!File.Exists(path))
        {
            // Exit 2, not 1: a path that is not there is a mistake in the invocation, not a verdict
            // about anyone's SQL. A CI job with a typo in it should say so rather than pass.
            error = $"{path}: no such file or directory.";
            return false;
        }

        if (excludes.IsExcluded(path, out error))
        {
            ExcludedCount++;

            if (named)
            {
                // Never silent for a path someone typed. Prettier's convention is to skip quietly,
                // and the resulting "why did nothing happen" is a support question worth one line of
                // stderr to avoid.
                Console.Error.WriteLine($"maxdop: {path}: skipped — excluded by .maxdop.json");
            }

            return true;
        }

        return error is null && Add(path);
    }

    /// <summary>Sorts the collected files so two runs over the same tree agree.</summary>
    /// <remarks>
    /// Directory enumeration order is whatever the filesystem says, which differs between ext4, APFS
    /// and NTFS. Unsorted output would make a CI log impossible to diff against a local run.
    /// </remarks>
    internal void Finish() => _files.Sort(StringComparer.Ordinal);

    private bool TryAddDirectory(string directory, out string? error)
    {
        error = null;

        foreach (var file in SqlFilesUnder(directory))
        {
            if (excludes.IsExcluded(file, out error))
            {
                ExcludedCount++;
                continue;
            }

            if (error is not null)
            {
                return false;
            }

            Add(file);
        }

        return true;
    }

    /// <summary>
    /// Enumerates <c>*.sql</c> below a directory, not descending into anything dot-prefixed.
    /// </summary>
    /// <remarks>
    /// <see cref="FileSystemEnumerable{T}"/> rather than <see cref="Directory.EnumerateFiles(string)"/>
    /// because only this one can decline to recurse: the simpler API would walk all of <c>.git</c>
    /// and every <c>node_modules</c> and then discard the results, which is the same answer for
    /// considerably more filesystem.
    /// </remarks>
    private static FileSystemEnumerable<string> SqlFilesUnder(string directory) =>
        new FileSystemEnumerable<string>(
            directory,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            WalkOptions)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && !IsDotPrefixed(entry.FileName)
                && entry.FileName.EndsWith(SqlExtension, StringComparison.OrdinalIgnoreCase),

            ShouldRecursePredicate = static (ref FileSystemEntry entry) => !IsDotPrefixed(entry.FileName),
        };

    private static bool IsDotPrefixed(ReadOnlySpan<char> name) => name.Length > 0 && name[0] == '.';

    private bool Add(string path)
    {
        // Overlapping arguments — a directory and a file inside it, say — must not format the same
        // file twice, which under --write would mean writing it twice and under --check would report
        // it twice.
        if (_seen.Add(Path.GetFullPath(path)))
        {
            _files.Add(path);
        }

        return true;
    }
}
