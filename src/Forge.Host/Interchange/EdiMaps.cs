using System.Globalization;
using Forge.Interchange.Contracts;
using Forge.Inventory.Contracts;
using Forge.Procurement.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Host.Interchange;

/// <summary>A customer's purchase order line as its 850 states it.</summary>
/// <param name="BuyerPart">The customer's own part number (BP or IN), mapped through the customer part numbers when no VP is sent.</param>
public sealed record EdiOrderLine(string LineId, string? PartNumber, decimal? Quantity, string? Unit, decimal? UnitPrice, DateOnly? DueDate, string? BuyerPart = null);

/// <summary>A customer's purchase order change (860) as read: what they want changed, for a person to apply.</summary>
public sealed record EdiOrderChange(string? CustomerPo, string Purpose, IReadOnlyList<string> Changes);

/// <summary>A customer's purchase order (850) as read, with what could not be read.</summary>
public sealed record EdiOrder(string? CustomerPo, string Purpose, DateOnly? OrderDate, string? ShipTo, IReadOnlyList<EdiOrderLine> Lines, IReadOnlyList<string> Problems);

/// <summary>
/// Maps between Forge's records and X12 004010 transaction sets (owner decisions 2026-11-01). Forge's own part numbers travel as the
/// vendor's part number (VP) to customers and as the buyer's part number (BP) to suppliers; a customer may send either, Forge's number first.
/// </summary>
public static class EdiMaps
{
    private static string D(DateOnly d) => X12.Date(d);

    private static string N(decimal v) => X12.Number(v);

    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a customer's 850. Nothing is guessed: a missing quantity, price, date or part is a problem.</summary>
    public static EdiOrder Read850(IReadOnlyList<X12Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var problems = new List<string>();
        var beg = segments.FirstOrDefault(s => s.Id == "BEG");
        if (beg is null) problems.Add("The 850 has no BEG segment.");
        var purpose = beg?[1] ?? "00";
        if (purpose != "00") problems.Add($"Purpose code {purpose}: only original orders (00) become sales orders; changes and cancellations are reviewed by a person.");
        DateOnly? headerDue = null;
        string? shipTo = null;
        var lines = new List<EdiOrderLine>();
        var inShipTo = false;
        var shipToParts = new List<string>();
        EdiOrderLine? current = null;
        foreach (var s in segments)
        {
            switch (s.Id)
            {
                case "DTM" when s[1] is "002" or "010" or "037":
                    var date = X12.ParseDate(s[2]);
                    if (current is not null) { lines[^1] = current = current with { DueDate = date ?? current.DueDate }; }
                    else headerDue = date ?? headerDue;
                    break;
                case "N1":
                    inShipTo = s[1] == "ST";
                    if (inShipTo && s[2] is { } name) shipToParts.Add(name);
                    break;
                case "N2" or "N3" when inShipTo:
                    shipToParts.AddRange(s.Elements.Where(e => e.Length > 0));
                    break;
                case "N4" when inShipTo:
                    shipToParts.Add(string.Join(" ", new[] { s[1], s[2], s[3], s[4] }.Where(e => e is not null)));
                    inShipTo = false;
                    break;
                case "PO1":
                    inShipTo = false;
                    var ids = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (var i = 6; i + 1 <= s.Elements.Count; i += 2)
                        if (s[i] is { } q && s[i + 1] is { } v) ids.TryAdd(q, v);
                    var vendor = ids.GetValueOrDefault("VP");
                    var buyer = ids.GetValueOrDefault("BP") ?? ids.GetValueOrDefault("IN");
                    current = new EdiOrderLine(s[1] ?? (lines.Count + 1).ToString(CultureInfo.InvariantCulture), vendor?.Trim().ToUpperInvariant(), X12.ParseNumber(s[2]), s[3], X12.ParseNumber(s[4]), null,
                        buyer?.Trim().ToUpperInvariant());
                    lines.Add(current);
                    break;
            }
        }

        if (shipToParts.Count > 0) shipTo = string.Join(", ", shipToParts);
        lines = lines.Select(l => l with { DueDate = l.DueDate ?? headerDue }).ToList();
        if (lines.Count == 0) problems.Add("The 850 has no PO1 lines.");
        foreach (var l in lines)
        {
            if (l.PartNumber is null && l.BuyerPart is null) problems.Add($"Line {l.LineId}: no part number (VP or BP).");
            if (l.Quantity is not > 0) problems.Add($"Line {l.LineId}: no quantity.");
            if (l.UnitPrice is null) problems.Add($"Line {l.LineId}: no unit price; Forge does not guess prices.");
            if (l.DueDate is null) problems.Add($"Line {l.LineId}: no requested delivery date (DTM 002).");
        }

        return new EdiOrder(beg?[3], purpose, X12.ParseDate(beg?[5]), shipTo, lines, problems);
    }

