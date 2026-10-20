using System.Globalization;

namespace Forge.Simulation.Tests.Factory;

/// <summary>Size and seed of a factory run. The owner chose 300 orders and 70 operators; 90 days of simulated time.</summary>
public sealed record FactorySize(int Orders, int Operators, int Days, int Seed)
{
    public static FactorySize Owner => new(300, 70, 90, 42);

    /// <summary>A small factory for quick iteration; same structure, fewer orders, people and days.</summary>
    public static FactorySize Smoke => new(40, 30, 14, 42);

    /// <summary>Reads FORGE_FACTORY_ORDERS, _OPERATORS, _DAYS and _SEED; FORGE_FACTORY_SIZE=smoke selects the small factory.</summary>
    public static FactorySize FromEnvironment()
    {
        var size = string.Equals(Environment.GetEnvironmentVariable("FORGE_FACTORY_SIZE"), "smoke", StringComparison.OrdinalIgnoreCase) ? Smoke : Owner;
        static int? Read(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        return new FactorySize(Read("FORGE_FACTORY_ORDERS") ?? size.Orders, Read("FORGE_FACTORY_OPERATORS") ?? size.Operators, Read("FORGE_FACTORY_DAYS") ?? size.Days, Read("FORGE_FACTORY_SEED") ?? size.Seed);
    }

    public override string ToString() => $"{Orders} orders, {Operators} operators, {Days} days, seed {Seed}";
}

/// <summary>A shift: start hour (UTC, the site's zone), length, Monday to Friday.</summary>
public sealed record ShiftSpec(string Code, string Name, int StartHour, int Hours);

/// <summary>A department: its safety area, work center, machines (or work-center activity), qualification and shifts.</summary>
public sealed record DepartmentSpec(
    string Area,
    string WorkCenter,
    string Activity,
    string? Qualification,
    IReadOnlyList<string> Machines,
    IReadOnlyList<string> Shifts,
    int PeoplePerShift,
    bool TwoPerson,
    bool Existing)
{
    /// <summary>Work-center activities (no machine) are limited by people; machine activities by machines and people.</summary>
    public bool MachineWork => Machines.Count > 0;
}

public sealed record WorkerSpec(string Subject, string Department, string Shift, IReadOnlyList<string> Qualifications, bool Existing);

public sealed record SupplierSpec(string Code, string Name, int LeadTimeDays);

public sealed record PartSpec(string Part, string Description, string Uom, bool Make, int LeadTimeDays, string? Supplier, bool LotTracked, decimal OpeningStock);

public sealed record BomLineSpec(int Line, string Component, decimal QuantityPer);

/// <summary>One routing step. Run minutes per unit are calibrated by <see cref="FactorySpec"/> to a target load.</summary>
public sealed record OperationSpec(int Sequence, string Department, decimal SetupMinutes, decimal RunMinutesPerUnit, bool Inspected);

public sealed record ProductSpec(string Part, string Description, IReadOnlyList<BomLineSpec> Bom, IReadOnlyList<OperationSpec> Routing);

/// <summary>A customer order: when it arrives (days from the start), what, how many, when it is needed and how urgent.</summary>
public sealed record DemandSpec(int Index, int ArrivalDay, string Customer, string Part, decimal Quantity, int NeedDay, int Priority);

/// <summary>
/// The synthetic organization, derived deterministically from a <see cref="FactorySize"/>. Eight departments (the reference
/// plant's Slitting, Paint and Assembly plus Forming, Welding, Machining, Testing and Packing), three shifts, operators with
/// different qualifications, bought raw material with five suppliers and realistic lead times, six made intermediates (WIP),
/// twelve finished products with multi-level BOMs and multi-department routings, approved alternates, a demand stream of
/// competing customer orders with priorities, and run times calibrated so every department is planned at about 70% of its capacity.
/// Nothing here touches Forge: <see cref="FactoryBuilder"/> creates it through the real API.
/// </summary>
public sealed class FactorySpec
{
    /// <summary>Planned time for the functional test inspection (ADR 0057 §2): the plan leaves this gap after the test operation.</summary>
    public const int TestInspectionMinutes = 30;

