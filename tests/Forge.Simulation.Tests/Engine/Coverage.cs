namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// The amendment's areas, from the coverage matrix in docs/v5/V5-simulation-discovery-and-architecture.md §2. A scenario
/// declares the areas it exercises through the real API with <see cref="SimReport.Covers"/>; the scorecard lists every
/// area with the scenarios that covered it in this run, and names the ones that were not covered or are not implemented
/// in Forge. An area is never reported as passing unless a scenario that covers it ran and passed.
/// </summary>
public static class Coverage
{
    public const string Demand = "Demand management";
    public const string Scheduling = "Planning and scheduling";
    public const string Mrp = "MRP";
    public const string Procurement = "Procurement and suppliers";
    public const string Receiving = "Receiving and inspection (partial, FAI)";
    public const string Holds = "Quality holds (scope)";
    public const string Mrb = "NCR and MRB dispositions";
    public const string Capa = "CAPA";
    public const string EngineeringChange = "Engineering changes and revisions";
    public const string Documents = "Document control";
    public const string Allocation = "Material allocation and approved alternates";
    public const string Execution = "Manufacturing and execution";
    public const string Workforce = "Workforce: shifts, callouts, PTO, transfers";
    public const string Training = "Training and qualification";
    public const string Safety = "Safety (two-person, lone work)";
    public const string Maintenance = "Maintenance (breakdowns, overdue PM)";
    public const string Calibration = "Tooling and calibration expiry";
    public const string Warehouse = "Warehouse (locations, counts, shelf life)";
    public const string Shipping = "Shipping";
    public const string Identity = "Identity, labels and scans";
    public const string Printing = "Printing (failed printing, as records)";
    public const string Reporting = "Reporting and KPIs";
    public const string Recovery = "Infrastructure and recovery";
    public const string Network = "Network interruptions";
    public const string Concurrency = "Concurrent transactions";
    public const string Concessions = "Customer concessions";

    /// <summary>Every area, in the matrix's order.</summary>
    public static readonly string[] Areas =
    [
        Demand, Scheduling, Mrp, Procurement, Receiving, Holds, Mrb, Capa, EngineeringChange, Documents, Allocation, Execution,
        Workforce, Training, Safety, Maintenance, Calibration, Warehouse, Shipping, Identity, Printing, Reporting, Recovery,
        Network, Concurrency, Concessions,
    ];

    /// <summary>
    /// The module each scenario is about (its I area, §2 legend). Every other area a scenario checks is reached as a
    /// consequence of that module's event, which makes the scenario an X scenario for those areas.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Subject = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["S01-operator-callout"] = Workforce,
        ["S02-pto-and-training"] = Training,
        ["S03-staffing-transfer"] = Workforce,
        ["S04-breakdown-and-overdue-pm"] = Maintenance,
        ["S05-calibration-expiry"] = Calibration,
        ["S06-supplier-delay"] = Procurement,
        ["S07-inaccurate-inventory"] = Warehouse,
        ["S08-receiving-with-fai"] = Receiving,
        ["S09-quality-hold-scope"] = Holds,
        ["S10-supplier-mrb"] = Mrb,
        ["S10b-split-dispositions"] = Mrb,
        ["S11-engineering-revision"] = EngineeringChange,
        ["S12-expedited-order"] = Scheduling,
        ["S13-document-error"] = Documents,
        ["S14-incorrect-scan-and-failed-print"] = Identity,
        ["S15-network-interruption"] = Network,
        ["S16-concurrent-transactions"] = Concurrency,
        ["S17-backup-and-recovery"] = Recovery,
        ["S18-production-execution"] = Execution,
        ["S19-lone-work-rule"] = Safety,
        ["S20-demand-change"] = Demand,
        ["S21-mrp-planned-purchase"] = Mrp,
        ["S22-customer-shipment"] = Shipping,
        ["S23-label-print-failures"] = Printing,
        ["S24-reports-match-records"] = Reporting,
        ["S25-corrective-action"] = Capa,
        ["S26-approved-alternate"] = Allocation,
        ["S27-customer-concession"] = Concessions,
    };

    /// <summary>What the amendment names that Forge does not have; reported as not implemented, never as passing.</summary>
    public static readonly (string Area, string What)[] NotImplemented =
    [
        (Allocation, "V5 allocation optimizer (excluded from V5 scope); only current allocation is testable"),
        (Workforce, "Overtime as its own concept; approximated through shifts and presence only"),
        (Scheduling, "90-day planning horizon; engines cap at 62 days (90-day readings are Phase C, by advancing the clock)"),
        (Printing, "Physical printing and device drivers; only recorded print attempts and observations are testable"),
        ("Economic consequences", "No costing module; consequences are reported in quantities, hours and late days (owner decision)"),
    ];
}
