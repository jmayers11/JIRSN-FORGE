using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Factory;

/// <summary>
/// The full factory as built in Forge: the simulation's handle on the host plus everything the run needs to act as the
/// organization's people (badges, person ids, the role holders) and to judge Forge's answers independently.
/// </summary>
internal sealed class FactoryPlant(SimFactory f, FactorySpec spec)
{
    public const string FinishedGoods = "FG";
    public const string Gauge = "TG-1";

    public SimFactory F { get; } = f;

    public FactorySpec Spec { get; } = spec;

    public SimApi Api => F.Api;

    public SimClock Clock => F.Clock;

    /// <summary>Badge by worker subject.</summary>
    public Dictionary<string, string> Badges { get; } = new(StringComparer.Ordinal);

    /// <summary>Worker subject by person id, to read the scheduler's operator ids.</summary>
    public Dictionary<Guid, string> Subjects { get; } = [];

    /// <summary>How long the build took in wall time (simulated setup is instantaneous).</summary>
    public TimeSpan SetupWallTime { get; set; }

    public HttpClient Engineer => F.Plant.Engineer;

    public HttpClient Approver => F.Plant.Approver;

    public HttpClient Planner => F.Planner;

    public HttpClient Planner2 => Api.As("planner2");

    public HttpClient Stores => F.StoresUser;

    public HttpClient Counter => Api.As("ivy");

    public HttpClient Buyer => Api.As("bob");

    public HttpClient Purchasing => Api.As("pat");

    public HttpClient Inspector => Api.As("insp");

    public HttpClient QualityEngineer => Api.As("qe");

    public HttpClient QualityManager => Api.As("qm");

    public HttpClient Shipper => Api.As("ship");

    public HttpClient MaintenancePlanner => Api.As("mp");

    public HttpClient MaintenancePlanner2 => Api.As("mp2");

    public HttpClient Technician => Api.As("tech");

    public HttpClient Kiosk => Api.As("kiosk");

    public HttpClient Kiosk2 => Api.As("kiosk2");

    public Guid PersonId(string subject) => F.Plant.People[subject];

    public string Day(int fromStart) =>
        DateOnly.FromDateTime(Clock.Start.UtcDateTime).AddDays(fromStart).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Builds a <see cref="FactorySpec"/> in a fresh Forge database, only through authenticated commands, in the order a real
/// plant would: competencies, calendar, safety areas, work centers and machines, the two-person rules, people (with
/// qualifications, shifts, assignments and badges), engineering (items, BOMs, routings, releases, an approved alternate),
/// planning parameters, suppliers, opening stock, inspection plans and a gauge, and preventive maintenance plans.
/// Every setup command must succeed: a failed setup throws, because nothing after it would mean anything.
/// </summary>
internal static class FactoryBuilder
{
    public static async Task<FactoryPlant> BuildAsync(PostgresFixture postgres, FactorySpec spec, string keyPrefix)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var f = await SimFactory.BuildAsync(postgres, keyPrefix, clock: new SimClock(FactorySpec.NextMondayAtSite()));
        f.Api.KeepAllCalls = false;
        var plant = f.Plant;
        var fp = new FactoryPlant(f, spec);
        var (e, approver) = (plant.Engineer, plant.Approver);

        // People who run the organization (the reference plant already has eng, approver, planner, stores, insp, qe, qm, ship, pat).
        foreach (var (subject, role) in new[] { ("planner2", "Planner"), ("ivy", "InventoryController"), ("bob", "Buyer"), ("mp", "MaintenancePlanner"), ("mp2", "MaintenancePlanner"), ("tech", "MaintenanceTechnician"), ("kiosk", "ShopFloorTerminal"), ("kiosk2", "ShopFloorTerminal") })
        {
            await plant.Person(subject, role);
        }