    public const string Day = "DAY";
    public const string Swing = "SWING";
    public const string Night = "NIGHT";
    public const decimal TargetLoad = 0.7m;

    /// <summary>
    /// The site's time zone. Forge creates PLANT1 in America/New_York (its default), and shifts are local times there,
    /// so the simulation keeps its day in that zone too, including the change from daylight saving time.
    /// </summary>
    public static readonly NodaTime.DateTimeZone SiteZone = NodaTime.DateTimeZoneProviders.Tzdb["America/New_York"];

    /// <summary>A local site time (date plus hours) as an instant. Uses the time-zone database Forge itself uses (NodaTime).</summary>
    public static DateTimeOffset Local(DateOnly date, double hours)
    {
        var local = new NodaTime.LocalDateTime(date.Year, date.Month, date.Day, 0, 0).PlusMinutes((long)Math.Round(hours * 60));
        return local.InZoneLeniently(SiteZone).ToDateTimeOffset().ToUniversalTime();
    }

    /// <summary>The site's local date of an instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset at)
    {
        var d = NodaTime.Instant.FromDateTimeOffset(at).InZone(SiteZone).Date;
        return new DateOnly(d.Year, d.Month, d.Day);
    }

    /// <summary>The next Monday at 00:00 at the site.</summary>
    public static DateTimeOffset NextMondayAtSite()
    {
        var today = LocalDate(DateTimeOffset.UtcNow);
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return Local(today.AddDays(days == 0 ? 7 : days), 0);
    }

    public static readonly IReadOnlyList<ShiftSpec> Shifts =
    [
        new(Day, "Day shift", 6, 8),
        new(Swing, "Swing shift", 14, 8),
        new(Night, "Night shift", 22, 8),
    ];

