using System.Globalization;
using System.Text;

namespace Forge.Interchange.Contracts;

/// <summary>One X12 segment: its id (ISA, BEG, PO1…) and its elements, element 1 first.</summary>
public sealed record X12Segment(string Id, IReadOnlyList<string> Elements)
{
    /// <summary>Element n (1-based), or null when absent or empty.</summary>
    public string? this[int n] => n >= 1 && n <= Elements.Count && Elements[n - 1].Length > 0 ? Elements[n - 1] : null;

    public static X12Segment Of(string id, params string?[] elements) => new(id, elements.Select(e => e ?? string.Empty).ToList());
}

/// <summary>One transaction set (ST … SE), segments between ST and SE.</summary>
public sealed record X12Transaction(string Code, string ControlNumber, IReadOnlyList<X12Segment> Segments);

/// <summary>One functional group (GS … GE).</summary>
public sealed record X12Group(string FunctionalId, string SenderCode, string ReceiverCode, string ControlNumber, string Version, IReadOnlyList<X12Transaction> Transactions);

/// <summary>One interchange (ISA … IEA).</summary>
public sealed record X12Interchange(string SenderQualifier, string SenderId, string ReceiverQualifier, string ReceiverId, string ControlNumber, bool Test, IReadOnlyList<X12Group> Groups);

/// <summary>Why a file is not a readable X12 interchange.</summary>
public sealed class X12FormatException(string message) : FormatException(message);

/// <summary>
/// ANSI X12 reading and writing (owner decision 2026-11-01: X12 004010 for 850, 855, 856, 810 and 997). Separators are read from the
/// ISA segment of each file; files Forge writes use * between elements, &gt; between components and ~ after each segment.
/// </summary>
public static class X12
{
    public const string Version = "004010";
    public const string GroupVersion = "004010";

    /// <summary>The functional identifier (GS01) of a transaction set.</summary>
    public static string FunctionalId(string transactionSet) => transactionSet switch
    {
        "850" => "PO",
        "855" => "PR",
        "856" => "SH",
        "810" => "IN",
        "997" => "FA",
        _ => throw new ArgumentOutOfRangeException(nameof(transactionSet), transactionSet, "Forge reads and writes 850, 855, 856, 810 and 997."),
    };

    public static IReadOnlyList<X12Interchange> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var interchanges = new List<X12Interchange>();
        var rest = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (rest.Length == 0) throw new X12FormatException("The file is empty.");
        while (rest.Length > 0)
        {
            if (!rest.StartsWith("ISA", StringComparison.Ordinal) || rest.Length < 106) throw new X12FormatException("An X12 interchange starts with a 106-character ISA segment.");
            var element = rest[3];
            var terminator = rest[105];
            var segments = new List<X12Segment>();
            var end = -1;
            var position = 0;
            while (position < rest.Length)
            {
                var next = rest.IndexOf(terminator, position);
                if (next < 0) next = rest.Length;
                var raw = rest[position..next].Trim('\r', '\n', ' ', '\t');
                position = next + 1;
                if (raw.Length == 0) continue;
                var parts = raw.Split(element);
                var segment = new X12Segment(parts[0], parts.Skip(1).ToList());
                segments.Add(segment);
                if (segment.Id == "IEA") { end = position; break; }
            }

            if (end < 0) throw new X12FormatException("The interchange has no IEA segment.");
            interchanges.Add(Read(segments));
            rest = rest[end..].TrimStart(' ', '\r', '\n', '\t');
        }

