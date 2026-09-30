namespace Forge.Kernel.Identity;

/// <summary>
/// Creates time-ordered UUIDv7 identifiers. The timestamp comes from the injected
/// <see cref="TimeProvider"/> so tests can control it; the random tail comes from the runtime.
/// Engines never create identifiers, so this randomness never reaches a calculation.
/// </summary>
public static class ForgeIds
{
    public static Guid New(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Guid.CreateVersion7(time.GetUtcNow());
    }
}

/// <summary>Stable identifier of a person known to Forge. Maps 1:1 to an identity-provider subject.</summary>
public readonly record struct PersonId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a program used for data segregation.</summary>
public readonly record struct ProgramId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