    public FactorySpec(FactorySize size)
    {
        Size = size;
        var random = new Random(size.Seed);

        Departments =
        [
            new("SLITTING", "SLIT-WC", "RUN_SLITTER", "SLIT-II", ["SLITTER-A", "SLITTER-B"], [Day, Swing], 3, true, true),
            new("FORMING", "FORM-WC", "FORM", "FORM-I", ["PRESS-1", "PRESS-2"], [Day, Swing], 3, false, false),
            new("WELDING", "WELD-WC", "WELD", "WELD-CERT", ["WELD-1", "WELD-2", "WELD-3"], [Day, Swing], 4, true, false),
            new("MACHINING", "MACH-WC", "MACHINE", "CNC-I", ["CNC-1", "CNC-2"], [Day, Swing, Night], 3, false, false),
            new("PAINT", "PAINT-WC", "PAINT", "PAINT-I", ["PAINT-BOOTH", "PAINT-BOOTH-2"], [Day, Swing], 3, false, true),
            new("ASSEMBLY", "ASSY-WC", "ASSEMBLE", "ASSY-I", [], [Day, Swing], 6, false, true),
            new("TESTING", "TEST-WC", "TEST", "TEST-I", ["TEST-1", "TEST-2"], [Day, Swing], 3, false, false),
            new("PACKING", "PACK-WC", "PACK", null, [], [Day], 5, false, false),
        ];

        Workers = BuildWorkers(size.Operators, random);

        Suppliers =
        [
            new("SUP-STEEL", "Northern Steel", 10),
            new("SUP-TUBE", "Tube and Bar Co", 14),
            new("SUP-ALU", "Alu Castings", 12),
            new("SUP-FAST", "Fastener Supply", 5),
            new("SUP-PACK", "Packaging Direct", 7),
        ];

        // Raw material (bought). Opening stock covers about three weeks; MRP buys the rest.
        Raw =
        [
            new("SHEET-STEEL", "Steel sheet", "KG", false, 10, "SUP-STEEL", true, 0m),
            new("SHEET-STEEL-EQ", "Steel sheet, equivalent mill", "KG", false, 10, "SUP-STEEL", true, 0m),
            new("TUBE-STEEL", "Steel tube", "M", false, 14, "SUP-TUBE", false, 0m),
            new("BAR-STOCK", "Bar stock", "M", false, 14, "SUP-TUBE", false, 0m),
            new("ALU-BLOCK", "Aluminium block", "EA", false, 12, "SUP-ALU", false, 0m),
            new("FASTENER-KIT", "Fastener kit", "EA", false, 5, "SUP-FAST", false, 0m),
            new("CARTON", "Shipping carton", "EA", false, 7, "SUP-PACK", false, 0m),
            new("COIL-AL", "Aluminum coil", "KG", false, 14, "SUP-ALU", false, 0m),
        ];

        Intermediates =
        [
            new("SUB-BRKT", "Formed bracket", [new(1, "SHEET-STEEL", 1.5m)], [new(10, "FORMING", 15, 1, false)]),
            new("SUB-FRAME", "Welded frame", [new(1, "TUBE-STEEL", 3m)], [new(10, "MACHINING", 20, 1, false), new(20, "WELDING", 10, 1, false)]),
            new("SUB-SHAFT", "Machined shaft", [new(1, "BAR-STOCK", 0.5m)], [new(10, "MACHINING", 30, 1, false)]),
            new("SUB-STRIP", "Slit strip", [new(1, "COIL-AL", 2.5m)], [new(10, "SLITTING", 30, 1, false)]),
            new("SUB-HOUSING", "Painted housing", [new(1, "ALU-BLOCK", 1m)], [new(10, "MACHINING", 20, 1, false), new(20, "PAINT", 15, 1, false)]),
            new("SUB-PANEL", "Painted panel", [new(1, "SHEET-STEEL", 2m)], [new(10, "FORMING", 10, 1, false), new(20, "PAINT", 15, 1, false)]),
        ];

        string[] subs = ["SUB-BRKT", "SUB-FRAME", "SUB-SHAFT", "SUB-STRIP", "SUB-HOUSING", "SUB-PANEL"];
        var finished = new List<ProductSpec>();
        for (var i = 0; i < 12; i++)
        {
            var first = subs[i % subs.Length];
            var second = subs[(i * 5 + 2) % subs.Length];
            var bom = new List<BomLineSpec> { new(1, first, 1m) };
            if (second != first)
            {
                bom.Add(new(2, second, i % 3 == 0 ? 2m : 1m));
            }

            bom.Add(new(bom.Count + 1, "FASTENER-KIT", 1m));
            bom.Add(new(bom.Count + 1, "CARTON", 1m));
            var routing = new List<OperationSpec>();
            var seq = 10;
            if (i % 4 == 1)
            {
                routing.Add(new(seq, "WELDING", 15, 1, false));
                seq += 10;
            }

            routing.Add(new(seq, "ASSEMBLY", 10, 1, false));
            seq += 10;
            if (i % 3 == 2)
            {
                routing.Add(new(seq, "PAINT", 15, 1, false));
                seq += 10;
            }

            routing.Add(new(seq, "TESTING", 10, 1, true));
            seq += 10;
            routing.Add(new(seq, "PACKING", 5, 1, false));
            finished.Add(new($"PROD-{i + 1:00}", $"Finished product {i + 1:00}", bom, routing));
        }

        Products = finished;
        Customers = Enumerable.Range(1, 12).Select(i => $"Customer {i:00}").ToList();
        Demand = BuildDemand(size, random);
        Calibrate();
        SetOpeningStock();
    }

    public FactorySize Size { get; }

    /// <summary>The day the test gauge's calibration falls due (a weekday a third of the way into the run).</summary>
    public int GaugeDueDay => Weekday(Math.Max(4, Size.Days / 3), forward: true);

    /// <summary>The nearest Monday-to-Friday day number (the run starts on a Monday, day 0).</summary>
    public static int Weekday(int day, bool forward)
    {
        while (day % 7 >= 5)
        {
            day += forward ? 1 : -1;
        }

        return Math.Max(0, day);
    }

