using System.Collections.Frozen;

namespace Maxdop.Core.Syntax;

/// <summary>
/// The names SQL Server resolves as built-in functions when a call is written with no schema
/// qualifier — <c>LEN(x)</c>, <c>GETDATE()</c>, <c>ROW_NUMBER()</c>.
/// </summary>
/// <remarks>
/// <para>The list exists because ScriptDom does not distinguish a built-in scalar function from a
/// user-defined one: <c>GETDATE()</c> and <c>dbo.MyFunc()</c> are both a <c>FunctionCall</c> whose
/// <c>FunctionName</c> is a plain <c>Identifier</c>, so the printer has no structural proof of the
/// kind that <c>SqlDataTypeReference</c> gives it for <c>NVARCHAR</c>. Every other keyword position
/// in the printer is proved from the parse tree; this one is proved from a vocabulary, which is why
/// it is the only casing decision behind a config switch
/// (<c>FormatOptions.RecaseBuiltInFunctions</c>).</para>
/// <para><b>The vocabulary is not the whole proof.</b> The caller also requires the call to have no
/// call target and the name to be unquoted, and that carries most of the weight: SQL Server requires
/// at least a two-part name to invoke a user-defined scalar function, so an unqualified
/// <c>Len(x)</c> can only ever bind to the built-in. A user function really named <c>Len</c> is
/// reached as <c>dbo.Len(x)</c>, which has a call target and is left exactly as written.</para>
/// <para>Names with a ScriptDom node of their own — <c>CAST</c>, <c>COALESCE</c>, <c>NULLIF</c>,
/// <c>LEFT</c>, <c>RIGHT</c>, <c>IIF</c>, <c>IDENTITY</c> — never reach this lookup, because their
/// handlers already recase them from structural proof. They are omitted rather than listed, so that
/// membership here means "the printer has nothing but the spelling to go on".</para>
/// <para>Omitted too: anything not spelled as a bare <c>name(</c> call. <c>@@ROWCOUNT</c> and friends
/// are global variables, <c>CURRENT_TIMESTAMP</c> and <c>SESSION_USER</c> are reserved words that take
/// no parentheses, and <c>NEXT VALUE FOR</c> and <c>$PARTITION</c> have syntax of their own.</para>
/// </remarks>
public static class SqlBuiltInFunctions
{
    private static readonly FrozenSet<string> Names = new[]
    {
        // Aggregate
        "APPROX_COUNT_DISTINCT", "APPROX_PERCENTILE_CONT", "APPROX_PERCENTILE_DISC", "AVG",
        "CHECKSUM_AGG", "COUNT", "COUNT_BIG", "GROUPING", "GROUPING_ID", "MAX", "MIN", "STDEV",
        "STDEVP", "STRING_AGG", "SUM", "VAR", "VARP",

        // Ranking and analytic
        "CUME_DIST", "DENSE_RANK", "FIRST_VALUE", "LAG", "LAST_VALUE", "LEAD", "NTILE",
        "PERCENTILE_CONT", "PERCENTILE_DISC", "PERCENT_RANK", "RANK", "ROW_NUMBER",

        // String
        "ASCII", "CHAR", "CHARINDEX", "CONCAT", "CONCAT_WS", "DIFFERENCE", "FORMAT", "LEN",
        "LOWER", "LTRIM", "NCHAR", "PATINDEX", "QUOTENAME", "REPLACE", "REPLICATE", "REVERSE",
        "RTRIM", "SOUNDEX", "SPACE", "STR", "STRING_ESCAPE", "STUFF", "SUBSTRING", "TRANSLATE",
        "TRIM", "UNICODE", "UPPER",

        // Date and time
        "CURRENT_TIMEZONE", "CURRENT_TIMEZONE_ID", "DATEADD", "DATEDIFF", "DATEDIFF_BIG",
        "DATEFROMPARTS", "DATENAME", "DATEPART", "DATETIME2FROMPARTS", "DATETIMEFROMPARTS",
        "DATETIMEOFFSETFROMPARTS", "DATETRUNC", "DATE_BUCKET", "DAY", "EOMONTH", "GETDATE",
        "GETUTCDATE", "ISDATE", "MONTH", "SMALLDATETIMEFROMPARTS", "SWITCHOFFSET", "SYSDATETIME",
        "SYSDATETIMEOFFSET", "SYSUTCDATETIME", "TIMEFROMPARTS", "TODATETIMEOFFSET", "YEAR",

        // Mathematical
        "ABS", "ACOS", "ASIN", "ATAN", "ATN2", "CEILING", "COS", "COT", "DEGREES", "EXP", "FLOOR",
        "LOG", "LOG10", "PI", "POWER", "RADIANS", "RAND", "ROUND", "SIGN", "SIN", "SQRT", "SQUARE",
        "TAN",

        // Bit manipulation
        "BIT_COUNT", "GET_BIT", "LEFT_SHIFT", "RIGHT_SHIFT", "SET_BIT",

        // Logical and conversion
        "CHOOSE", "GREATEST", "LEAST", "PARSE", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE",

        // JSON
        "ISJSON", "JSON_ARRAY", "JSON_ARRAYAGG", "JSON_CONTAINS", "JSON_MODIFY", "JSON_OBJECT",
        "JSON_OBJECTAGG", "JSON_PATH_EXISTS", "JSON_QUERY", "JSON_VALUE",

        // System
        "BINARY_CHECKSUM", "CHECKSUM", "COMPRESS", "CONNECTIONPROPERTY", "CONTEXT_INFO",
        "CURRENT_REQUEST_ID", "CURRENT_TRANSACTION_ID", "DATALENGTH", "DECOMPRESS", "ERROR_LINE",
        "ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_PROCEDURE", "ERROR_SEVERITY", "ERROR_STATE",
        "FORMATMESSAGE", "GETANSINULL", "GET_FILESTREAM_TRANSACTION_CONTEXT", "HOST_ID",
        "HOST_NAME", "ISNULL", "ISNUMERIC", "MIN_ACTIVE_ROWVERSION", "NEWID", "NEWSEQUENTIALID",
        "ROWCOUNT_BIG", "SESSION_CONTEXT", "SESSION_ID", "SQL_VARIANT_PROPERTY", "XACT_STATE",

        // Metadata
        "APPLOCK_MODE", "APPLOCK_TEST", "APP_NAME", "ASSEMBLYPROPERTY", "COLUMNPROPERTY",
        "COL_LENGTH", "COL_NAME", "DATABASEPROPERTYEX", "DB_ID", "DB_NAME", "FILEGROUPPROPERTY",
        "FILEGROUP_ID", "FILEGROUP_NAME", "FILEPROPERTY", "FILEPROPERTYEX", "FILE_ID", "FILE_IDEX",
        "FILE_NAME", "FULLTEXTCATALOGPROPERTY", "FULLTEXTSERVICEPROPERTY", "IDENT_CURRENT",
        "IDENT_INCR", "IDENT_SEED", "INDEXKEY_PROPERTY", "INDEXPROPERTY", "INDEX_COL",
        "OBJECTPROPERTY", "OBJECTPROPERTYEX", "OBJECT_DEFINITION", "OBJECT_ID", "OBJECT_NAME",
        "OBJECT_SCHEMA_NAME", "ORIGINAL_DB_NAME", "PARSENAME", "SCHEMA_ID", "SCHEMA_NAME",
        "SCOPE_IDENTITY", "SERVERPROPERTY", "STATS_DATE", "TYPEPROPERTY", "TYPE_ID", "TYPE_NAME",

        // Security
        "CERTENCODED", "CERTPRIVATEKEY", "DATABASE_PRINCIPAL_ID", "HAS_DBACCESS",
        "HAS_PERMS_BY_NAME", "IS_MEMBER", "IS_ROLEMEMBER", "IS_SRVROLEMEMBER", "LOGINPROPERTY",
        "ORIGINAL_LOGIN", "PERMISSIONS", "PWDCOMPARE", "PWDENCRYPT", "SESSIONPROPERTY", "SUSER_ID",
        "SUSER_NAME", "SUSER_SID", "SUSER_SNAME", "USER_ID", "USER_NAME",

        // Cryptographic
        "ASYMKEYPROPERTY", "ASYMKEY_ID", "CERTPROPERTY", "CERT_ID", "CRYPT_GEN_RANDOM",
        "DECRYPTBYASYMKEY", "DECRYPTBYCERT", "DECRYPTBYKEY", "DECRYPTBYKEYAUTOASYMKEY",
        "DECRYPTBYKEYAUTOCERT", "DECRYPTBYPASSPHRASE", "ENCRYPTBYASYMKEY", "ENCRYPTBYCERT",
        "ENCRYPTBYKEY", "ENCRYPTBYPASSPHRASE", "HASHBYTES", "IS_OBJECTSIGNED", "KEY_GUID", "KEY_ID",
        "KEY_NAME", "SIGNBYASYMKEY", "SIGNBYCERT", "SYMKEYPROPERTY", "VERIFYSIGNEDBYASYMKEY",
        "VERIFYSIGNEDBYCERT",

        // Cursor, trigger, change tracking and text
        "CHANGE_TRACKING_CURRENT_VERSION", "CHANGE_TRACKING_IS_COLUMN_IN_MASK",
        "CHANGE_TRACKING_MIN_VALID_VERSION", "COLUMNS_UPDATED", "CURSOR_STATUS", "EVENTDATA",
        "TEXTPTR", "TEXTVALID", "TRIGGER_NESTLEVEL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is one of the built-in function names.</summary>
    public static bool Contains(string? name) => name is not null && Names.Contains(name);
}