    /// <summary>855: the approved sales order, every line accepted as ordered (AC / IA).</summary>
    /// <summary>Our part as VP, and the customer's own number as BP when one is recorded.</summary>
    private static string?[] Parts(string part, IReadOnlyDictionary<string, string>? theirs) =>
        theirs?.TryGetValue(part, out var buyer) == true ? ["VP", part, "BP", buyer] : ["VP", part];

    /// <summary>Reads a customer's 860. Forge never applies a change by itself; this says what the customer asked for.</summary>
    public static EdiOrderChange Read860(IReadOnlyList<X12Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var bch = segments.FirstOrDefault(s => s.Id == "BCH");
        var purpose = bch?[1] switch { "01" => "Cancel the whole order", "04" => "Change", "05" => "Replace", null => "Change", var p => $"Purpose {p}" };
        var changes = new List<string>();
        foreach (var (s, i) in segments.Select((s, i) => (s, i)))
        {
            if (s.Id != "POC") continue;
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var k = 8; k + 1 <= s.Elements.Count; k += 2)
                if (s[k] is { } q && s[k + 1] is { } v) ids.TryAdd(q, v);
            var part = ids.GetValueOrDefault("VP") ?? ids.GetValueOrDefault("BP") ?? "?";
            var date = segments.Skip(i + 1).TakeWhile(x => x.Id != "POC").FirstOrDefault(x => x.Id == "DTM" && x[1] is "002" or "010")?[2];
            var what = s[2] switch
            {
                "AI" => "add",
                "DI" => "delete",
                "QI" => "increase quantity to",
                "QD" => "decrease quantity to",
                "PC" => "change price to",
                "CT" or "DC" => "change date",
                "CA" or "PQ" or "RZ" => "change to",
                var c => $"change ({c}) to",
            };
            var parts = new List<string>();
            if (s[3] is { } qty) parts.Add($"{qty} {s[5] ?? string.Empty}".Trim());
            if (s[6] is { } price) parts.Add($"at {price}");
            if (X12.ParseDate(date) is { } d) parts.Add($"due {d:yyyy-MM-dd}");
            changes.Add($"Line {s[1] ?? "?"} ({part}): {what} {string.Join(", ", parts)}".TrimEnd());
        }

