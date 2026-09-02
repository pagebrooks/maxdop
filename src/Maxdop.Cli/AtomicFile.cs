namespace Maxdop.Cli;

/// <summary>
/// Replaces a file's contents without ever leaving it half-written.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>File.WriteAllBytes</c> opens with <c>FileMode.Create</c>, which
/// truncates and then writes. A write that does not finish — a full volume, a killed process, a
/// container out of memory — leaves the file truncated. Every gate upstream exists so maxdop never
/// writes output it cannot prove safe, and then the write itself could destroy the file for a reason
/// none of those gates can see. Disk-full is the realistic trigger, and it is likeliest exactly when
/// <c>--write</c> is running across a large repository.</para>
///
/// <para><b>The trade, which is not avoidable.</b> POSIX offers no way to atomically replace a file's
/// <em>contents</em>. <c>rename(2)</c> is the only atomic replace and it necessarily swaps the
/// directory entry to a new inode, so hard links to the old content keep the old content.
/// <c>File.Replace</c> does not help here — measured on Linux it also produces a new inode; it is
/// used on Windows only, where it maps to <c>ReplaceFile</c> and preserves the destination's ACLs and
/// attributes that <c>MoveFileEx</c> drops.</para>
///
/// <para>The alternative is worse rather than better. Writing in place keeps the inode and the hard
/// links, but a failure partway leaves a file mixing old and new bytes — and for SQL that is more
/// dangerous than truncation, because a hybrid file may still parse and mean something different,
/// where a truncated one fails loudly. Silent wrongness is the failure this project is built around.
/// Git makes the same call, using temp-and-rename throughout and accepting the inode churn.</para>
///
/// <para><b>Symlinks are followed rather than replaced.</b> <see cref="PathExpander"/> skips links
/// while walking a directory, but a link named directly on the command line never reaches that walk
/// and does arrive here. Renaming over it would silently turn someone's symlink into a regular file —
/// a regression the old non-atomic write did not have — so the target is resolved first and the
/// rename happens there.</para>
///
/// <para><b>Hard links are documented, not detected.</b> The link count is not exposed by the BCL and
/// would need <c>stat(2)</c> through a platform split. Hard-linked <c>.sql</c> files are vanishingly
/// rare, and git breaks them on checkout anyway.</para>
///
/// <para><b>No fsync.</b> The threat model is a process that dies or a volume that fills, and the
/// rename already covers both: the original is untouched until the new bytes are complete on disk's
/// own terms. Surviving a power cut would additionally need the temp file <em>and</em> its directory
/// flushed, and paying that on every file of a repository-wide run costs more than it buys.</para>
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>, replacing it in one step.
    /// </summary>
    /// <remarks>
    /// On failure the original is left exactly as it was and the temporary file is removed. The
    /// exception is allowed out so the caller can report it the way any other write failure is
    /// reported.
    /// </remarks>
    internal static void Write(string path, byte[] bytes)
    {
        var target = ResolveLink(path);
        var temp = TempBeside(target);

        try
        {
            File.WriteAllBytes(temp, bytes);

            // Before the rename, not after: the temp file is created under the process umask, so a
            // 0640 file would silently come back 0644. Ownership cannot be carried across without
            // privileges maxdop does not have and does not want.
            CopyUnixMode(from: target, to: temp);

            Replace(temp, target);
        }
        catch
        {
            Discard(temp);
            throw;
        }
    }

    /// <summary>
    /// The path the write should land on: the final target of a symlink chain, or the path itself.
    /// </summary>
    private static string ResolveLink(string path)
    {
        // Nothing to resolve, and asking would throw. maxdop only ever rewrites a file it has just
        // read, so this is defensive rather than load-bearing — but a helper that throws on a path
        // that does not exist yet is a trap for the next caller.
        if (!File.Exists(path))
        {
            return path;
        }

        // Null for anything that is not a link. returnFinalTarget walks a chain of them, so a link to
        // a link resolves to the file at the end rather than to the middle.
        var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
        return resolved is null ? path : resolved.FullName;
    }

    /// <summary>
    /// A unique temporary path in the target's own directory.
    /// </summary>
    /// <remarks>
    /// Same directory because a rename is only atomic within one filesystem — the system temp
    /// directory is routinely on another. The <c>.tmp</c> suffix is what keeps it out of a concurrent
    /// directory walk, which matches on <c>*.sql</c>; the leading dot is only tidiness. The random
    /// component matters once <c>--write</c> runs files in parallel, and costs nothing before then.
    /// </remarks>
    private static string TempBeside(string target)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        var unique = Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal);
        var name = $".{Path.GetFileName(target)}.maxdop-{unique}.tmp";

        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    private static void CopyUnixMode(string from, string to)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(from))
        {
            return;
        }

        File.SetUnixFileMode(to, File.GetUnixFileMode(from));
    }

    private static void Replace(string temp, string target)
    {
        // File.Replace preserves the destination's ACLs and attributes on Windows, which a plain move
        // discards. It also requires the destination to exist, so a first write falls back.
        if (OperatingSystem.IsWindows() && File.Exists(target))
        {
            File.Replace(temp, target, destinationBackupFileName: null);
            return;
        }

        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Removes a temporary file, never masking the failure that led here.
    /// </summary>
    private static void Discard(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The original is intact, which is the promise that matters. A leftover temp file is
            // untidy; throwing from a catch block would replace the real diagnostic with this one.
        }
    }
}