    public static bool IsWeekday(int day) => day % 7 < 5;

    public IReadOnlyList<DepartmentSpec> Departments { get; }

    public IReadOnlyList<WorkerSpec> Workers { get; }

    public IReadOnlyList<SupplierSpec> Suppliers { get; }

    public IReadOnlyList<PartSpec> Raw { get; private set; }

    public IReadOnlyList<ProductSpec> Intermediates { get; private set; }

    public IReadOnlyList<ProductSpec> Products { get; private set; }

    public IReadOnlyList<string> Customers { get; }

    public IReadOnlyList<DemandSpec> Demand { get; }

    /// <summary>The departmental load the calibration aimed for, by department, as a fraction of weekly capacity.</summary>
    public IReadOnlyDictionary<string, decimal> PlannedLoad { get; private set; } = new Dictionary<string, decimal>();

    public IEnumerable<ProductSpec> AllMade => Intermediates.Concat(Products);

    public DepartmentSpec Department(string area) => Departments.Single(d => d.Area == area);

    public ProductSpec Made(string part) => AllMade.Single(p => p.Part == part);

    public bool IsMade(string part) => AllMade.Any(p => p.Part == part);

    public PartSpec RawPart(string part) => Raw.Single(p => p.Part == part);

    /// <summary>The reference plant's four people keep their departments; everyone else is new. Each department and shift is staffed in proportion.</summary>
    private List<WorkerSpec> BuildWorkers(int operators, Random random)
    {
        var slots = Departments.SelectMany(d => d.Shifts.Select(s => (Dept: d, Shift: s))).ToList();
        var planned = slots.Sum(s => s.Dept.PeoplePerShift);
        var counts = slots.ToDictionary(s => s, s => Math.Max(s.Dept.TwoPerson ? 2 : 1, (int)Math.Round((decimal)s.Dept.PeoplePerShift * operators / planned, MidpointRounding.AwayFromZero)));
        while (counts.Values.Sum() > operators)
        {
            var largest = counts.Where(c => c.Value > (c.Key.Dept.TwoPerson ? 2 : 1)).OrderByDescending(c => c.Value).ThenBy(c => c.Key.Dept.Area, StringComparer.Ordinal).First().Key;
            counts[largest]--;
        }

        var extra = 0;
        while (counts.Values.Sum() < operators)
        {
            var slot = slots[extra++ % slots.Count];
            counts[slot]++;
        }

        var existing = new Dictionary<(string Area, string Shift), Queue<(string Subject, string[] Quals)>>
        {
            [("SLITTING", Day)] = new([("sarah", ["SLIT-II", "FIRST-AID"]), ("michael", ["SLIT-II"])]),
            [("PAINT", Day)] = new([("jennifer", ["PAINT-I", "FIRST-AID"])]),
            [("ASSEMBLY", Day)] = new([("dana", Array.Empty<string>())]),
        };

        var workers = new List<WorkerSpec>();
        var number = 0;
        foreach (var slot in slots)
        {
            for (var i = 0; i < counts[slot]; i++)
            {
                if (existing.TryGetValue((slot.Dept.Area, slot.Shift), out var queue) && queue.Count > 0)
                {
                    var (subject, quals) = queue.Dequeue();
                    var list = quals.ToList();
                    if (slot.Dept.Qualification is { } q && !list.Contains(q))
                    {
                        list.Add(q);
                    }

                    workers.Add(new(subject, slot.Dept.Area, slot.Shift, list, true));
                    continue;
                }

                number++;
                var qualifications = new List<string>();
                if (slot.Dept.Qualification is { } own)
                {
                    qualifications.Add(own);
                }

                // About a third are first-aid responders; about one in five is cross-trained in another department.
                if (random.Next(3) == 0)
                {
                    qualifications.Add("FIRST-AID");
                }

                if (random.Next(5) == 0)
                {
                    var other = Departments[random.Next(Departments.Count)].Qualification;
                    if (other is not null && !qualifications.Contains(other))
                    {
                        qualifications.Add(other);
                    }
                }

                workers.Add(new($"op{number:000}", slot.Dept.Area, slot.Shift, qualifications, false));
            }
        }

        return workers;
    }

