using Forge.Quality.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>V3.11: FAI pending determination is never complete, and only a passing FAI completes a required one.</summary>
public class FaiStatusTests
{
    private const string Subject = "BRKT-9:A@ACME";

    private static Determination Decided(DeterminationState state) => new(1, DeterminationQuestion.Fai, Subject, state, "Basis", "person:qe", Now);

    private static FaiRecord Fai(bool passed) => new(5001, Subject, "BRKT-9", "A", "ACME", passed, [new ReceivingCharacteristicResult("Hole spacing", "50 ±0.1", "50.02", passed)], "FAI-BRKT-9", "A", "person:insp", Now);

    [Fact]
    public void Pending_determination_is_never_complete_and_never_allows_acceptance()
    {
        Assert.Equal(FaiStatus.PendingDetermination, Forge.Quality.Contracts.Fai.StatusOf("ACME", null, []));
        Assert.Equal(FaiStatus.PendingDetermination, Forge.Quality.Contracts.Fai.StatusOf("ACME", Decided(DeterminationState.PendingDetermination), [Fai(true)]));
        Assert.False(Forge.Quality.Contracts.Fai.AllowsAcceptance(FaiStatus.PendingDetermination));
        Assert.Contains("pending determination", Forge.Quality.Contracts.Fai.Explain(FaiStatus.PendingDetermination, Subject), StringComparison.Ordinal);
    }

    [Fact]
    public void Required_completes_only_with_a_passing_fai_and_not_required_or_not_applicable_allow_acceptance()
    {
        Assert.Equal(FaiStatus.Required, Forge.Quality.Contracts.Fai.StatusOf("ACME", Decided(DeterminationState.Required), []));
        Assert.Equal(FaiStatus.Required, Forge.Quality.Contracts.Fai.StatusOf("ACME", Decided(DeterminationState.Required), [Fai(false)]));
        Assert.Equal(FaiStatus.Complete, Forge.Quality.Contracts.Fai.StatusOf("ACME", Decided(DeterminationState.Required), [Fai(true), Fai(false)]));
        Assert.Equal(FaiStatus.NotRequired, Forge.Quality.Contracts.Fai.StatusOf("ACME", Decided(DeterminationState.NotRequired), []));
        Assert.Equal(FaiStatus.NotApplicable, Forge.Quality.Contracts.Fai.StatusOf(null, null, []));
        Assert.True(Forge.Quality.Contracts.Fai.AllowsAcceptance(FaiStatus.Complete));
        Assert.True(Forge.Quality.Contracts.Fai.AllowsAcceptance(FaiStatus.NotRequired));
        Assert.False(Forge.Quality.Contracts.Fai.AllowsAcceptance(FaiStatus.Required));
        Assert.Equal("BRKT-9:A@ACME", Forge.Quality.Contracts.Fai.Subject("BRKT-9", "A", "ACME"));
    }
}

