namespace AgentFleet.Tests;

// The worker sync decides which files it compares with the hub and which it may delete. A frozen test file is in neither set,
// so the hub copy stays the accepted one; these tests pin that decision without an SSH connection.
public sealed class WorkerWorkspaceFrozenSyncTests
{
    private static readonly PlanFrozenTestFile Frozen = new("tests/LimitTests.cs", "00");

    [Fact]
    public void A_sync_leaves_a_frozen_test_file_out_of_the_comparison_with_the_hub()
    {
        string[] compared = WorkerWorkspaceSession.FilesToCompareOnSync(
            ["tests/LimitTests.cs", "src/Limit.cs", "tests/Other.cs", "node_modules/x.js", ".env"], [Frozen], "linux").ToArray();

        Assert.Equal(["src/Limit.cs", "tests/Other.cs"], compared);
    }

    [Fact]
    public void A_windows_sync_leaves_a_frozen_test_file_out_under_another_spelling()
    {
        string[] compared = WorkerWorkspaceSession.FilesToCompareOnSync(
            ["TESTS/limittests.cs", "tests/LimitTests.cs.", "tests/LimitTests.cs::$DATA", "src/Limit.cs"], [Frozen], "windows").ToArray();

        Assert.Equal(["src/Limit.cs"], compared);
    }

    [Fact]
    public void A_frozen_test_file_missing_from_the_worker_is_never_a_deletion_candidate()
    {
        string[] candidates = WorkerWorkspaceSession.DeletionCandidates(
            ["tests/LimitTests.cs", "src/Limit.cs"], new HashSet<string>(StringComparer.OrdinalIgnoreCase), [Frozen], "linux", _ => true).ToArray();

        Assert.Equal(["src/Limit.cs"], candidates);
    }

    [Fact]
    public void A_deletion_candidate_must_be_named_by_the_step()
    {
        string[] candidates = WorkerWorkspaceSession.DeletionCandidates(
            ["src/Limit.cs", "src/Other.cs"], new HashSet<string>(StringComparer.OrdinalIgnoreCase), [Frozen], "linux", path => path == "src/Other.cs").ToArray();

        Assert.Equal(["src/Other.cs"], candidates);
    }
}
