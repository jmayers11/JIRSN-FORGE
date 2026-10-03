using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Planning.Reporting;

namespace Forge.Planning.Analyst;

public sealed record VocabularyMachine(string Code, string Name, string WorkCenterCode);

public sealed record VocabularyWorkCenter(string Code, string Name);

public sealed record VocabularyShift(string Code, string Name, TimeOnly Start, int DurationMinutes);

public sealed record VocabularyProgram(Guid ProgramId, string Code, string Name);

public sealed record VocabularyPerson(Guid PersonId, string Name);

/// <summary>
/// The names Forge knows, already limited to what the reader may see: no program they cannot access,
/// and no people unless they may read the workforce. The parser can only recognize what is listed here.
/// </summary>
public sealed record AnalystVocabulary(
    IReadOnlyList<VocabularyMachine> Machines,
    IReadOnlyList<VocabularyWorkCenter> WorkCenters,
    IReadOnlyList<VocabularyShift> Shifts,
    IReadOnlyList<string> Parts,
    IReadOnlyList<VocabularyProgram> Programs,
    IReadOnlyList<VocabularyPerson> People,
    IReadOnlySet<long> Orders);

/// <summary>"Now" at the site: the local date and time, and how to turn a local date and time into an instant.</summary>
public sealed record AnalystClock(DateTimeOffset Now, DateOnly Today, TimeOnly TimeOfDay, Func<DateOnly, TimeOnly, DateTimeOffset> ToInstant);

/// <summary>
/// Forge's native language parser (spec §59). Deterministic and rule-based: it recognizes manufacturing
/// intents, Forge's own entity codes and names, relative periods and shifts, and says what it understood.
/// It never guesses silently: unresolved or ambiguous parts are listed for the person to confirm.
/// </summary>
public static partial class QueryParser
{
    private static readonly string[] Ordinals = ["first", "second", "third", "fourth"];

    public static Interpretation Parse(string question, AnalystVocabulary vocabulary, AnalystClock clock)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(clock);
        var original = (question ?? string.Empty).Trim();
        var text = " " + Normalize(original) + " ";
        var ambiguities = new List<string>();

        // ---- entities
        long? order = null;
        if (OrderRegex().Match(text) is { Success: true } om)
        {
            order = long.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            foreach (Match m in NumberRegex().Matches(text))
            {
                var n = long.Parse(m.Value, CultureInfo.InvariantCulture);
                if (vocabulary.Orders.Contains(n))
                {
                    order = n;
                    break;
                }
            }
        }

        var masked = text;
        string? machine = null;
        var machineHits = vocabulary.Machines
            .Where(m => Contains(masked, m.Code) || Contains(masked, m.Name))
            .Select(m => m.Code).Distinct(StringComparer.Ordinal).ToList();
        if (machineHits.Count == 0 && MachineLetterRegex().Match(text) is { Success: true } ml)
        {
            // "machine a": a machine whose code ends in that letter or number.
            var suffix = ml.Groups[1].Value;
            machineHits = vocabulary.Machines.Where(m => Normalize(m.Code).EndsWith("-" + suffix, StringComparison.Ordinal) || Normalize(m.Code).EndsWith(" " + suffix, StringComparison.Ordinal)).Select(m => m.Code).ToList();
            if (machineHits.Count > 1)
            {
                ambiguities.Add($"“machine {suffix.ToUpperInvariant()}” could be {string.Join(" or ", machineHits)}.");
                machineHits = [];
            }
        }

        if (machineHits.Count >= 1)
        {
            machine = machineHits[0];
            if (machineHits.Count > 1)
            {
                ambiguities.Add($"Several machines are named: {string.Join(", ", machineHits)}; using {machine}.");
            }

            foreach (var m in vocabulary.Machines.Where(m => machineHits.Contains(m.Code, StringComparer.Ordinal)))
            {
                masked = Mask(Mask(masked, m.Code), m.Name);
            }
        }