        if (bch?[1] == "01") changes.Insert(0, "The customer cancels the whole order.");
        if (changes.Count == 0) changes.Add("No line changes were sent.");
        return new EdiOrderChange(bch?[3], purpose, changes);
    }

    public static IReadOnlyList<X12Segment> Build855(SalesOrderInfo order, DateOnly today, IReadOnlyDictionary<string, string>? customerParts = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var body = new List<X12Segment> { X12Segment.Of("BAK", "00", "AC", order.CustomerPo ?? I(order.OrderNumber), D(today), null, null, null, I(order.OrderNumber)) };
        foreach (var l in order.Lines.Where(l => l.Status == SalesLineStatus.Open))
        {
            body.Add(X12Segment.Of("PO1", [I(l.LineNumber), N(l.Quantity), l.UnitOfMeasure, N(l.UnitPrice), null, .. Parts(l.PartNumber, customerParts)]));
            body.Add(X12Segment.Of("ACK", "IA", N(l.Quantity), l.UnitOfMeasure, "068", D(l.DueDate)));
        }

        body.Add(X12Segment.Of("CTT", I(order.Lines.Count(l => l.Status == SalesLineStatus.Open))));
        return body;
    }

    /// <summary>856: one shipment against one sales order line, shipment / order / item levels, lots as REF LT.</summary>
    public static IReadOnlyList<X12Segment> Build856(ShipmentInfo shipment, SalesOrderInfo order, int orderLine, string unit, DateOnly shipped, IReadOnlyDictionary<string, string>? customerParts = null)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentNullException.ThrowIfNull(order);
        var at = shipment.ShippedAt ?? shipment.CreatedAt;
        var body = new List<X12Segment>
        {
            X12Segment.Of("BSN", "00", I(shipment.ShipmentId), D(shipped), at.UtcDateTime.ToString("HHmm", CultureInfo.InvariantCulture)),
            X12Segment.Of("HL", "1", null, "S"),
        };
        if (shipment.Carrier is { } carrier) body.Add(X12Segment.Of("TD5", null, null, null, null, carrier));
        if (shipment.Tracking is { } tracking) body.Add(X12Segment.Of("REF", "CN", tracking));
        body.Add(X12Segment.Of("DTM", "011", D(shipped)));
        if ((shipment.ShipTo ?? order.ShipTo) is { } shipTo) body.Add(X12Segment.Of("N1", "ST", shipTo.Length > 60 ? shipTo[..60] : shipTo));
        body.Add(X12Segment.Of("HL", "2", "1", "O"));
        body.Add(X12Segment.Of("PRF", order.CustomerPo ?? I(order.OrderNumber)));
        var hl = 2;
        foreach (var part in shipment.Lines.GroupBy(l => l.PartNumber, StringComparer.Ordinal))
        {
            hl++;
            body.Add(X12Segment.Of("HL", I(hl), "2", "I"));
            body.Add(X12Segment.Of("LIN", [I(orderLine), .. Parts(part.Key, customerParts)]));
            body.Add(X12Segment.Of("SN1", null, N(part.Sum(l => l.Quantity)), unit));
            foreach (var lot in part.Select(l => l.LotNumber).Where(l => l is not null).Distinct(StringComparer.Ordinal)) body.Add(X12Segment.Of("REF", "LT", lot));
        }

        body.Add(X12Segment.Of("CTT", I(hl)));
        return body;
    }

    /// <summary>810: an issued invoice, its lines, tax and terms. TDS is the total in cents.</summary>
    public static IReadOnlyList<X12Segment> Build810(InvoiceInfo invoice, IReadOnlyDictionary<int, string> units, int paymentTermsDays, IReadOnlyDictionary<string, string>? customerParts = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(units);
        var body = new List<X12Segment>
        {
            X12Segment.Of("BIG", D(invoice.InvoiceDate), I(invoice.InvoiceNumber), null, invoice.CustomerPo ?? I(invoice.OrderNumber)),
            X12Segment.Of("CUR", "SE", invoice.Currency),
            X12Segment.Of("N1", "BT", invoice.CustomerName),
            X12Segment.Of("ITD", "05", "3", null, null, null, D(invoice.DueDate), I(paymentTermsDays)),
        };
        foreach (var l in invoice.Lines)
            body.Add(X12Segment.Of("IT1", [I(l.LineNumber), N(l.Quantity), units.GetValueOrDefault(l.OrderLine, "EA"), N(l.UnitPrice), null, .. Parts(l.PartNumber, customerParts)]));
        body.Add(X12Segment.Of("TDS", I((long)Math.Round(invoice.Total * 100m, MidpointRounding.AwayFromZero))));
        body.Add(invoice.TaxExemptCertificate is { } certificate
            ? X12Segment.Of("TXI", "TX", "0", null, null, null, null, "2", certificate)
            : X12Segment.Of("TXI", "TX", N(invoice.Tax), N(invoice.TaxRatePercent)));
        body.Add(X12Segment.Of("CTT", I(invoice.Lines.Count)));
        return body;
    }

    /// <summary>850 to a supplier: an approved purchase order with its open lines and promised dates.</summary>
    public static IReadOnlyList<X12Segment> Build850(PurchaseOrderInfo po, string supplierName, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(po);
        var body = new List<X12Segment> { X12Segment.Of("BEG", "00", "SA", I(po.PoNumber), null, D(date)), X12Segment.Of("N1", "VN", supplierName) };
        var open = po.Lines.Where(l => l.Status == PurchaseLineStatus.Open).ToList();
        foreach (var l in open)
        {
            body.Add(X12Segment.Of("PO1", I(l.LineNumber), N(l.Quantity), l.UnitOfMeasure, l.UnitPrice is { } p ? N(p) : null, null, "BP", l.PartNumber));
            body.Add(X12Segment.Of("DTM", "002", D(l.PromisedDate)));
        }

        body.Add(X12Segment.Of("CTT", I(open.Count)));
        return body;
    }

    /// <summary>997 for one received transaction: accepted, or rejected with the reason in a note segment.</summary>
    public static IReadOnlyList<X12Segment> Build997(InterchangeDocument received, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(received);
        var code = accepted ? "A" : "R";
        return
        [
            X12Segment.Of("AK1", X12.FunctionalId(received.TransactionSet), received.GroupControl),
            X12Segment.Of("AK2", received.TransactionSet, received.TransactionControl),
            X12Segment.Of("AK5", code),
            X12Segment.Of("AK9", code, "1", "1", accepted ? "1" : "0"),
        ];
    }

    /// <summary>Reads a partner's 997: the group it answers and whether it was accepted (A or E) or rejected.</summary>
    public static (string? GroupControl, bool Accepted, string Note) Read997(IReadOnlyList<X12Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ak1 = segments.FirstOrDefault(s => s.Id == "AK1");
        var ak9 = segments.FirstOrDefault(s => s.Id == "AK9");
        var code = ak9?[1] ?? segments.FirstOrDefault(s => s.Id == "AK5")?[1];
        return (ak1?[2], code is "A" or "E", code is null ? "No AK9 or AK5 status." : $"Status {code}.");
    }

    /// <summary>A supplier's 855, 856 or 810, summarised for people; nothing in Forge changes because of it.</summary>
    public static (string? PoNumber, string Summary) ReadSupplierDocument(string transactionSet, IReadOnlyList<X12Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        string? Find(string id, int element) => segments.FirstOrDefault(s => s.Id == id)?[element];
        switch (transactionSet)
        {
            case "855":
            {
                var po = Find("BAK", 3);
                var acks = segments.Where(s => s.Id == "ACK").ToList();
                var changed = acks.Count(a => a[1] is not ("IA" or "AC"));
                var dates = acks.Select(a => X12.ParseDate(a[5])).Where(d => d is not null).Select(d => d!.Value).ToList();
                return (po, $"Supplier acknowledged PO {po}: {Find("BAK", 2) switch { "AC" => "accepted", "AD" => "accepted with details", "AT" => "accepted", "RD" => "rejected with details", "RJ" => "rejected", var c => c ?? "?" }}" +
                    $"{(changed > 0 ? $", {changed} line(s) changed" : string.Empty)}{(dates.Count > 0 ? $", latest date {dates.Max():yyyy-MM-dd}" : string.Empty)}. Check the PO's promised dates.");
            }
            case "856":
            {
                var po = Find("PRF", 1);
                var qty = segments.Where(s => s.Id == "SN1").Sum(s => X12.ParseNumber(s[2]) ?? 0m);
                var shipped = X12.ParseDate(segments.FirstOrDefault(s => s.Id == "DTM" && s[1] == "011")?[2]);
                return (po, $"Supplier ship notice {Find("BSN", 2)} for PO {po}: {N(qty)} shipped{(shipped is { } d ? $" {d:yyyy-MM-dd}" : string.Empty)}{(Find("REF", 2) is { } r ? $", reference {r}" : string.Empty)}.");
            }
            case "810":
            {
                var po = Find("BIG", 4);
                var total = X12.ParseNumber(Find("TDS", 1));
                return (po, $"Supplier invoice {Find("BIG", 2)} for PO {po}{(total is { } t ? $": {N(t / 100m)}" : string.Empty)}. Forge keeps no payables; pass it to accounting.");
            }
            default:
                return (null, $"Transaction set {transactionSet}.");
        }
    }
}