        // Competencies the new departments need.
        foreach (var (code, name, kind) in new[] { ("FORM-I", "Press forming", "Machine"), ("WELD-CERT", "Certified welder", "Machine"), ("CNC-I", "CNC machining", "Machine"), ("ASSY-I", "Assembly", "Process"), ("TEST-I", "Functional test", "Machine") })
        {
            await plant.Ok(e, "/api/competency/qualification-types", new { code, name, kind, validityDays = 365 });
        }

        // Calendar: three shifts, Monday to Friday (.NET DayOfWeek numbers).
        foreach (var s in FactorySpec.Shifts)
        {
            await plant.Ok(e, "/api/resources/shifts", new { code = s.Code, siteCode = "PLANT1", name = s.Name, start = $"{s.StartHour:00}:00:00", durationMinutes = s.Hours * 60, days = new[] { 1, 2, 3, 4, 5 } });
        }

        foreach (var (code, name) in new[] { ("FORM", "Press forming"), ("WELD", "Welding"), ("MACHINE", "CNC machining"), ("TEST", "Functional test"), ("PACK", "Packing") })
        {
            await plant.Ok(e, "/api/resources/activity-types", new { code, name });
        }

        foreach (var dept in spec.Departments)
        {
            if (!dept.Existing)
            {
                await plant.Ok(e, "/api/resources/safety-areas", new { code = dept.Area, name = dept.Area, parent = "PLANT1" });
                await plant.Ok(e, "/api/resources/work-centers", new { code = dept.WorkCenter, name = dept.WorkCenter, parent = dept.Area });
            }

            foreach (var machine in dept.Machines.Where(m => m is not ("SLITTER-A" or "SLITTER-B" or "PAINT-BOOTH")))
            {
                await plant.Ok(e, "/api/resources/machines", new { code = machine, name = machine, parent = dept.WorkCenter });
                await plant.Ok(e, $"/api/resources/machines/{machine}/activities", new { activityCode = dept.Activity, operatorQualifications = new[] { dept.Qualification } });
            }

            if (!dept.MachineWork && dept.Area != "ASSEMBLY")
            {
                await plant.Ok(e, $"/api/resources/work-centers/{dept.WorkCenter}/activities", new { activityCode = dept.Activity });
            }

            foreach (var shift in dept.Shifts)
            {
                await plant.Ok(e, $"/api/resources/work-centers/{dept.WorkCenter}/shifts", new { shiftCode = shift });
            }
        }

        // Welding is a two-person area like Slitting (the reference plant's SLIT-AREA rule stays as it is).
        await plant.Ok(e, "/api/safety-rules/drafts", new
        {
            code = "WELD-AREA", title = "Two-person welding coverage", scopeKind = "SafetyArea", scopeCode = "WELDING", activityCodes = new[] { "WELD" },
            minimumPhysicalPeople = 2, prohibitLoneWork = true,
            slots = new object[]
            {
                new { name = "Welder", count = 1, satisfiedBy = new[] { "WELD-CERT" }, traineeMayFill = false },
                new { name = "Fire watch", count = 1, satisfiedBy = new[] { "FIRST-AID", "WELD-CERT" }, traineeMayFill = false },
            },
            reason = "Hot work procedure HW-3",
        });
        await plant.Ok(approver, "/api/safety-rules/WELD-AREA/versions/1/publish", new { reason = "Reviewed against HW-3" });