    /// <summary>
    /// A quarter of the orders are open at the start (due in 10–30 days); the rest arrive over the run with 14–28 days of lead
    /// time. Quantities 10–50; priority 1 (urgent) to 5; product popularity is uneven, as in real order books.
    /// </summary>
    private List<DemandSpec> BuildDemand(FactorySize size, Random random)
    {
        var list = new List<DemandSpec>();
        var backlog = size.Orders / 4;
        var arrivalSpan = Math.Max(1, size.Days * 3 / 4);
        for (var i = 0; i < size.Orders; i++)
        {
            var arrival = i < backlog ? 0 : 1 + (int)((long)(i - backlog) * arrivalSpan / Math.Max(1, size.Orders - backlog));
            var need = arrival + (i < backlog ? 10 + random.Next(21) : 14 + random.Next(15));
            var product = (int)Math.Floor(Math.Pow(random.NextDouble(), 1.6) * 12);
            var priority = random.Next(100) switch { < 8 => 1, < 25 => 2, < 70 => 3, < 90 => 4, _ => 5 };
            list.Add(new(i + 1, arrival, Customers[random.Next(Customers.Count)], $"PROD-{product + 1:00}", 10 + random.Next(41), need, priority));
        }

        return list;
    }

    /// <summary>Units per week each made part needs, from the demand stream through the BOMs.</summary>
    private Dictionary<string, decimal> WeeklyUnits()
    {
        var weeks = Math.Max(1m, Size.Days / 7m);
        var units = AllMade.ToDictionary(p => p.Part, _ => 0m, StringComparer.Ordinal);
        foreach (var d in Demand)
        {
            units[d.Part] += d.Quantity / weeks;
            foreach (var line in Made(d.Part).Bom.Where(l => IsMade(l.Component)))
            {
                units[line.Component] += d.Quantity * line.QuantityPer / weeks;
            }
        }

        return units;
    }

    /// <summary>Orders per week each made part gets: one per customer order for finished parts, one per parent order for intermediates.</summary>
    private Dictionary<string, decimal> WeeklyOrders()
    {
        var weeks = Math.Max(1m, Size.Days / 7m);
        var orders = AllMade.ToDictionary(p => p.Part, _ => 0m, StringComparer.Ordinal);
        foreach (var d in Demand)
        {
            orders[d.Part] += 1m / weeks;
            foreach (var line in Made(d.Part).Bom.Where(l => IsMade(l.Component)))
            {
                orders[line.Component] += 1m / weeks;
            }
        }

        return orders;
    }

    /// <summary>
    /// Sets run minutes per unit so each department's expected weekly work (setups plus run time) is <see cref="TargetLoad"/>
    /// of its capacity: the hours its machines can be crewed, or the hours of its people for crew work.
    /// </summary>
    private void Calibrate()
    {
        var units = WeeklyUnits();
        var orders = WeeklyOrders();
        var load = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var dept in Departments)
        {
            var people = Workers.Where(w => w.Department == dept.Area).GroupBy(w => w.Shift).ToDictionary(g => g.Key, g => g.Count());
            var capacityMinutes = dept.Shifts.Sum(s => Math.Min(dept.MachineWork ? dept.Machines.Count : int.MaxValue, people.GetValueOrDefault(s))) * 8m * 60m * 5m;
            var weeklyUnits = AllMade.Sum(p => p.Routing.Count(o => o.Department == dept.Area) * units[p.Part]);
            var weeklySetup = AllMade.Sum(p => p.Routing.Where(o => o.Department == dept.Area).Sum(o => o.SetupMinutes) * orders[p.Part]);
            var perUnit = weeklyUnits == 0 ? 1m : Math.Max(0.2m, decimal.Round((TargetLoad * capacityMinutes - weeklySetup) / weeklyUnits, 1));
            load[dept.Area] = capacityMinutes == 0 ? 0m : decimal.Round((weeklySetup + perUnit * weeklyUnits) / capacityMinutes, 3);
            Intermediates = Intermediates.Select(p => Scale(p, dept.Area, perUnit)).ToList();
            Products = Products.Select(p => Scale(p, dept.Area, perUnit)).ToList();
        }