        var workCenterHits = vocabulary.WorkCenters
            .Where(w => Contains(masked, w.Code) || Contains(masked, w.Name) || Stems(w.Name).Any(s => WordStartsWith(masked, s)))
            .Select(w => w.Code).ToList();
        string? workCenter = null;
        if (workCenterHits.Count == 1)
        {
            workCenter = workCenterHits[0];
        }
        else if (workCenterHits.Count > 1)
        {
            // Prefer an exact code or full-name match over a word stem.
            var exact = vocabulary.WorkCenters.Where(w => workCenterHits.Contains(w.Code, StringComparer.Ordinal) && (Contains(masked, w.Code) || Contains(masked, w.Name))).ToList();
            workCenter = exact.Count == 1 ? exact[0].Code : null;
            if (workCenter is null)
            {
                ambiguities.Add($"Several work centers match: {string.Join(", ", workCenterHits)}.");
            }
        }

        var part = vocabulary.Parts.FirstOrDefault(p => Contains(masked, p));
        var program = vocabulary.Programs.FirstOrDefault(p => Contains(masked, p.Code) || Contains(masked, p.Name));
        var person = vocabulary.People.FirstOrDefault(p => Contains(masked, p.Name))
            ?? FirstNameMatch(vocabulary.People, masked, ambiguities);

