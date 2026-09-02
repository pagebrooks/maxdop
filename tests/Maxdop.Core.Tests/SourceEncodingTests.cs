using System.Text;
using Maxdop.Core.Formatting;
using Maxdop.Core.Text;

namespace Maxdop.Core.Tests;

/// <summary>
/// The encoding invariants — the landmine deferred out of the spike, whose
/// fixture was UTF-8 only.
/// </summary>
/// <remarks>
/// These matter more than their size suggests. Everything else in this repo is <c>string</c> to
/// <c>string</c> and cannot touch a byte; this is the one layer that can, and SSMS's default is
/// UTF-16 LE with BOM, so a wrong assumption here rewrites every byte of every file the formatter
/// is pointed at.
/// </remarks>
public class SourceEncodingTests
{
    private static byte[] Bytes(Encoding encoding, string text) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    // --- detection --------------------------------------------------------------------

    [Fact]
    public void Utf16LeWithBomIsDetected()
    {
        // SSMS's default. The single most important case in this file.
        var bytes = Bytes(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), "SELECT 1;");

        var encoding = SourceEncoding.Detect(bytes);

        Assert.Equal("UTF-16 LE with BOM", encoding.Name);
        Assert.True(encoding.HasBom);
        Assert.Equal("SELECT 1;", encoding.Decode(bytes));
    }

    [Theory]
    [InlineData("UTF-8", false)]
    [InlineData("UTF-8 with BOM", true)]
    public void Utf8IsDetectedWithAndWithoutABom(string expectedName, bool withBom)
    {
        var bytes = Bytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom), "SELECT 1;");

        var encoding = SourceEncoding.Detect(bytes);

        Assert.Equal(expectedName, encoding.Name);
        Assert.Equal(withBom, encoding.HasBom);
        Assert.Equal("SELECT 1;", encoding.Decode(bytes));
    }

    [Fact]
    public void Utf32LeIsNotMistakenForUtf16Le()
    {
        // The two share their first two bytes (FF FE), so checking the shorter mark first would
        // classify every UTF-32 LE file as UTF-16 LE and mangle it.
        var bytes = Bytes(new UTF32Encoding(bigEndian: false, byteOrderMark: true), "SELECT 1;");

        var encoding = SourceEncoding.Detect(bytes);

        Assert.Equal("UTF-32 LE with BOM", encoding.Name);
        Assert.Equal("SELECT 1;", encoding.Decode(bytes));
    }

    [Theory]
    [InlineData("UTF-16 BE with BOM")]
    public void Utf16BigEndianIsDetected(string expectedName)
    {
        var bytes = Bytes(new UnicodeEncoding(bigEndian: true, byteOrderMark: true), "SELECT 1;");

        Assert.Equal(expectedName, SourceEncoding.Detect(bytes).Name);
    }

    [Fact]
    public void NoBomMeansUtf8()
    {
        Assert.Equal("UTF-8", SourceEncoding.Detect("SELECT 1;"u8).Name);
        Assert.False(SourceEncoding.Detect("SELECT 1;"u8).HasBom);
    }

    [Fact]
    public void EmptyInputDoesNotThrow()
    {
        Assert.Equal("UTF-8", SourceEncoding.Detect([]).Name);
        Assert.Equal(string.Empty, SourceEncoding.Utf8NoBom.Decode([]));
    }

    // --- byte-identical round trip ----------------------------------------------------

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT N'héllo wörld';")]                     // non-ASCII in a literal
    [InlineData("SELECT N'\U0001F600';")]                      // outside the BMP: a surrogate pair
    [InlineData("-- comment\r\nSELECT 1;\r\n")]                // CRLF
    [InlineData("SELECT [Column With Space] FROM [t];")]
    public void EveryEncodingRoundTripsByteIdentically(string text)
    {
        // The invariant §3 actually asks for. Note UTF-16 LE especially: if it did not hold, every
        // SSMS-authored file would have every byte rewritten.
        Encoding[] encodings =
        [
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        ];

        foreach (var encoding in encodings)
        {
            var original = Bytes(encoding, text);
            var detected = SourceEncoding.Detect(original);

            Assert.True(detected.CanRoundTrip(original), $"{detected.Name} could not round-trip {encoding.WebName}");
            Assert.Equal(original, detected.Encode(detected.Decode(original)));
        }
    }

    [Fact]
    public void BomPresenceIsPreservedInBothDirections()
    {
        // Adding a BOM changes how SSMS reads the file; removing one changes how everything else
        // does. Neither is a change the formatter was asked to make.
        var withBom = Bytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), "SELECT 1;");
        var withoutBom = Bytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "SELECT 1;");

        Assert.Equal(withBom, SourceEncoding.Detect(withBom).Encode("SELECT 1;"));
        Assert.Equal(withoutBom, SourceEncoding.Detect(withoutBom).Encode("SELECT 1;"));
        Assert.NotEqual(withBom, withoutBom);
    }

    // --- bytes that cannot be handled safely ------------------------------------------

    [Fact]
    public void UndecodableBytesAreReportedRatherThanSubstituted()
    {
        // A Windows-1252 `é` (0xE9) is not valid UTF-8. The default decoder would silently swap in
        // U+FFFD and the file would then be written back corrupted, reporting success. Throwing on
        // invalid bytes turns that into a detectable refusal.
        byte[] latin1 = [.. "-- café"u8[..6], 0xE9];

        Assert.False(SourceEncoding.Utf8NoBom.CanRoundTrip(latin1));
        Assert.Throws<DecoderFallbackException>(() => SourceEncoding.Utf8NoBom.Decode(latin1));
    }

    [Fact]
    public void CanRoundTripIsFalseWhenBytesAreNotCanonical()
    {
        // A UTF-8 BOM in the middle of a UTF-16 LE file, and similar oddities, decode without error
        // but do not re-encode to the same bytes. CanRoundTrip is what catches the general case
        // without anyone having to enumerate it.
        byte[] overlong = [0xC0, 0x80];   // overlong encoding of NUL — invalid UTF-8

        Assert.False(SourceEncoding.Utf8NoBom.CanRoundTrip(overlong));
    }

    [Theory]
    [InlineData("UTF-8 with BOM", new byte[] { 0xEF, 0xBB, 0xBF, 0x80 })]
    [InlineData("UTF-8 with BOM", new byte[] { 0xEF, 0xBB, 0xBF, 0xE2, 0x82 })]
    [InlineData("UTF-16 LE with BOM", new byte[] { 0xFF, 0xFE, 0x00, 0xD8 })]
    [InlineData("UTF-16 LE with BOM", new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x41 })]
    [InlineData("UTF-16 BE with BOM", new byte[] { 0xFE, 0xFF, 0xD8, 0x00 })]
    [InlineData("UTF-32 LE with BOM", new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00 })]
    [InlineData("UTF-32 LE with BOM", new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x00, 0xD8, 0x00, 0x00 })]
    [InlineData("UTF-32 BE with BOM", new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00 })]
    public void EveryEncodingRejectsUndecodableBytesRatherThanSubstituting(string expectedName, byte[] bytes)
    {
        // The guarantee this class exists for was only ever proven for UTF-8 without a BOM. Every
        // decoder is constructed with throwOnInvalidBytes on purpose, and until now nothing held four
        // of the five to it: flipping any one of those flags to false left the whole suite green
        // while that encoding silently swapped in U+FFFD and wrote the corruption back.
        //
        // The cases are the ways each encoding can be malformed rather than a uniform sample: an
        // invalid UTF-8 start byte and a truncated sequence; an unpaired surrogate and an odd byte
        // count in UTF-16; a scalar past U+10FFFF and a surrogate value in UTF-32.
        var encoding = SourceEncoding.Detect(bytes);

        Assert.Equal(expectedName, encoding.Name);
        Assert.Throws<DecoderFallbackException>(() => encoding.Decode(bytes));

        // And the --write gate agrees, which is the half that actually protects a file.
        Assert.False(encoding.CanRoundTrip(bytes));
    }

    [Fact]
    public void EncodingNullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SourceEncoding.Utf8NoBom.Encode(null!));
    }

    // --- end to end through the formatter ---------------------------------------------

    [Fact]
    public void Utf16FileFormatsAndReEncodesWithoutChangingItsEncoding()
    {
        // The whole point, exercised as a user would hit it: an SSMS-authored file gets formatted and
        // written back still UTF-16 LE with a BOM, with only the SQL changed.
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var original = Bytes(utf16, "select a,b from dbo.t;\r\n");

        var encoding = SourceEncoding.Detect(original);
        var result = SqlFormatter.Format(encoding.Decode(original));
        var written = encoding.Encode(result.Output);

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Equal("UTF-16 LE with BOM", encoding.Name);
        Assert.Equal<byte[]>([0xFF, 0xFE], written[..2]);
        Assert.Equal("SELECT a, b FROM dbo.t;\r\n", utf16.GetString(written[2..]));
    }

    [Theory]
    [InlineData("SELECT 1;")]        // no trailing newline
    [InlineData("SELECT 1;\n")]      // trailing LF
    [InlineData("SELECT 1;\r\n")]    // trailing CRLF
    public void TrailingNewlinePresenceSurvivesTheByteLayer(string text)
    {
        // Adding or removing a final newline is a one-line diff on every file in a repo. The
        // formatter already preserves it; this confirms encoding does not reintroduce the problem.
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var original = Bytes(utf16, text);
        var encoding = SourceEncoding.Detect(original);

        var written = encoding.Encode(SqlFormatter.Format(encoding.Decode(original)).Output);

        Assert.Equal(text.EndsWith('\n'), utf16.GetString(written[2..]).EndsWith('\n'));
        Assert.Equal(text.EndsWith("\r\n", StringComparison.Ordinal), utf16.GetString(written[2..]).EndsWith("\r\n", StringComparison.Ordinal));
    }
}