        PlannedLoad = load;

        static ProductSpec Scale(ProductSpec p, string area, decimal perUnit) =>
            p with { Routing = p.Routing.Select(o => o.Department == area ? o with { RunMinutesPerUnit = perUnit } : o).ToList() };
    }

    /// <summary>Three weeks of raw material and one week of intermediates on hand at the start; safety stock for MRP.</summary>
    private void SetOpeningStock()
    {
        var units = WeeklyUnits();
        var raw = Raw.ToDictionary(p => p.Part, _ => 0m, StringComparer.Ordinal);
        foreach (var made in AllMade)
        {
            foreach (var line in made.Bom.Where(l => raw.ContainsKey(l.Component)))
            {
                raw[line.Component] += units[made.Part] * line.QuantityPer;
            }
        }

        Raw = Raw.Select(p => p with { OpeningStock = p.Part.EndsWith("-EQ", StringComparison.Ordinal) ? decimal.Round(raw["SHEET-STEEL"], 0) : decimal.Round(raw[p.Part] * 3m, 0) }).ToList();
        OpeningIntermediates = Intermediates.ToDictionary(p => p.Part, p => decimal.Round(units[p.Part], 0), StringComparer.Ordinal);
        SafetyStock = Raw.Where(p => !p.Part.EndsWith("-EQ", StringComparison.Ordinal)).ToDictionary(p => p.Part, p => decimal.Round(raw[p.Part] * 1.5m, 0), StringComparer.Ordinal)
            .Concat(Intermediates.Select(p => KeyValuePair.Create(p.Part, decimal.Round(units[p.Part] * 0.5m, 0))))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Safety stock MRP keeps, by part.</summary>
    public IReadOnlyDictionary<string, decimal> SafetyStock { get; private set; } = new Dictionary<string, decimal>();

    /// <summary>Intermediates on hand at the start (one week's use).</summary>
    public IReadOnlyDictionary<string, decimal> OpeningIntermediates { get; private set; } = new Dictionary<string, decimal>();

    /// <summary>A one-page description of the organization, for the report (output 2).</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"Size: {Size}.",
            $"Departments ({Departments.Count}): " + string.Join("; ", Departments.Select(d => $"{d.Area} ({(d.MachineWork ? string.Join("/", d.Machines) : "crew work")}, shifts {string.Join("+", d.Shifts)}{(d.TwoPerson ? ", two-person rule" : string.Empty)})")),
            $"Operators: {Workers.Count}; first-aid responders {Workers.Count(w => w.Qualifications.Contains("FIRST-AID"))}; cross-trained {Workers.Count(w => w.Qualifications.Count(q => q != "FIRST-AID") > 1)}.",
            $"Suppliers: " + string.Join("; ", Suppliers.Select(s => $"{s.Name} ({s.LeadTimeDays} days)")),
            $"Parts: {Raw.Count} bought, {Intermediates.Count} intermediates (WIP), {Products.Count} finished; BOM levels 2; approved alternate SHEET-STEEL-EQ for SHEET-STEEL.",
            $"Customer orders: {Demand.Count} from {Customers.Count} customers; {Demand.Count(d => d.ArrivalDay == 0)} open at the start; priorities 1–5; quantities {Demand.Min(d => d.Quantity)}–{Demand.Max(d => d.Quantity)}.",
            "Run times calibrated so every department is planned at about 70% of its weekly capacity (setups included); safety stock of 1.5 weeks for bought parts and half a week for intermediates.",
        };
        return string.Join('\n', lines);
    }
}
