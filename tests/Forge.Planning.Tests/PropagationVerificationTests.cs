using Forge.Engineering.Contracts;

namespace Forge.Planning.Tests;

public sealed class PropagationVerificationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static PropagationStepInfo Step(string key, PropagationStepKind kind, PropagationStepStatus status) =>
        new("ECN-0001", key, kind, key, "P", "detail", status, 1, status == PropagationStepStatus.Failed ? "boom" : null, At, status == PropagationStepStatus.Done ? At : null);

    private static ChangeActionInfo Action(long id, ChangeActionStatus status) =>
        new(id, "ECN-0001", ChangeActionKind.WipDisposition, id.ToString(System.Globalization.CultureInfo.InvariantCulture), "P", null, null, null, null, "d", status, null, null, "system:propagation", At, null, null);

    [Fact]
    public void Expected_and_actual_are_counted_per_kind_and_every_unfinished_step_is_unresolved()
    {
        var propagation = new ChangePropagationInfo("ECN-0001", PropagationStatus.Incomplete, Guid.Empty, 2, At, At, null, "boom",
        [
            Step("a", PropagationStepKind.MoveOrder, PropagationStepStatus.Done),
            Step("b", PropagationStepKind.MoveOrder, PropagationStepStatus.Failed),
            Step("c", PropagationStepKind.ReissueDocumentation, PropagationStepStatus.Waiting),
            Step("d", PropagationStepKind.WipDisposition, PropagationStepStatus.Done),
        ]);

        var v = PropagationVerification.Of(propagation, [Action(1, ChangeActionStatus.Open), Action(2, ChangeActionStatus.Resolved)]);
        Assert.Equal(new VerificationLine(PropagationStepKind.MoveOrder, 2, 1, 0, 1), v.Lines[0]);
        Assert.Equal(new VerificationLine(PropagationStepKind.ReissueDocumentation, 1, 0, 1, 0), v.Lines[1]);
        Assert.Equal(3, v.Lines.Count);
        Assert.Equal(2, v.Unresolved);
        Assert.Equal((2, 1), (v.ActionsRaised, v.ActionsOpen));
        Assert.Contains("2 of 4", v.Summary, StringComparison.Ordinal);
        Assert.Contains("boom", v.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_propagation_says_how_many_actions_are_still_open()
    {
        var propagation = new ChangePropagationInfo("ECN-0001", PropagationStatus.Complete, Guid.Empty, 1, At, At, At, null, [Step("a", PropagationStepKind.CompletedUnchanged, PropagationStepStatus.Done)]);
        var v = PropagationVerification.Of(propagation, []);
        Assert.Equal(0, v.Unresolved);
        Assert.StartsWith("Propagation complete: 1 step(s) verified; 0 action(s) raised, 0 still open.", v.Summary, StringComparison.Ordinal);
    }
}
