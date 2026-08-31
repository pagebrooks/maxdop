using System.Collections.Frozen;

namespace Maxdop.Core.Syntax;

/// <summary>
/// SQL Server's configuration, cursor and system global variables — <c>@@ROWCOUNT</c>,
/// <c>@@FETCH_STATUS</c>, <c>@@TRANCOUNT</c>.
/// </summary>
/// <remarks>
/// <para><b>The <c>@@</c> prefix is not the proof, which is the whole reason this list exists.</b>
/// SQL Server accepts <c>DECLARE @@MyVar INT</c> — a local variable that merely looks like a system
/// one — and ScriptDom resolves a later reference to it by spelling, not by scope: in an expression
/// position <c>@@MyVar</c> arrives as a <c>GlobalVariableExpression</c>, the same node
/// <c>@@ROWCOUNT</c> arrives as. Recasing every such node would rename that variable under a
/// case-sensitive collation, which is precisely the silent corruption the round-trip verifier is
/// there to prevent. Membership here is what separates the two.</para>
/// <para>Closed and small, unlike the built-in function list: SQL Server has added no new global
/// variable in well over a decade, and the ones below are the documented set. Anything not on it
/// keeps the author's casing.</para>
/// <para>Shares <see cref="Maxdop.Core.Formatting.FormatOptions.RecaseBuiltInFunctions"/> with the function
/// names rather than adding a switch of its own — the proof has the same shape, a vocabulary rather
/// than the parse tree, so the same answer should govern both.</para>
/// </remarks>
public static class SqlGlobalVariables
{
    private static readonly FrozenSet<string> Names = new[]
    {
        // Configuration
        "@@DATEFIRST", "@@DBTS", "@@LANGID", "@@LANGUAGE", "@@LOCK_TIMEOUT", "@@MAX_CONNECTIONS",
        "@@MAX_PRECISION", "@@NESTLEVEL", "@@OPTIONS", "@@REMSERVER", "@@SERVERNAME",
        "@@SERVICENAME", "@@SPID", "@@TEXTSIZE", "@@VERSION",

        // Cursor
        "@@CURSOR_ROWS", "@@FETCH_STATUS",

        // System
        "@@ERROR", "@@IDENTITY", "@@PACK_RECEIVED", "@@PROCID", "@@ROWCOUNT", "@@TRANCOUNT",

        // System statistical
        "@@CONNECTIONS", "@@CPU_BUSY", "@@IDLE", "@@IO_BUSY", "@@PACKET_ERRORS", "@@PACK_SENT",
        "@@TIMETICKS", "@@TOTAL_ERRORS", "@@TOTAL_READ", "@@TOTAL_WRITE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is one of the global variable names.</summary>
    public static bool Contains(string? name) => name is not null && Names.Contains(name);
}