        // People: the reference plant's four keep their records; everyone else is registered, trained and certified.
        foreach (var w in spec.Workers)
        {
            var dept = spec.Department(w.Department);
            if (w.Existing)
            {
                var id = plant.People[w.Subject];
                string[] held = w.Subject switch { "sarah" => ["SLIT-II", "FIRST-AID"], "michael" => ["SLIT-II"], "jennifer" => ["PAINT-I", "FIRST-AID"], _ => [] };
                foreach (var q in w.Qualifications.Except(held))
                {
                    await plant.Ok(e, $"/api/competency/people/{id}/training", new { code = q });
                    await plant.Ok(approver, $"/api/competency/people/{id}/certifications", new { code = q, evidence = "Practical assessment on file", reason = "Assessment passed" });
                }
            }
            else
            {
                await plant.Worker(w.Subject, dept.WorkCenter, [.. w.Qualifications]);
            }

            var person = plant.People[w.Subject];
            await plant.Ok(e, $"/api/workforce/workers/{person}/shift", new { shiftCode = w.Shift });
            await plant.Ok(e, $"/api/workforce/workers/{person}/assignment", new { safetyAreaCode = dept.Area, activityCode = dept.Activity, reason = "Home department" });
            var badge = "B-" + w.Subject.ToUpperInvariant() + "-0001";
            await plant.Ok(plant.Admin, $"/api/workforce/people/{person}/badge", new { badge });
            fp.Badges[w.Subject] = badge;
            fp.Subjects[person] = w.Subject;
        }

        // Engineering: bought parts, then intermediates, then finished products; components are released before parents.
        foreach (var raw in spec.Raw.Where(r => r.Part != SimFactory.BulkPart))
        {
            await plant.Ok(e, "/api/engineering/items", new { partNumber = raw.Part, description = raw.Description, unitOfMeasure = raw.Uom, makeBuy = "Buy", leadTimeDays = raw.LeadTimeDays, lotTracked = raw.LotTracked });
            await plant.Ok(e, $"/api/engineering/items/{raw.Part}/revisions", new { revision = "A" });
            await plant.Ok(approver, $"/api/engineering/items/{raw.Part}/revisions/A/release", new { reason = "Factory part release" });
        }

        foreach (var made in spec.Intermediates.Concat(spec.Products))
        {
            var intermediate = spec.Intermediates.Contains(made);
            await plant.Ok(e, "/api/engineering/items", new { partNumber = made.Part, description = made.Description, unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = intermediate ? 2 : 5 });
            await plant.Ok(e, $"/api/engineering/items/{made.Part}/revisions", new { revision = "A" });
            foreach (var line in made.Bom)
            {
                await plant.Ok(e, $"/api/engineering/items/{made.Part}/revisions/A/bom", new { lineNumber = line.Line, componentPartNumber = line.Component, quantityPer = line.QuantityPer, scrapPercent = 0m });
            }

            foreach (var op in made.Routing)
            {
                var dept = spec.Department(op.Department);
                if (dept.MachineWork)
                {
                    await plant.Ok(e, $"/api/engineering/items/{made.Part}/revisions/A/routing", new
                    {
                        sequence = op.Sequence, description = dept.Activity, workCenterCode = dept.WorkCenter, activityCode = dept.Activity, eligibleMachines = dept.Machines.ToArray(),
                        setupMinutes = op.SetupMinutes, runMinutesPerUnit = op.RunMinutesPerUnit, crewSize = 1,
                    });
                }
                else
                {
                    await plant.Ok(e, $"/api/engineering/items/{made.Part}/revisions/A/routing", new
                    {
                        sequence = op.Sequence, description = dept.Activity, workCenterCode = dept.WorkCenter, activityCode = dept.Activity,
                        requiredQualifications = dept.Qualification is null ? Array.Empty<string>() : [dept.Qualification],
                        setupMinutes = op.SetupMinutes, runMinutesPerUnit = op.RunMinutesPerUnit, crewSize = 1,
                    });
                }
            }

            await plant.Ok(approver, $"/api/engineering/items/{made.Part}/revisions/A/release", new { reason = "Factory part release" });
        }

