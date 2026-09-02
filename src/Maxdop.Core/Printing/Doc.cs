namespace Maxdop.Core.Printing;

/// <summary>
/// A node in the document IR. Node handlers build a <see cref="Doc"/> tree that describes
/// layout <em>structure</em> — where breaks are permitted, what nests, what changes when a
/// break happens — and <see cref="DocPrinter"/> decides where breaks actually land.
/// Handlers never make width decisions themselves.
/// </summary>
/// <remarks>
/// The primitive set follows Wadler ("A Prettier Printer", 1998) as extended by Prettier.
/// Instances are immutable from a caller's perspective and safe to share between trees;
/// the one mutable bit (<see cref="DocGroup.ShouldBreak"/>) is set only by
/// break propagation, which is derived purely from tree shape and therefore idempotent.
/// </remarks>
public abstract class Doc
{
    internal Doc()
    {
    }

    /// <summary>Emits nothing. Useful as a neutral element.</summary>
    public static Doc Empty { get; } = new DocText(string.Empty);

    /// <summary>A space when the enclosing group is flat, a newline when it breaks.</summary>
    public static Doc Line { get; } = new DocLine(LineKind.Space);

    /// <summary>Nothing when the enclosing group is flat, a newline when it breaks.</summary>
    public static Doc SoftLine { get; } = new DocLine(LineKind.Soft);

    /// <summary>
    /// Always a newline, and forces every enclosing group to break. Use for boundaries that
    /// are never joined: statement separators, <c>GO</c>, <c>BEGIN</c>/<c>END</c> bodies.
    /// </summary>
    public static Doc HardLine { get; } = new DocLine(LineKind.Hard);

    /// <summary>
    /// Always a newline, emitted with <em>no</em> indentation, and forces enclosing groups to
    /// break. This is how verbatim text keeps its original shape — see <see cref="Verbatim"/>.
    /// </summary>
    public static Doc LiteralLine { get; } = new DocLine(LineKind.Literal);

    /// <summary>
    /// Emits nothing but forces every enclosing group to break. Needed for trailing line
    /// comments: the comment text itself goes in a <see cref="LineSuffix"/> (so it cannot be
    /// pushed off the line) while this forces the surrounding construct to break, because
    /// anything following a <c>--</c> comment on the same line would be swallowed by it.
    /// </summary>
    public static Doc BreakParent { get; } = new DocBreakParent();

    /// <summary>
    /// Flushes any pending <see cref="LineSuffix"/> content by forcing a newline if some is
    /// pending, and does nothing otherwise. Place before text that must not end up after a
    /// trailing comment on the same line.
    /// </summary>
    public static Doc LineSuffixBoundary { get; } = new DocLineSuffixBoundary();

    /// <summary>Removes trailing spaces and tabs already written to the current line.</summary>
    public static Doc Trim { get; } = new DocTrim();

    /// <summary>Literal output. Must not contain newlines — use <see cref="Verbatim"/> for that.</summary>
    public static Doc Text(string value) =>
        string.IsNullOrEmpty(value) ? Empty : new DocText(value);

    public static Doc Concat(params Doc[] parts) =>
        parts is null || parts.Length == 0 ? Empty : parts.Length == 1 ? parts[0] : new DocConcat(parts);

    public static Doc Concat(IEnumerable<Doc> parts) =>
        Concat(parts is null ? [] : [.. parts]);

    /// <summary>
    /// A unit the layout engine tries to fit on one line. If the whole group fits in the
    /// remaining width, every <see cref="Line"/> and <see cref="SoftLine"/> inside it
    /// collapses; otherwise they all become newlines. Groups are the only place a layout
    /// decision is made.
    /// </summary>
    /// <param name="contents">The doc the group wraps.</param>
    /// <param name="shouldBreak">Force the broken layout without measuring.</param>
    /// <param name="id">
    /// Optional identity, so an <see cref="IfBreak"/> or <see cref="IndentIfBreak"/> elsewhere
    /// can key off <em>this</em> group's decision rather than its own nearest enclosing group.
    /// </param>
    public static Doc Group(Doc contents, bool shouldBreak = false, GroupId? id = null) =>
        new DocGroup(contents, shouldBreak, id);

    /// <summary>Adds one indent level to every newline emitted inside <paramref name="contents"/>.</summary>
    public static Doc Indent(Doc contents) => new DocIndent(contents);

    /// <summary>
    /// Adds exactly <paramref name="width"/> columns rather than one indent level. This is the
    /// primitive behind column-alignment options such as aligning <c>AS</c> clauses.
    /// </summary>
    public static Doc Align(int width, Doc contents) =>
        width == 0 ? contents : new DocAlign(width, contents);

    /// <summary>
    /// Emits <paramref name="whenBroken"/> if the reference group broke and
    /// <paramref name="whenFlat"/> if it stayed flat. The reference group is the one named by
    /// <paramref name="groupId"/>, or the nearest enclosing group when that is null.
    /// Trailing vs. leading commas and optional trailing semicolons are both this primitive.
    /// </summary>
    public static Doc IfBreak(Doc whenBroken, Doc? whenFlat = null, GroupId? groupId = null) =>
        new DocIfBreak(whenBroken, whenFlat ?? Empty, groupId);

    /// <summary>
    /// Indents <paramref name="contents"/> only if the group named by
    /// <paramref name="groupId"/> broke. Lets a continuation line indent under a construct
    /// that stayed on one line without over-indenting when it did not.
    /// </summary>
    public static Doc IndentIfBreak(Doc contents, GroupId groupId) =>
        new DocIndentIfBreak(contents, groupId);

