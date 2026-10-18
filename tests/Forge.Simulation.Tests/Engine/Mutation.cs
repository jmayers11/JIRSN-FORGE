namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// A deliberate break used by the negative controls. A mutated run must produce failed expectations; if it does not,
/// the checks it targets cannot be trusted. <see cref="None"/> is every real run.
/// </summary>
public enum Mutation
{
    None,

    /// <summary>S09: the hold is placed on the other lot, while the oracle still expects the held lot to be stopped.</summary>
    HoldWrongLot,

    /// <summary>S10: the oracle counts one unit too many as released after the first inspection.</summary>
    OracleOffByOne,

    /// <summary>S10: the deciding engineer approves their own use-as-is instead of the quality manager.</summary>
    SelfApproveUseAsIs,

    /// <summary>S15: a client retries a lost receipt with a fresh key instead of the original one.</summary>
    RetryWithFreshKey,

    /// <summary>S11: the oracle releases the part a return was reduced by, instead of keeping it on hold pending a decision.</summary>
    ReleaseReducedPart,

    /// <summary>S01: the callout is recorded for Michael while the oracle expects Sarah (who also holds FIRST-AID) to be away.</summary>
    CallOutWrongPerson,

    /// <summary>S07: the person who counted approves their own count (refused), while the oracle expects it posted.</summary>
    SelfApproveCount,
}