        // SHEET-STEEL-EQ is an approved equivalent; BOM lines that use sheet steel may substitute it automatically.
        var alternate = await plant.Ok(e, "/api/engineering/alternates", new { primaryPart = "SHEET-STEEL", alternatePart = "SHEET-STEEL-EQ", kind = "Equivalent", factor = 1m, evidence = "Same specification, mill certificate MC-EQ" });
        await plant.Ok(approver, $"/api/engineering/alternates/{alternate.GetProperty("entityId").GetString()}/approve", new { reason = "MC-EQ reviewed" });
        foreach (var made in spec.Intermediates.Concat(spec.Products))
        {
            foreach (var line in made.Bom.Where(l => l.Component == "SHEET-STEEL"))
            {
                await plant.Ok(approver, $"/api/engineering/items/{made.Part}/revisions/A/bom/{line.Line}/policy", new { policy = "Automatic", reason = "Equivalent mill qualified" });
            }
        }

        // Planning parameters: lead times and safety stock, lot for lot.
        foreach (var (part, safety) in spec.SafetyStock)
        {
            var lead = spec.Raw.FirstOrDefault(r => r.Part == part)?.LeadTimeDays ?? 2;
            await plant.Ok(e, $"/api/engineering/items/{part}/planning", new { leadTimeDays = lead, safetyStock = safety, lotPolicy = "LotForLot", orderQuantity = 0m, orderMultiple = 0m, reason = "Factory planning parameters" });
        }

        foreach (var s in spec.Suppliers)
        {
            await plant.Ok(fp.Buyer, "/api/procurement/suppliers", new { code = s.Code, name = s.Name });
        }

        // Stock: a finished-goods location, opening balances of bought parts and intermediates (the coil is already in stores).
        await plant.Ok(fp.Stores, "/api/inventory/locations", new { code = FactoryPlant.FinishedGoods, siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        foreach (var raw in spec.Raw.Where(r => r.Part != SimFactory.BulkPart && r.OpeningStock > 0))
        {
            await plant.Ok(fp.Stores, "/api/inventory/receipts", new { partNumber = raw.Part, quantity = raw.OpeningStock, locationCode = SimFactory.Stores, lotNumber = raw.LotTracked ? "OPEN-" + raw.Part : null, revision = "A", reason = "Opening balance" });
        }

        foreach (var (part, quantity) in spec.OpeningIntermediates.Where(kv => kv.Value > 0))
        {
            await plant.Ok(fp.Stores, "/api/inventory/receipts", new { partNumber = part, quantity, locationCode = SimFactory.Stores, revision = "A", reason = "Opening balance" });
        }

        // Quality: a calibrated test gauge (due a third of the way into the run; the quality engineer recalibrates it before then), and an
        // inspection plan at every finished product's test operation.
        await plant.Ok(fp.QualityEngineer, "/api/quality/gauges", new { code = FactoryPlant.Gauge, description = "Leak tester", calibrationDue = fp.Day(spec.GaugeDueDay), certificate = "CAL-TG-0" });
        foreach (var product in spec.Products)
        {
            var test = product.Routing.Single(o => o.Department == "TESTING").Sequence;
            await plant.Ok(fp.QualityEngineer, "/api/quality/plans", new
            {
                partNumber = product.Part, sequence = test, title = "Functional test",
                characteristics = new object[] { new { name = "Leak rate", kind = "Variable", nominal = 1.0m, lower = 0m, upper = 2.0m, unit = "ml/min" }, new { name = "Label present", kind = "Attribute" } },
            });
        }

        // Preventive maintenance: every machine outside Slitting and Paint, every 28 days on a Saturday, locked out when overdue.
        var saturdays = 0;
        foreach (var machine in spec.Departments.Where(d => d.Area is not ("SLITTING" or "PAINT")).SelectMany(d => d.Machines))
        {
            await plant.Ok(fp.MaintenancePlanner, "/api/maintenance/plans", new
            {
                machineCode = machine, title = "Preventive maintenance " + machine, intervalDays = 28, firstDue = fp.Day(5 + 7 * (saturdays++ % 4)), durationMinutes = 60,
                tasks = new[] { "Lock out and tag out", "Inspect and lubricate", "Check guards" }, lockOutWhenOverdue = true, graceDays = 2,
            });
        }

        fp.SetupWallTime = watch.Elapsed;
        return fp;
    }
}
