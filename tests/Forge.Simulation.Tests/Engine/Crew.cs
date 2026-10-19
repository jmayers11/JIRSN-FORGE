using System.Globalization;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// The simulation's own model of the reference plant's people, kept independently of Forge: who holds which
/// qualification, where each person works and who is away. From it, the oracle decides whether the SLIT-AREA safety
/// rule (two people in Slitting: an Operator holding SLIT-II and a Safety responder holding FIRST-AID or SLIT-II, no
/// trainees) and the machines' operator qualifications are satisfied. Forge's answers are compared with it.
/// Presence is evidence that expires: a supervisor's confirmation lasts eight hours (WorkforceService.DefaultPresence),
/// so the oracle tracks it, and scenarios re-confirm presence at shift changes as a supervisor would.
/// </summary>
internal sealed class Crew(DateTimeOffset confirmedAt)
{
    public static readonly TimeSpan PresenceLasts = TimeSpan.FromHours(8);

    private readonly Dictionary<string, HashSet<string>> qualifications = new(StringComparer.Ordinal)
    {
        ["sarah"] = new(StringComparer.Ordinal) { "SLIT-II", "FIRST-AID" },
        ["michael"] = new(StringComparer.Ordinal) { "SLIT-II" },
        ["jennifer"] = new(StringComparer.Ordinal) { "PAINT-I", "FIRST-AID" },
        ["dana"] = new(StringComparer.Ordinal),
    };

    private readonly Dictionary<string, string> area = new(StringComparer.Ordinal)
    {
        ["sarah"] = "SLITTING", ["michael"] = "SLITTING", ["jennifer"] = "PAINT", ["dana"] = "ASSEMBLY",
    };

    private readonly Dictionary<string, DateTimeOffset> presentUntil = new(StringComparer.Ordinal)
    {
        ["sarah"] = confirmedAt + PresenceLasts, ["michael"] = confirmedAt + PresenceLasts, ["jennifer"] = confirmedAt + PresenceLasts, ["dana"] = confirmedAt + PresenceLasts,
    };

    private readonly HashSet<string> away = new(StringComparer.Ordinal);

    /// <summary>The simulated time the oracle judges at.</summary>
    public DateTimeOffset Now { get; set; } = confirmedAt;

    /// <summary>A supervisor confirms the person is on site now; the evidence lasts eight hours.</summary>
    public void Confirm(string person, DateTimeOffset at) => presentUntil[person] = at + PresenceLasts;
    private readonly HashSet<string> trainees = new(StringComparer.Ordinal);

    public void Away(string person, bool isAway)
    {
        if (isAway)
        {
            away.Add(person);
        }
        else
        {
            away.Remove(person);
        }
    }

    public void MoveTo(string person, string safetyArea, bool asTrainee = false)
    {
        area[person] = safetyArea;
        if (asTrainee)
        {
            trainees.Add(person);
        }
        else
        {
            trainees.Remove(person);
        }
    }

    public void Suspend(string person, string qualification) => qualifications[person].Remove(qualification);

    public void Certify(string person, string qualification) => qualifications[person].Add(qualification);

    public bool Holds(string person, string qualification) => qualifications[person].Contains(qualification);

    private IEnumerable<string> PresentIn(string safetyArea) =>
        area.Where(p => p.Value == safetyArea && !away.Contains(p.Key) && !trainees.Contains(p.Key) && presentUntil[p.Key] > Now).Select(p => p.Key);

    /// <summary>A supervisor confirms presence of everyone who is not away, through the real API.</summary>
    public async Task ConfirmPresentAsync(SimFactory f, SimReport report, string step)
    {
        foreach (var person in SimFactory.Workers.Where(p => !away.Contains(p)))
        {
            var confirmed = await f.Api.Send(f.Plant.Engineer, $"/api/workforce/workers/{f.Plant.People[person]}/presence", new { kind = "SupervisorConfirmed", reason = "Shift handover" });
            report.Expect($"{step}: presence confirmed for {person}", "200", confirmed.Label);
            Confirm(person, f.Clock.GetUtcNow());
        }
    }

    /// <summary>SLIT-AREA: an Operator with SLIT-II and a different Safety responder with FIRST-AID or SLIT-II, both present in Slitting.</summary>
    public bool SlittingRunnable()
    {
        var people = PresentIn("SLITTING").ToList();
        return people.Any(op => Holds(op, "SLIT-II") && people.Any(r => r != op && (Holds(r, "FIRST-AID") || Holds(r, "SLIT-II"))));
    }

    /// <summary>
    /// PAINT-PAIR (scenario 19): two people present in Paint, an Operator holding PAINT-I and a different Safety
    /// responder holding FIRST-AID, no trainees.
    /// </summary>
    public bool PaintPairRunnable()
    {
        var people = PresentIn("PAINT").ToList();
        return people.Any(op => Holds(op, "PAINT-I") && people.Any(r => r != op && Holds(r, "FIRST-AID")));
    }

    /// <summary>The paint booth needs a present PAINT-I operator in Paint.</summary>
    public bool PaintRunnable() => PresentIn("PAINT").Any(p => Holds(p, "PAINT-I"));

    /// <summary>Forge's current answer for a machine activity: (executable, primary constraint).</summary>
    public static async Task<(bool Executable, string Constraint)> MachineAsync(SimFactory f, string machine, string activity)
    {
        var run = await SimApi.Get(f.Plant.Engineer, $"/api/readiness/machines/{machine}?activity={activity}");
        var output = run.GetProperty("output");
        var constraint = output.TryGetProperty("primaryConstraint", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "none";
        return (output.GetProperty("executable").GetBoolean(), constraint);
    }

    public static string Label((bool Executable, string Constraint) state) => state.Executable ? "executable" : "blocked: " + state.Constraint;

    public static object Window(DateTimeOffset from, DateTimeOffset to, string kind, string reason) =>
        new { kind, startsAt = from.ToString("o", CultureInfo.InvariantCulture), endsAt = to.ToString("o", CultureInfo.InvariantCulture), reason };
}