        return interchanges;
    }

    private static X12Interchange Read(List<X12Segment> segments)
    {
        var isa = segments[0];
        if (isa.Elements.Count < 16) throw new X12FormatException("The ISA segment needs 16 elements.");
        var groups = new List<X12Group>();
        X12Segment? gs = null;
        var transactions = new List<X12Transaction>();
        X12Segment? st = null;
        var body = new List<X12Segment>();
        foreach (var s in segments.Skip(1))
        {
            switch (s.Id)
            {
                case "GS":
                    if (gs is not null) throw new X12FormatException("A GS segment appears before the previous group's GE.");
                    gs = s; transactions = [];
                    break;
                case "ST":
                    if (gs is null || st is not null) throw new X12FormatException("An ST segment must be inside a group and follow the previous SE.");
                    st = s; body = [];
                    break;
                case "SE":
                    if (st is null) throw new X12FormatException("An SE segment without its ST.");
                    if (int.TryParse(s[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count != body.Count + 2)
                        throw new X12FormatException($"Transaction {st[2]} says it has {count} segments but has {body.Count + 2}.");
                    if (s[2] != st[2]) throw new X12FormatException($"Transaction {st[2]} ends with control number {s[2]}.");
                    transactions.Add(new X12Transaction(st[1] ?? string.Empty, st[2] ?? string.Empty, body));
                    st = null;
                    break;
                case "GE":
                    if (gs is null || st is not null) throw new X12FormatException("A GE segment without its GS, or inside a transaction.");
                    groups.Add(new X12Group(gs[1] ?? string.Empty, gs[2] ?? string.Empty, gs[3] ?? string.Empty, gs[6] ?? string.Empty, gs[8] ?? string.Empty, transactions));
                    gs = null;
                    break;
                case "IEA":
                    break;
                default:
                    if (st is null) throw new X12FormatException($"Segment {s.Id} is outside a transaction.");
                    body.Add(s);
                    break;
            }
        }

        if (gs is not null || st is not null) throw new X12FormatException("A group or transaction is not closed.");
        return new X12Interchange(isa[5]?.Trim() ?? string.Empty, isa[6]?.Trim() ?? string.Empty, isa[7]?.Trim() ?? string.Empty, isa[8]?.Trim() ?? string.Empty,
            isa[13] ?? string.Empty, isa[15] == "T", groups);
    }

    /// <summary>Writes one interchange holding one group of one transaction set.</summary>
    public static string Write(string senderQualifier, string senderId, string receiverQualifier, string receiverId, long interchangeControl, long groupControl,
        string transactionSet, IReadOnlyList<X12Segment> body, DateTimeOffset at, bool test = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        var sb = new StringBuilder();
        void Segment(string id, params string[] elements)
        {
            sb.Append(id);
            foreach (var e in elements) sb.Append('*').Append(Clean(e));
            sb.Append("~\n");
        }

        var date6 = at.UtcDateTime.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var date8 = at.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var time = at.UtcDateTime.ToString("HHmm", CultureInfo.InvariantCulture);
        var isa = interchangeControl.ToString("D9", CultureInfo.InvariantCulture);
        // ISA is fixed width; Clean is not applied to the separators ISA16 defines.
        sb.Append(CultureInfo.InvariantCulture, $"ISA*00*{new string(' ', 10)}*00*{new string(' ', 10)}*{Pad(senderQualifier, 2)}*{Pad(senderId, 15)}*{Pad(receiverQualifier, 2)}*{Pad(receiverId, 15)}*{date6}*{time}*U*00401*{isa}*0*{(test ? 'T' : 'P')}*>~\n");
        var gs = groupControl.ToString(CultureInfo.InvariantCulture);
        Segment("GS", FunctionalId(transactionSet), senderId.Trim(), receiverId.Trim(), date8, time, gs, "X", GroupVersion);
        const string st = "0001";
        Segment("ST", transactionSet, st);
        foreach (var s in body) Segment(s.Id, [.. s.Elements]);
        Segment("SE", (body.Count + 2).ToString(CultureInfo.InvariantCulture), st);
        Segment("GE", "1", gs);
        Segment("IEA", "1", isa);
        return sb.ToString();
    }

    private static string Pad(string value, int width)
    {
        var v = Clean(value ?? string.Empty);
        return v.Length >= width ? v[..width] : v.PadRight(width);
    }

    /// <summary>Removes separator characters and line breaks so data never breaks the envelope.</summary>
    public static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string(value.Where(c => c is not ('*' or '~' or '>' or '\r' or '\n')).ToArray()).Trim();

    public static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? value) =>
        value is { Length: 8 } && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : value is { Length: 6 } && DateOnly.TryParseExact(value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : null;

    public static decimal? ParseNumber(string? value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;
}
