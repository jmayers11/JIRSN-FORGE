using Forge.Interchange;
using Forge.Interchange.Contracts;

namespace Forge.Planning.Tests;

/// <summary>Owner decision 2026-11-01: ANSI X12 004010 envelopes are read with the separators the file declares and written exactly.</summary>
public class X12Tests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 14, 5, 0, TimeSpan.Zero);

    [Fact]
    public void A_written_interchange_reads_back_with_its_envelope_and_counts()
    {
        var text = X12.Write("ZZ", "FORGE", "01", "123456789", 42, 42, "855",
            [X12Segment.Of("BAK", "00", "AC", "PO-1*bad~chars", "20261101"), X12Segment.Of("CTT", "0")], At, test: true);
        Assert.StartsWith("ISA*00*          *00*          *ZZ*FORGE          *01*123456789      *261101*1405*U*00401*000000042*0*T*>~", text, StringComparison.Ordinal);
        Assert.Equal(106, text.IndexOf('~', StringComparison.Ordinal) + 1);
        Assert.Contains("GS*PR*FORGE*123456789*20261101*1405*42*X*004010~", text, StringComparison.Ordinal);
        Assert.Contains("SE*4*0001~", text, StringComparison.Ordinal);
        var isa = Assert.Single(X12.Parse(text));
        Assert.Equal(("ZZ", "FORGE", "01", "123456789", "000000042", true), (isa.SenderQualifier, isa.SenderId, isa.ReceiverQualifier, isa.ReceiverId, isa.ControlNumber, isa.Test));
        var t = Assert.Single(Assert.Single(isa.Groups).Transactions);
        Assert.Equal(("855", "PO-1badchars"), (t.Code, t.Segments[0][3]));
        static IEnumerable<string> Flat(IEnumerable<X12Segment> segments) => segments.Select(s => s.Id + "|" + string.Join("|", s.Elements));
        Assert.Equal(Flat(t.Segments), Flat(InterchangeService.Segments(InterchangeService.Render(t))));
    }

    [Fact]
    public void Separators_come_from_the_isa_and_several_interchanges_in_one_file_are_read()
    {
        var one = "ISA|00|          |00|          |ZZ|ACME           |ZZ|FORGE          |261101|1200|U|00401|000000001|0|P|^\r\nGS|PO|ACME|FORGE|20261101|1200|1|X|004010\r\nST|850|0001\r\nBEG|00|SA|PO-9||20261101\r\nSE|3|0001\r\nGE|1|1\r\nIEA|1|000000001\r\n";
        Assert.Equal('\r', one[105]);
        var both = X12.Parse(one + one.Replace("000000001", "000000002", StringComparison.Ordinal));
        Assert.Equal(2, both.Count);
        Assert.Equal("PO-9", both[1].Groups[0].Transactions[0].Segments[0][3]);
    }

    [Fact]
    public void Broken_files_are_refused_with_the_reason()
    {
        Assert.Throws<X12FormatException>(() => X12.Parse(""));
        Assert.Throws<X12FormatException>(() => X12.Parse("hello"));
        var text = X12.Write("ZZ", "A", "ZZ", "B", 1, 1, "997", [X12Segment.Of("AK1", "PO", "1")], At);
        Assert.Contains("says it has 9", Assert.Throws<X12FormatException>(() => X12.Parse(text.Replace("SE*3*", "SE*9*", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        Assert.Contains("no IEA", Assert.Throws<X12FormatException>(() => X12.Parse(text[..text.IndexOf("IEA", StringComparison.Ordinal)])).Message, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 11, 1), X12.ParseDate("261101"));
        Assert.Null(X12.ParseDate("2026-11-01"));
    }
}
