**No formatting changes.** A file formatted by 0.1.2 comes out of 0.1.3 byte for byte the same.
What changed is what happens around the format: how `--write` puts the result on disk, what the VS
Code extension tells you when it declines a file, and how much of the safety net is actually tested.

## `--write` can no longer destroy the file it was formatting

Until now `--write` used `File.WriteAllBytes`, which truncates the file and then writes it. A write
that did not finish — a full volume, a killed process, a container out of memory — left the file
truncated. Every gate upstream exists so maxdop never writes output it cannot prove safe, and then
the write itself could lose the file for a reason none of those gates can see. Disk-full is the
realistic trigger, and it is likeliest exactly when `--write` is running across a large repository.

Output now goes to a temporary file beside the original, which is renamed over it only once the new
bytes are complete. If anything fails first, the original is untouched and the temporary file is
removed.

- **Permissions carry over.** On Linux and macOS the file mode is copied before the rename, so a
  `0640` file does not come back `0644`. On Windows the replace goes through `ReplaceFile`, which keeps
  the file's ACLs and attributes.
- **Symlinks are followed, not replaced.** A symlink named on the command line has its target
  rewritten, and stays a symlink.
- **Hard links are the trade.** An atomic replace necessarily gives the file a new inode, so another
  hard link to it keeps the old content. Writing in place instead would risk a half-old, half-new file,
  and for SQL that is worse than truncation: a hybrid can still parse and mean something different.
  Git makes the same call.

The temporary file is named `.<file>.maxdop-<random>.tmp`, so a concurrent `*.sql` walk never picks
it up.

## VS Code: a declined file says so

A file that does not parse is handed back untouched, and until now the only trace of that was a line
in the **maxdop** output channel — so pressing Format appeared to do nothing. The status bar now shows
**⚠ maxdop: parse error** while that file is the active one. Its tooltip carries the parser's message,
and clicking it opens the output channel.

It is passive on purpose: no popup and no coloured background, since format-on-save runs on every
save while a statement is half typed. It clears as soon as the file formats, and it does not follow
you to other files.

The output channel is also available as **maxdop: Show Output** in the command palette.

## Testing the safety net itself

The refusal path — hand back the input rather than output that failed a gate — had quietly become
the one behaviour no test executed. A working printer cannot produce output that trips the gates, and
every construct that used to has been fixed, so the refusal machinery lost its coverage as the bugs
went away. A refusal that returned the rejected text instead of the input would have kept every
suite green.

0.1.3 tests it directly:

- **Refusal paths.** The gates are handed deliberately damaged output, and the tests assert the
  input comes back.
- **Batch seams.** The same for the check that runs after `GO`-separated batches are joined back
  together — for example, a batch whose trailing newline went missing and welded `END` onto the next
  `GO`.
- **Width sweep.** The committed corpus is formatted at every width in a range, not only the
  120-column default, and each result must pass the gates and be a fixed point.
- **Mutation testing.** A weekly job deliberately breaks the five gate files — inverting a
  comparison, dropping a negation — and fails if the tests stop noticing. It is scoped to the gates on
  purpose: a surviving mutant there means a gate could stop working with the suite still green.

## Distribution

- **WinGet.** `winget install --id pagebrooks.maxdop -e`. The package is in `microsoft/winget-pkgs`
  now. WinGet updates go through Microsoft's review, so a new version reaches it a little after the
  release rather than with it.
- **Open VSX.** The extension is published to [Open VSX](https://open-vsx.org/extension/pbrooks/maxdop)
  alongside the Visual Studio Marketplace, for VSCodium, Cursor and other editors that use it.
- **Homebrew.** The formula is now generated from each release's `SHA256SUMS` and pushed to the tap
  by the release workflow, rather than edited by hand.