    /// <summary>
    /// Defers <paramref name="contents"/> to the end of the current line, past whatever else
    /// is printed. This is how a trailing <c>-- comment</c> stays glued to the end of its line
    /// no matter how the surrounding layout breaks.
    /// </summary>
    public static Doc LineSuffix(Doc contents) => new DocLineSuffix(contents);

    /// <summary>
    /// Emits <paramref name="contents"/> but contributes nothing to the width measurement, so it
    /// cannot influence where lines break.
    /// </summary>
    /// <remarks>
    /// This exists for comments. A trailing comment deferred with <see cref="LineSuffix"/> is
    /// already invisible to the fit test, but one that must stay inline — because code follows it on
    /// its line — was being measured, and that made layout depend on comment length. Worse, it made
    /// layout <em>unstable</em>: reformatting can move the code that follows a comment onto another
    /// line, which reclassifies the comment as end-of-line, which changes whether its width counted,
    /// which changes where the surrounding code breaks. Formatting stopped being a fixed point.
    /// <para>Excluding comments from measurement means a line carrying one may exceed the maximum
    /// width. That is the right trade and the conventional one — a comment is not code, and letting
    /// it push code around is worse than letting it overhang.</para>
    /// </remarks>
    public static Doc Unmeasured(Doc contents) => new DocUnmeasured(contents);

    /// <summary>Interposes <paramref name="separator"/> between <paramref name="parts"/>.</summary>
    public static Doc Join(Doc separator, IEnumerable<Doc> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var result = new List<Doc>();
        foreach (var part in parts)
        {
            if (result.Count > 0)
            {
                result.Add(separator);
            }

            result.Add(part);
        }

        return Concat(result);
    }

    /// <summary>
    /// Emits <paramref name="value"/> exactly as given, preserving its own line structure and
    /// refusing all re-indentation. This backs the graceful-passthrough invariant:
    /// an unhandled construct is sliced out of the token stream and emitted
    /// through here, so the formatter degrades to "leaves it alone" rather than mangling it.
    /// </summary>
    public static Doc Verbatim(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Empty;
        }

        // Normalise CRLF so a single split yields clean lines; the printer re-emits whichever
        // terminator PrintOptions.NewLine specifies.
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 1)
        {
            return Text(lines[0]);
        }

        var parts = new List<Doc>(lines.Length * 2);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                parts.Add(LiteralLine);
            }

            parts.Add(Text(lines[i]));
        }

        return Concat(parts);
    }
}

/// <summary>
/// Identity for a <see cref="Doc.Group"/>, letting <see cref="Doc.IfBreak"/> and
/// <see cref="Doc.IndentIfBreak"/> depend on that specific group's decision. Compared by
/// reference, so each instance names exactly one group.
/// </summary>
public sealed class GroupId
{
    public GroupId(string? name = null) => Name = name;

    public string? Name { get; }

    public override string ToString() => Name ?? "group";
}

internal enum LineKind
{
    /// <summary>Collapses to nothing when flat.</summary>
    Soft,

    /// <summary>Collapses to a single space when flat.</summary>
    Space,

    /// <summary>Never collapses; forces enclosing groups to break.</summary>
    Hard,

    /// <summary>Never collapses, never indents; forces enclosing groups to break.</summary>
    Literal,
}

internal sealed class DocText(string value) : Doc
{
    internal string Value { get; } = value;

    internal int Width { get; } = DocPrinter.StringWidth(value);
}

internal sealed class DocConcat(IReadOnlyList<Doc> parts) : Doc
{
    internal IReadOnlyList<Doc> Parts { get; } = parts;
}

internal sealed class DocLine(LineKind kind) : Doc
{
    internal LineKind Kind { get; } = kind;

    /// <summary>Hard and literal lines never collapse and propagate a break outward.</summary>
    internal bool IsHard => Kind is LineKind.Hard or LineKind.Literal;
}

internal sealed class DocGroup(Doc contents, bool shouldBreak, GroupId? id) : Doc
{
    internal Doc Contents { get; } = contents;

    /// <summary>
    /// Set at construction by the caller, or during break propagation when this group is found
    /// to contain something that cannot be flattened.
    /// </summary>
    internal bool ShouldBreak { get; set; } = shouldBreak;

    internal GroupId? Id { get; } = id;
}

internal sealed class DocIndent(Doc contents) : Doc
{
    internal Doc Contents { get; } = contents;
}

internal sealed class DocAlign(int width, Doc contents) : Doc
{
    internal int Width { get; } = width;

    internal Doc Contents { get; } = contents;
}

internal sealed class DocIfBreak(Doc whenBroken, Doc whenFlat, GroupId? groupId) : Doc
{
    internal Doc WhenBroken { get; } = whenBroken;

    internal Doc WhenFlat { get; } = whenFlat;

    internal GroupId? GroupId { get; } = groupId;
}

internal sealed class DocIndentIfBreak(Doc contents, GroupId groupId) : Doc
{
    internal Doc Contents { get; } = contents;

    internal GroupId GroupId { get; } = groupId;
}

internal sealed class DocLineSuffix(Doc contents) : Doc
{
    internal Doc Contents { get; } = contents;
}

internal sealed class DocUnmeasured(Doc contents) : Doc
{
    internal Doc Contents { get; } = contents;
}

internal sealed class DocLineSuffixBoundary : Doc;

internal sealed class DocBreakParent : Doc;

internal sealed class DocTrim : Doc;
