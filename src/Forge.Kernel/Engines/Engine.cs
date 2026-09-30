using Forge.Kernel.Serialization;

namespace Forge.Kernel.Engines;

/// <summary>
/// The contract every Forge calculation engine implements (MRP, scheduling, readiness, composition,
/// executable capacity). An engine is a pure function: an immutable snapshot, parameters and an
/// as-of instant in; an output and its evidence out. No database, no clock, no randomness.
/// Scenarios are the same call on a snapshot with proposed changes overlaid.
/// </summary>
public interface IEngine<in TSnapshot, in TParameters, TOutput>
{
    /// <summary>Stable engine name, for example "readiness".</summary>
    string Name { get; }

    /// <summary>Bumped whenever a change could alter output for the same inputs.</summary>
    string Version { get; }

    EngineEvaluation<TOutput> Evaluate(TSnapshot snapshot, TParameters parameters, DateTimeOffset asOf);
}

/// <summary>What an engine returns: its answer and the evidence tree that justifies it.</summary>
public sealed record EngineEvaluation<TOutput>(TOutput Output, EvidenceNode Evidence);

/// <summary>
/// A recorded engine run. <see cref="InputFingerprint"/> hashes snapshot, parameters and as-of;
/// the same fingerprint and engine version must always produce the same <see cref="OutputFingerprint"/>.
/// </summary>
public sealed record EngineRun<TOutput>(
    string EngineName,
    string EngineVersion,
    DateTimeOffset AsOf,
    string InputFingerprint,
    string OutputFingerprint,
    TOutput Output,
    EvidenceNode Evidence);

public static class EngineRunner
{
    public static EngineRun<TOutput> Run<TSnapshot, TParameters, TOutput>(
        IEngine<TSnapshot, TParameters, TOutput> engine,
        TSnapshot snapshot,
        TParameters parameters,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(engine);
        asOf = asOf.ToUniversalTime();

        var inputFingerprint = CanonicalJson.Fingerprint(new
        {
            engine = engine.Name,
            version = engine.Version,
            asOf,
            parameters,
            snapshot,
        });

        var evaluation = engine.Evaluate(snapshot, parameters, asOf)
            ?? throw new InvalidOperationException($"Engine '{engine.Name}' returned no evaluation.");

        var outputFingerprint = CanonicalJson.Fingerprint(new { evaluation.Output, evaluation.Evidence });

        return new EngineRun<TOutput>(
            engine.Name,
            engine.Version,
            asOf,
            inputFingerprint,
            outputFingerprint,
            evaluation.Output,
            evaluation.Evidence);
    }
}
