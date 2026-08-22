namespace Maxdop.Cli;

/// <summary>
/// Reads the path list <c>--files-from</c> points at.
/// </summary>
/// <remarks>
/// <para>This is how maxdop serves "only the files that changed" without knowing what a commit is.
/// The caller decides what changed and pipes the answer in:</para>
/// <code>
/// git diff --name-only --diff-filter=ACM -z | maxdop --check --files-from -
/// </code>
/// <para>which works identically with <c>hg status</c>, a Perforce changelist, a CI action's
/// changed-files output, or <c>find -newer</c>. maxdop stays a function from paths to verdicts, so
/// there is no git binary to install in the CI image, nothing to break in a shallow or detached
/// checkout, and nothing that assumes the repository is a git repository at all.</para>
/// </remarks>
internal static class FileList
{
    /// <summary>Argument value meaning "read the list from stdin".</summary>
    internal const string StdinMarker = "-";

    /// <summary>
    /// Reads paths from a file, or from stdin when <paramref name="path"/> is <c>-</c>.
    /// </summary>
    /// <remarks>
    /// The separator is detected rather than configured: a list containing a NUL came from a
    /// <c>-z</c>/<c>-print0</c> producer and is split there, and anything else is split on line
    /// endings. That means the safe form costs the caller a flag they were probably already passing,
    /// and the ordinary form still works — without a second maxdop flag to get wrong.
    /// </remarks>
    internal static bool TryRead(string path, out List<string> files, out string? error)
    {
        error = null;
        files = [];

        string content;
        try
        {
            content = path == StdinMarker
                ? Console.In.ReadToEnd()
                : File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"--files-from {path}: {e.Message}";
            return false;
        }

        var separators = content.Contains('\0') ? new[] { '\0' } : ['\n'];

        foreach (var entry in content.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            // Trailing '\r' survives the split on a CRLF list, and a path with a stray carriage
            // return in it does not exist, so the error would be about the wrong thing entirely.
            var file = entry.Trim('\r').Trim();
            if (file.Length > 0)
            {
                files.Add(file);
            }
        }

        return true;
    }
}