        var shift = ParseShift(text, vocabulary.Shifts, ambiguities);
        var category = CategoryOf(text);
        var hours = HoursRegex().Match(text) is { Success: true } hm
            ? decimal.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture) * (hm.Groups[2].Value.StartsWith('d') ? 24 : 1)
            : (decimal?)null;
        var (period, at) = ParsePeriod(text, clock);

        // ---- intent
        var intent = IntentOf(text, order, machine, workCenter, person, category);
        AnalystReportRequest? report = null;
        if (intent == AnalystIntent.Report || (intent == AnalystIntent.Unsupported && ReportOf(text) is not null))
        {
            report = BuildReport(text, clock, period, machine, workCenter, part, program, shift, ambiguities);
            intent = report is null ? AnalystIntent.Unsupported : AnalystIntent.Report;
        }

        var confidence = intent switch
        {
            AnalystIntent.Unsupported => AnalystConfidence.Low,
            AnalystIntent.OrderStatus when order is null => AnalystConfidence.Low,
            AnalystIntent.MachineBlockers or AnalystIntent.Simulate when machine is null && person is null => AnalystConfidence.Low,
            AnalystIntent.QualifiedPeople when machine is null && workCenter is null => AnalystConfidence.Low,
            _ when ambiguities.Count > 0 => AnalystConfidence.Medium,
            _ => AnalystConfidence.High,
        };

        if (intent == AnalystIntent.OrderStatus && order is null)
        {
            ambiguities.Add("Which order? Give its number, for example “order 10001”.");
        }

        if (intent is AnalystIntent.MachineBlockers && machine is null)
        {
            ambiguities.Add("Which machine? Use its code, for example “SLITTER-A”.");
        }

        if (intent is AnalystIntent.Simulate && machine is null && person is null)
        {
            ambiguities.Add("What should go down? Name a machine (for example “SLITTER-A”) or a person.");
        }

        if (intent is AnalystIntent.QualifiedPeople && machine is null && workCenter is null)
        {
            ambiguities.Add("For which work center or machine?");
        }

        if (intent is AnalystIntent.AffectedOrders && machine is null && workCenter is null && category is null && person is null)
        {
            ambiguities.Add("Affected by what? Name a machine, a work center or a kind of constraint (capacity, labor, material, maintenance).");
        }

        var interpretation = new Interpretation(
            original, intent, string.Empty, confidence, order, machine, workCenter, part, program?.ProgramId, person?.PersonId,
            category, shift?.Code, period, at, hours, report, ambiguities);
        return interpretation with { Understood = Describe(interpretation, vocabulary) };
    }

    // ------------------------------------------------------------------ intent

    private static AnalystIntent IntentOf(string t, long? order, string? machine, string? workCenter, VocabularyPerson? person, string? category)
    {
        bool Has(params string[] words) => words.Any(w => t.Contains(" " + w, StringComparison.Ordinal));

        if (Has("brief", "briefing"))
        {
            return AnalystIntent.Brief;
        }

        if (Has("what changed", "what has changed", "whats changed", "changes since", "changed since"))
        {
            return AnalystIntent.WhatChanged;
        }

        if (Has("what happens if", "what if", "simulate", "suppose", "what would happen"))
        {
            return AnalystIntent.Simulate;
        }

        if (person is not null && Has("pto", "absence", "absent", "vacation", "leave", "out ", "off ", "unavailab", "affected", "impact"))
        {
            return AnalystIntent.PersonImpact;
        }

        if (Has("expir") && Has("cert", "qualif", "training"))
        {
            return AnalystIntent.Report;
        }

        if (Has("who is qualified", "who qualified", "whos qualified", "who can run", "who can work", "who can operate", "qualified to", "qualified people", "qualified operators", "who is certified"))
        {
            return AnalystIntent.QualifiedPeople;
        }

        if (Has("generate", "create", "export", "excel", "xlsx", "csv", "pdf", "spreadsheet", "report", "compare", "graph", "chart", "grouped by", "group by"))
        {
            return AnalystIntent.Report;
        }

        if (order is not null)
        {
            return AnalystIntent.OrderStatus;
        }

        if (machine is not null && Has("prevent", "blocking", "block", "stopping", "cant run", "cannot run", "can not run", "why can", "can it run", "able to run", "not running", "keeping"))
        {
            return AnalystIntent.MachineBlockers;
        }

        if (Has("affected", "impacted", "held up", "blocked by", "delayed by", "hit by"))
        {
            return AnalystIntent.AffectedOrders;
        }

        if (Has("late", "at risk", "behind", "overdue", "slipping", "wont make", "miss due"))
        {
            return AnalystIntent.LateOrders;
        }

        if (Has("unavailable", "not available", "down", "offline", "out of service", "locked out", "broken") && (machine is null || Has("machines", "equipment")))
        {
            return AnalystIntent.UnavailableMachines;
        }

        if (machine is not null && Has("run", "available", "running", "ready"))
        {
            return AnalystIntent.MachineBlockers;
        }

        if ((workCenter is not null || machine is not null || category is not null) && Has("orders", "constraint", "bottleneck"))
        {
            return AnalystIntent.AffectedOrders;
        }

        return ReportOf(t) is not null && Has("show", "list", "find", "how many", "how much", "what", "which") ? AnalystIntent.Report : AnalystIntent.Unsupported;
    }

    // ------------------------------------------------------------------ reports

    /// <summary>The standard report a phrase names, if any.</summary>
    internal static string? ReportOf(string t)
    {
        bool Has(params string[] words) => words.Any(w => t.Contains(" " + w, StringComparison.Ordinal));
        return Has("expir") && Has("cert", "qualif", "training") ? "qualifications-expiring"
            : Has("attainment") ? "schedule-attainment"
            : Has("constraint", "bottleneck") ? "constraint-history"
            : Has("pm compliance", "preventive", "work order", "maintenance work", "maintenance compliance", "pm ") ? "pm-compliance"
            : Has("downtime", "outage", "machine down", "breakdown") ? "downtime"
            : Has("contract deliver", "clin", "program deliver") ? "program-deliveries"
            : Has("deliver", "on-time", "on time", "shipped", "shipping performance") ? "delivery-performance"
            : Has("ncr", "nonconform", "non-conform", "defect") ? "ncr-summary"
            : Has("inspection", "first pass", "first-pass", "yield") ? "inspection-results"
            : Has("transaction", "stock movement", "inventory movement", "ledger", "receipts", "issues") ? "inventory-transactions"
            : Has("output", "production", "plan vs", "plan versus", "produced", "missed plan", "miss plan") ? "production-output"
            : null;
    }

    private static AnalystReportRequest? BuildReport(string t, AnalystClock clock, AnalystPeriod? period, string? machine, string? workCenter, string? part, VocabularyProgram? program, VocabularyShift? shift, List<string> ambiguities)
    {
        var id = ReportOf(t);
        if (id is null)
        {
            return null;
        }

        var entry = StandardReports.Find(id)!.Entry;
        var notes = new List<string>();
        bool Filter(string key) => entry.Filters.Any(f => f.Key == key);

        // Period: as asked, or the report's default; clamped to what the report allows.
        DateOnly? from = period?.From, to = period?.To;
        if (period is null)
        {
            notes.Add(entry.Forward ? "No period given: the next 90 days." : "No period given: the last 30 days.");
        }
        else if (to!.Value.DayNumber - from!.Value.DayNumber + 1 > entry.MaxDays)
        {
            from = to.Value.AddDays(1 - entry.MaxDays);
            notes.Add($"{entry.Title} covers at most {entry.MaxDays} days, so the period starts {from:yyyy-MM-dd}.");
        }

        // Grouping: "by machine", "per work center", "grouped by part"; "compare … shifts" groups by shift.
        string? groupBy = null;
        if (ByRegex().Match(t) is { Success: true } by)
        {
            var words = by.Groups[1].Value;
            groupBy = entry.GroupBy.FirstOrDefault(g =>
            {
                var title = Normalize(entry.Columns.First(c => c.Key == g).Title);
                return words.StartsWith(title, StringComparison.Ordinal) || words.StartsWith(g.ToLowerInvariant(), StringComparison.Ordinal)
                    || (title.Length >= 4 && words.StartsWith(title[..4], StringComparison.Ordinal));
            });
            if (groupBy is null)
            {
                ambiguities.Add($"{entry.Title} cannot be grouped by “{words.Split(' ')[0]}”; it can be grouped by {string.Join(", ", entry.GroupBy.Select(g => entry.Columns.First(c => c.Key == g).Title.ToLowerInvariant()))}.");
            }
        }

        if (groupBy is null && t.Contains(" compare", StringComparison.Ordinal) && t.Contains("shift", StringComparison.Ordinal) && entry.GroupBy.Contains("shift"))
        {
            groupBy = "shift";
        }

        groupBy ??= entry.DefaultGroupBy;
        var compareShifts = t.Contains(" compare", StringComparison.Ordinal) && groupBy == "shift";
        var parameters = new ReportParameters(
            from, to, null,
            Filter("programId") ? program?.ProgramId : null,
            Filter("workCenter") ? workCenter : null,
            Filter("machine") ? machine : null,
            Filter("partNumber") ? part : null,
            Status(t, entry),
            null,
            groupBy)
        {
            Shift = Filter("shift") && !compareShifts ? shift?.Code : null,
        };

        foreach (var (given, key, what) in new[] { (workCenter, "workCenter", "work center"), (machine, "machine", "machine"), (part, "partNumber", "part"), (program?.Code, "programId", "program"), (shift?.Code, "shift", "shift") })
        {
            if (given is not null && !Filter(key) && !(key == "shift" && compareShifts))
            {
                notes.Add($"{entry.Title} has no {what} filter, so {given} is not applied.");
            }
        }

        var output = t.Contains(" excel", StringComparison.Ordinal) || t.Contains(" xlsx", StringComparison.Ordinal) || t.Contains(" spreadsheet", StringComparison.Ordinal) ? "xlsx"
            : t.Contains(" csv", StringComparison.Ordinal) ? "csv"
            : t.Contains(" pdf", StringComparison.Ordinal) || t.Contains(" print", StringComparison.Ordinal) ? "pdf"
            : "screen";
        if (t.Contains(" graph", StringComparison.Ordinal) || t.Contains(" chart", StringComparison.Ordinal))
        {
            notes.Add("Forge shows this as a table with totals; charts are not part of reports yet.");
        }

        _ = clock;
        return new AnalystReportRequest(entry.Id, entry.Title, parameters, output, notes);
    }

    private static string? Status(string t, ReportCatalogEntry entry)
    {
        var choices = entry.Filters.FirstOrDefault(f => f.Key == "status")?.Kind.Replace("choice:", string.Empty, StringComparison.Ordinal).Split('|') ?? [];
        return choices.FirstOrDefault(c => c.Length > 3 && t.Contains(" " + Normalize(c) + " ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ periods

    internal static (AnalystPeriod? Period, DateTimeOffset? At) ParsePeriod(string t, AnalystClock clock)
    {
        var today = clock.Today;
        AnalystPeriod P(DateOnly a, DateOnly b, string label) => new(a <= b ? a : b, a <= b ? b : a, label);

        if (IsoRangeRegex().Match(t) is { Success: true } iso)
        {
            var a = DateOnly.ParseExact(iso.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var b = iso.Groups[2].Success ? DateOnly.ParseExact(iso.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture) : a;
            return (P(a, b, iso.Value.Trim()), null);
        }

        if (MonthRangeRegex().Match(t) is { Success: true } mr && MonthOf(mr.Groups[1].Value) is { } month)
        {
            var year = today.Year;
            var d1 = int.Parse(mr.Groups[2].Value, CultureInfo.InvariantCulture);
            var d2 = mr.Groups[3].Success ? int.Parse(mr.Groups[3].Value, CultureInfo.InvariantCulture) : d1;
            var last = DateTime.DaysInMonth(year, month);
            if (d1 >= 1 && d1 <= last && d2 >= d1 && d2 <= last)
            {
                var a = new DateOnly(year, month, d1);
                if (a > today)
                {
                    a = a.AddYears(-1);
                }

                return (P(a, a.AddDays(d2 - d1), mr.Value.Trim()), null);
            }
        }

        if (RelativeRegex().Match(t) is { Success: true } rel)
        {
            var n = int.Parse(rel.Groups[2].Value, CultureInfo.InvariantCulture);
            var unit = rel.Groups[3].Value;
            var days = unit.StartsWith('w') ? n * 7 : unit.StartsWith('m') ? n * 30 : n;
            var forward = rel.Groups[1].Value is "next" or "coming";
            return forward
                ? (P(today, today.AddDays(days - 1), rel.Value.Trim()), null)
                : (P(today.AddDays(1 - days), today, rel.Value.Trim()), clock.Now.AddDays(-days));
        }

        if (SinceRegex().Match(t) is { Success: true } since)
        {
            var what = since.Groups[1].Value;
            if (what == "yesterday")
            {
                return (P(today.AddDays(-1), today, "since yesterday"), clock.ToInstant(today.AddDays(-1), TimeOnly.MinValue));
            }

            if (what is "today" or "this morning" or "midnight")
            {
                return (P(today, today, "since " + what), clock.ToInstant(today, TimeOnly.MinValue));
            }

            if (what is "last week")
            {
                return (P(today.AddDays(-7), today, "since last week"), clock.Now.AddDays(-7));
            }

            if (DayOfWeekOf(what) is { } dow)
            {
                var back = ((int)today.DayOfWeek - (int)dow + 7) % 7;
                back = back == 0 ? 7 : back;
                var d = today.AddDays(-back);
                return (P(d, today, "since " + what), clock.ToInstant(d, TimeOnly.MinValue));
            }

            if (ClockRegex().Match(" " + what + " ") is { Success: true } ck && TimeOf(ck) is { } time)
            {
                var day = time <= clock.TimeOfDay ? today : today.AddDays(-1);
                return (P(day, today, "since " + what), clock.ToInstant(day, time));
            }
        }

        bool Has(string w) => t.Contains(" " + w + " ", StringComparison.Ordinal);
        if (Has("tonight"))
        {
            return (P(today, today, "tonight"), clock.ToInstant(today, new TimeOnly(22, 0)));
        }

        if (Has("yesterday"))
        {
            return (P(today.AddDays(-1), today.AddDays(-1), "yesterday"), clock.Now.AddDays(-1));
        }

        if (Has("tomorrow"))
        {
            return (P(today.AddDays(1), today.AddDays(1), "tomorrow"), clock.ToInstant(today.AddDays(1), new TimeOnly(6, 0)));
        }

        if (Has("today") || Has("now") || Has("right now") || Has("currently"))
        {
            return (P(today, today, "today"), clock.Now);
        }

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (Has("this week"))
        {
            return (P(monday, today, "this week"), null);
        }

        if (Has("last week") || Has("past week"))
        {
            return (P(monday.AddDays(-7), monday.AddDays(-1), "last week"), clock.Now.AddDays(-7));
        }

        if (Has("next week"))
        {
            return (P(monday.AddDays(7), monday.AddDays(13), "next week"), null);
        }

        var first = new DateOnly(today.Year, today.Month, 1);
        if (Has("this month") || Has("month to date"))
        {
            return (P(first, today, "this month"), null);
        }

        if (Has("last month") || Has("past month"))
        {
            return (P(first.AddMonths(-1), first.AddDays(-1), "last month"), null);
        }

        if (Has("this year") || Has("year to date"))
        {
            return (P(new DateOnly(today.Year, 1, 1), today, "this year"), null);
        }

        return (null, null);
    }

    // ------------------------------------------------------------------ helpers

    private static VocabularyShift? ParseShift(string t, IReadOnlyList<VocabularyShift> shifts, List<string> ambiguities)
    {
        if (shifts.Count == 0)
        {
            return null;
        }

        var ordered = shifts.OrderBy(s => s.Start).ThenBy(s => s.Code, StringComparer.Ordinal).ToList();
        foreach (var s in ordered)
        {
            if (Contains(t, s.Code + " shift") || Contains(t, s.Name) || Contains(t, "shift " + s.Code))
            {
                return s;
            }
        }

        if (ShiftOrdinalRegex().Match(t) is { Success: true } m)
        {
            var word = m.Groups[1].Value;
            var index = word switch { "first" or "1st" or "day" => 0, "second" or "2nd" or "swing" => 1, "third" or "3rd" or "night" or "graveyard" => 2, _ => -1 };
            if (word == "night" && ordered.Count == 2)
            {
                index = 1;
            }

            if (index >= 0 && index < ordered.Count)
            {
                return ordered[index];
            }

            ambiguities.Add($"There is no {word} shift; the site's shifts are {string.Join(", ", ordered.Select((s, i) => $"{s.Name} ({Ordinals[Math.Min(i, 3)]})"))}.");
        }

        return null;
    }

    private static string? CategoryOf(string t)
    {
        bool Has(params string[] words) => words.Any(w => t.Contains(" " + w, StringComparison.Ordinal));
        return Has("material", "shortage of parts", "parts shortage") ? "Material"
            : Has("labor", "labour", "staff", "people", "crew", "qualified") ? "Labor"
            : Has("maintenance", "downtime", "outage", "breakdown") ? "Maintenance"
            : Has("safety") ? "Safety"
            : Has("quality", "inspection", "ncr") ? "Quality"
            : Has("capacity", "bottleneck") ? "Capacity"
            : null;
    }

    private static VocabularyPerson? FirstNameMatch(IReadOnlyList<VocabularyPerson> people, string t, List<string> ambiguities)
    {
        var hits = people
            .Where(p => p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts && parts[0].Length >= 3 && Contains(t, parts[0]))
            .ToList();
        if (hits.Count > 1)
        {
            ambiguities.Add($"Several people match: {string.Join(", ", hits.Select(h => h.Name))}.");
            return null;
        }

        return hits.Count == 1 ? hits[0] : null;
    }

    private static string Describe(Interpretation i, AnalystVocabulary v)
    {
        var parts = new List<string>();
        string? Person() => i.PersonId is { } id ? v.People.FirstOrDefault(p => p.PersonId == id)?.Name : null;
        string? Program() => i.ProgramId is { } id ? v.Programs.FirstOrDefault(p => p.ProgramId == id)?.Code : null;
        string? Shift() => i.ShiftCode is { } c ? v.Shifts.FirstOrDefault(s => s.Code == c)?.Name : null;
        parts.Add(i.Intent switch
        {
            AnalystIntent.OrderStatus => i.OrderNumber is { } o ? $"Status of order {o} and what is holding it" : "Status of an order",
            AnalystIntent.LateOrders => "Orders late or at risk in the approved schedule",
            AnalystIntent.UnavailableMachines => "Machines unavailable",
            AnalystIntent.MachineBlockers => $"What prevents {i.Machine ?? "a machine"} from running",
            AnalystIntent.QualifiedPeople => $"People qualified for {i.Machine ?? i.WorkCenter ?? "work"}",
            AnalystIntent.WhatChanged => "Changes to the approved schedule",
            AnalystIntent.AffectedOrders => $"Orders affected by {i.Machine ?? i.WorkCenter ?? Person() ?? (i.Category is { } c ? c.ToLowerInvariant() + " constraints" : "…")}",
            AnalystIntent.PersonImpact => $"Orders that depend on {Person()} being available",
            AnalystIntent.Simulate => $"Scenario: {i.Machine ?? Person() ?? "…"} unavailable" + (i.Hours is { } h ? $" for {h:0.#} h" : string.Empty),
            AnalystIntent.Report => $"Report: {i.Report!.Title}",
            AnalystIntent.Brief => "Operational brief",
            _ => "Not a question about your Forge operation",
        });

        if (i.Intent == AnalystIntent.Report && i.Report is { } r)
        {
            var p = r.Parameters;
            if (p.From is { } f)
            {
                parts.Add(f == p.To ? $"{f:MMM d}" : $"{f:MMM d}–{p.To:MMM d}");
            }

            parts.AddRange(new[] { p.WorkCenter is { } w ? "work center " + w : null, p.Machine is { } m ? "machine " + m : null, p.PartNumber is { } pn ? "part " + pn : null, Program() is { } pc && p.ProgramId is not null ? "program " + pc : null, p.Shift is not null ? Shift() : null, p.Status is { } s ? s : null }.OfType<string>());
            if (p.GroupBy is { } g)
            {
                parts.Add("grouped by " + StandardReports.Find(r.ReportId)!.Entry.Columns.First(c => c.Key == g).Title.ToLowerInvariant());
            }

            parts.Add(r.Output switch { "xlsx" => "Excel", "csv" => "CSV", "pdf" => "PDF (print)", _ => "on screen" });
        }
        else
        {
            if (i.Period is { } period && i.Intent is not AnalystIntent.OrderStatus)
            {
                parts.Add(period.Label);
            }

            if (Shift() is { } s)
            {
                parts.Add(s);
            }

            if (Program() is { } pc)
            {
                parts.Add("program " + pc);
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Lower case, possessives and apostrophes dropped ("sarah's" → "sarah", "can't" → "cant"), punctuation to spaces.</summary>
    internal static string Normalize(string s)
    {
        var t = PossessiveRegex().Replace(s.ToLowerInvariant().Replace('’', '\''), string.Empty).Replace("'", string.Empty, StringComparison.Ordinal);
        t = PunctuationRegex().Replace(t, " ");
        t = TrailingDotRegex().Replace(t, " ");
        return WhitespaceRegex().Replace(t, " ").Trim();
    }

    private static bool Contains(string normalizedText, string phrase)
    {
        var p = Normalize(phrase);
        return p.Length > 0 && normalizedText.Contains(" " + p + " ", StringComparison.Ordinal);
    }

    private static string Mask(string normalizedText, string phrase)
    {
        var p = Normalize(phrase);
        return p.Length == 0 ? normalizedText : normalizedText.Replace(" " + p + " ", " _ ", StringComparison.Ordinal);
    }

    /// <summary>The first four letters of each word of a name: "Slitting" → "slit", so "slitting" and "slit" both match.</summary>
    private static IEnumerable<string> Stems(string name) =>
        Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && w != "work" && w != "center" && w != "area" && w != "line").Select(w => w[..4]);

    private static bool WordStartsWith(string normalizedText, string stem) => normalizedText.Contains(" " + stem, StringComparison.Ordinal);

    private static int? MonthOf(string word)
    {
        string[] months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
        var i = Array.FindIndex(months, m => word.StartsWith(m, StringComparison.Ordinal));
        return i < 0 ? null : i + 1;
    }

    private static DayOfWeek? DayOfWeekOf(string word) => word switch
    {
        "monday" => DayOfWeek.Monday,
        "tuesday" => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday,
        "friday" => DayOfWeek.Friday,
        "saturday" => DayOfWeek.Saturday,
        "sunday" => DayOfWeek.Sunday,
        _ => null,
    };

    private static TimeOnly? TimeOf(Match m)
    {
        var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = m.Groups[3].Value;
        if (meridiem.StartsWith('p') && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem.StartsWith('a') && hour == 12)
        {
            hour = 0;
        }

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    [GeneratedRegex(@"'s\b")]
    private static partial Regex PossessiveRegex();

    [GeneratedRegex(@"\.(?=\s|$)")]
    private static partial Regex TrailingDotRegex();

    [GeneratedRegex(@"[^a-z0-9\-/:\.# ]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:order|orders|wo|job|#)\s*(?:number|no\.?|#)?\s*#?(\d{3,9})\b")]
    private static partial Regex OrderRegex();

    [GeneratedRegex(@"(?<=\s)\d{4,9}(?=\s)")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\bmachine\s+([a-z0-9]{1,2})\b")]
    private static partial Regex MachineLetterRegex();

    [GeneratedRegex(@"\bfor\s+(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|days?)\b")]
    private static partial Regex HoursRegex();

    [GeneratedRegex(@"\b(?:by|per|grouped by|group by|broken down by|split by)\s+([a-z ]+?)(?:\s+(?:for|over|in|during|from|since|last|this|next|as|to)\b|\s*$)")]
    private static partial Regex ByRegex();

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2})(?:\s*(?:to|-|through|until|and)\s*(\d{4}-\d{2}-\d{2}))?\b")]
    private static partial Regex IsoRangeRegex();

    [GeneratedRegex(@"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(\d{1,2})(?:\s*(?:-|to|through)\s*(\d{1,2}))?\b")]
    private static partial Regex MonthRangeRegex();

    [GeneratedRegex(@"\b(last|past|previous|next|coming)\s+(\d{1,3})\s+(days?|weeks?|months?)\b")]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"\bsince\s+(yesterday|today|this morning|midnight|last week|monday|tuesday|wednesday|thursday|friday|saturday|sunday|\d{1,2}(?::\d{2})?\s*(?:am|pm|a|p)?)\b")]
    private static partial Regex SinceRegex();

    [GeneratedRegex(@"\s(\d{1,2})(?::(\d{2}))?\s*(am|pm|a|p)?\s")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"\b(first|1st|second|2nd|third|3rd|day|swing|night|graveyard)\s+shift\b")]
    private static partial Regex ShiftOrdinalRegex();
}
