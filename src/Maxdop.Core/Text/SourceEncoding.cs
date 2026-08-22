using System.Text;

namespace Maxdop.Core.Text;

/// <summary>
/// The byte-level encoding of a source file, and everything needed to write it back the way it
/// arrived.
/// </summary>
/// <remarks>
/// <para>This is the layer that can destroy a file, and the only one — the formatter proper is
/// <c>string</c> to <c>string</c> and physically cannot touch a byte. SSMS writes
/// <b>UTF-16 LE with BOM</b> by default, so a formatter that assumes UTF-8 rewrites every byte of
/// every file it touches, and one that drops a BOM changes how SSMS then reads the file. Both are
/// silent.</para>
/// <para><b>The design rule: never enumerate what could go wrong, prove the round trip.</b>
/// <see cref="CanRoundTrip"/> checks that re-encoding the decoded text reproduces the original bytes
/// exactly. That single check subsumes every case a list would try to cover — legacy code pages
/// mis-read as UTF-8, lone surrogates, redundant BOMs, unusual normalisation — and it will keep
/// covering cases nobody has thought of. It is the same argument as the round-trip token verifier,
/// one level down.</para>
/// <para>Decoders are constructed with <c>throwOnInvalidBytes: true</c> on purpose. The default
/// silently substitutes U+FFFD for anything undecodable, which turns a Windows-1252 <c>é</c> into a
/// replacement character and then writes it back — corrupting the file while reporting success.</para>
/// </remarks>
public sealed class SourceEncoding
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] Utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BeBom = [0x00, 0x00, 0xFE, 0xFF];

    private SourceEncoding(string name, Encoding encoding, byte[] bom)
    {
        Name = name;
        Encoding = encoding;
        Bom = bom;
    }

    /// <summary>Human-readable name, for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Whether the file began with a byte-order mark.</summary>
    public bool HasBom => Bom.Length > 0;

    private Encoding Encoding { get; }

    private byte[] Bom { get; }

    /// <summary>
    /// UTF-8 with no BOM — the assumption for any file without one, and what stdin always is.
    /// </summary>
    /// <remarks>
    /// A BOM-less UTF-16 file decodes to text full of NUL characters, which fails to parse and so
    /// comes back untouched through the normal passthrough path. Guessing at BOM-less UTF-16 would
    /// risk mis-detecting a UTF-8 file, which is the more common and more damaging error.
    /// </remarks>
    public static SourceEncoding Utf8NoBom { get; } =
        new("UTF-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), []);

    /// <summary>
    /// Identifies a file's encoding from its byte-order mark, defaulting to UTF-8 without one.
    /// </summary>
    public static SourceEncoding Detect(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE before UTF-16 LE: they share their first two bytes, so checking the shorter mark
        // first would classify every UTF-32 LE file as UTF-16 LE and mangle it.
        if (bytes.StartsWith(Utf32LeBom))
        {
            return new SourceEncoding("UTF-32 LE with BOM", new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true), Utf32LeBom);
        }

        if (bytes.StartsWith(Utf32BeBom))
        {
            return new SourceEncoding("UTF-32 BE with BOM", new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true), Utf32BeBom);
        }

        if (bytes.StartsWith(Utf8Bom))
        {
            return new SourceEncoding("UTF-8 with BOM", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), Utf8Bom);
        }

        if (bytes.StartsWith(Utf16LeBom))
        {
            return new SourceEncoding("UTF-16 LE with BOM", new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), Utf16LeBom);
        }

        if (bytes.StartsWith(Utf16BeBom))
        {
            return new SourceEncoding("UTF-16 BE with BOM", new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), Utf16BeBom);
        }

        return Utf8NoBom;
    }

    /// <summary>
    /// Decodes a file's bytes to text, with the byte-order mark removed.
    /// </summary>
    /// <exception cref="DecoderFallbackException">
    /// The bytes are not valid in this encoding. Callers must treat that as "leave the file alone"
    /// rather than substituting replacement characters.
    /// </exception>
    public string Decode(ReadOnlySpan<byte> bytes) => Encoding.GetString(bytes[Bom.Length..]);

    /// <summary>
    /// Encodes text back to file bytes, re-emitting the byte-order mark if the original had one.
    /// </summary>
    public byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var body = Encoding.GetBytes(text);
        if (Bom.Length == 0)
        {
            return body;
        }

        var result = new byte[Bom.Length + body.Length];
        Bom.CopyTo(result, 0);
        body.CopyTo(result, Bom.Length);
        return result;
    }

    /// <summary>
    /// Whether decoding and re-encoding these bytes reproduces them exactly.
    /// </summary>
    /// <remarks>
    /// The safety gate for <c>--write</c>. If this is false the file cannot be rewritten without
    /// changing bytes the formatter was never asked to change, so the only correct action is to leave
    /// it alone and say why. Cheap — one decode and one encode of a file already being read.
    /// </remarks>
    public bool CanRoundTrip(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Encode(Decode(bytes)).AsSpan().SequenceEqual(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
