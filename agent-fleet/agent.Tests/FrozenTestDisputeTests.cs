
namespace AgentFleet.Tests;

// The places a correction to a frozen test file reaches: the plan's record of its hash, the run log that gives the step its
// place on the repair ladder, and the retry rule for a suspect test.
public sealed class FrozenTestDisputeTests : PlanTestBase
{
    [Fact]
    public void Amending_a_frozen_file_returns_the_old_hash_and_records_the_new_one()
    {
        PlanRecord plan = NewPlan(Step("tests", verify: "dotnet test"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/A.cs", "OLD"), new PlanFrozenTestFile("tests/B.cs", "B")]));

        string? previous = Store.AmendFrozenFile(plan.Id, 1, "tests/A.cs", "NEW");

        Assert.Equal("OLD", previous);
        PlanFrozenTestSet set = Assert.Single(Store.Get(plan.Id)!.FrozenTests!);
        Assert.Equal(["tests/A.cs", "tests/B.cs"], set.Files.Select(file => file.Path));
        Assert.Equal(["NEW", "B"], set.Files.Select(file => file.Sha256));
    }

    [Fact]
    public async Task An_amendment_that_matches_nothing_does_not_save_the_plan()
    {
        PlanRecord plan = NewPlan(Step("tests", verify: "dotnet test"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/A.cs", "OLD")]));
        DateTimeOffset saved = Store.Get(plan.Id)!.UpdatedUtc;
        await Task.Delay(20);

        Assert.Null(Store.AmendFrozenFile(plan.Id, 2, "tests/A.cs", "NEW"));
        Assert.Null(Store.AmendFrozenFile(plan.Id, 1, "tests/Missing.cs", "NEW"));

        Assert.Equal(saved, Store.Get(plan.Id)!.UpdatedUtc);
    }

    [Fact]
    public void Amending_an_unknown_set_or_file_changes_nothing_and_returns_null()
    {
        PlanRecord plan = NewPlan(Step("tests", verify: "dotnet test"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/A.cs", "OLD")]));

        Assert.Null(Store.AmendFrozenFile(plan.Id, 2, "tests/A.cs", "NEW"));
        Assert.Null(Store.AmendFrozenFile(plan.Id, 1, "tests/Missing.cs", "NEW"));
        Assert.Null(Store.AmendFrozenFile("no-such-plan", 1, "tests/A.cs", "NEW"));
        Assert.Equal("OLD", Assert.Single(Assert.Single(Store.Get(plan.Id)!.FrozenTests!).Files).Sha256);
    }

    private static PlanRunEvent Round(int rung, int round, string signature) =>
        new(DateTimeOffset.UtcNow, 1, 1, RunEventKind.RoundClassified, "standard", "worker-a", "Round classified.", "worker-a", null,
            "CodeProgress", signature, 1, 1, 3, true, rung, round, null);

    private static PlanRunEvent Ruling(string verdict) =>
        new(DateTimeOffset.UtcNow, 1, 1, RunEventKind.TestRuling, "standard", "worker-b", "Ruling.", "worker-b", null, verdict);

    [Fact]
    public void A_test_wrong_ruling_gives_the_rung_its_rounds_back_once_per_ruling()
    {
        PlanRunEvent[] events =
        [
            Round(1, 1, "a\nb\nc"),
            Ruling("TestWrong"),
            Round(1, 1, "a\nb\nc")
        ];

        Assert.Equal(new RepairPosition(1, 1, 2), RepairLadder.PositionFrom(events));
        Assert.Equal(new RepairPosition(1, 1, 4), RepairLadder.PositionFrom([.. events, Round(1, 1, "a\nb\nc"), Ruling("TestWrong"), Round(1, 1, "a\nb\nc")]));
    }

    [Fact]
    public void Without_a_test_wrong_ruling_the_rounds_are_counted_as_before()
    {
        PlanRunEvent[] events = [Round(1, 1, "a\nb\nc"), Round(1, 1, "a\nb\nc")];

        Assert.Equal(new RepairPosition(1, 2, 2), RepairLadder.PositionFrom(events));
    }

    [Fact]
    public void A_code_wrong_ruling_does_not_give_back_any_rounds()
    {
        PlanRunEvent[] events = [Round(1, 1, "a\nb\nc"), Ruling("CodeWrong"), Round(1, 1, "a\nb\nc")];

        Assert.Equal(new RepairPosition(1, 2, 2), RepairLadder.PositionFrom(events));
    }

    [Fact]
    public void A_test_suspect_park_is_retryable_and_the_other_causes_are_unchanged()
    {
        Assert.True(PlanAutoRetry.Retryable("test-suspect"));
        Assert.True(PlanAutoRetry.Retryable("check-kept-failing"));
        Assert.False(PlanAutoRetry.Retryable("environment"));
        Assert.False(PlanAutoRetry.Retryable("unknown-failure"));
    }
}
