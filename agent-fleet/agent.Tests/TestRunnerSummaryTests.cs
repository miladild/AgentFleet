namespace AgentFleet.Tests;

public sealed class TestRunnerSummaryTests
{
    [Theory]
    [InlineData("2 passed in 0.05s", 2, 0)]
    [InlineData("============================== 2 passed in 0.05s ==============================", 2, 0)]
    [InlineData("2 failed, 2 passed, 1 skipped in 0.08s", 5, 1)]
    [InlineData("=================== 2 failed, 2 passed, 1 skipped in 0.08s ====================", 5, 1)]
    [InlineData("============================ 1 error in 0.15s ============================", 0, 0)]
    [InlineData("============================ no tests ran in 0.02s ============================", 0, 0)]
    [InlineData("1 skipped, 4 deselected in 0.01s", 1, 1)]
    [InlineData("1 passed, 2 xfailed, 1 xpassed, 1 warning in 0.20s", 4, 0)]
    [InlineData("1 passed, 1 skipped in 1.23s (0:00:01)", 2, 1)]
    public void Pytest_final_line_counts_the_tests_that_ran_and_the_skipped_ones(string line, int total, int skipped)
    {
        TestRunnerTotals totals = TestRunnerSummary.Read("Exit code: 1\n" + line);

        Assert.True(totals.Found);
        Assert.Equal(total, totals.Total);
        Assert.Equal(skipped, totals.Skipped);
    }

    [Fact]
    public void Pytest_xfailed_tests_ran_so_they_count_and_deselected_tests_do_not()
    {
        TestRunnerTotals totals = TestRunnerSummary.Read("=== 1 xfailed, 2 deselected in 0.01s ===");

        Assert.Equal(1, totals.Total);
        Assert.Equal(0, totals.Skipped);
    }

    [Theory]
    [InlineData("Tests:       2 passed, 2 total", 2, 0)]
    [InlineData("Tests:       2 failed, 1 skipped, 2 passed, 5 total", 5, 1)]
    [InlineData("Tests:       1 todo, 1 passed, 2 total", 2, 1)]
    [InlineData("Tests:       0 total", 0, 0)]
    public void Jest_summary_line_counts_todo_as_skipped(string line, int total, int skipped)
    {
        TestRunnerTotals totals = TestRunnerSummary.Read("Test Suites: 1 failed, 1 total\n" + line + "\nSnapshots:   0 total");

        Assert.True(totals.Jest);
        Assert.Equal(total, totals.Total);
        Assert.Equal(skipped, totals.Skipped);
    }

    [Theory]
    [InlineData("      Tests  1 failed | 1 passed | 1 skipped (3)", 3, 1)]
    [InlineData("      Tests  1 passed (1)", 1, 0)]
    [InlineData("      Tests  1 passed | 1 todo (2)", 2, 1)]
    [InlineData("      Tests  no tests", 0, 0)]
    public void Vitest_summary_line_is_read(string line, int total, int skipped)
    {
        TestRunnerTotals totals = TestRunnerSummary.Read(" Test Files  1 failed (1)\n" + line);

        Assert.True(totals.Vitest);
        Assert.False(totals.Jest);
        Assert.Equal(total, totals.Total);
        Assert.Equal(skipped, totals.Skipped);
    }

    [Fact]
    public void Jest_and_vitest_no_test_files_messages_are_read_as_no_tests()
    {
        Assert.True(TestRunnerSummary.Read("No tests found, exiting with code 1\nRun with `--passWithNoTests`").NoTestFiles);
        Assert.True(TestRunnerSummary.Read("No test files found, exiting with code 1\n\nfilter: nomatch").NoTestFiles);
    }

    [Fact]
    public void Collection_markers_are_read_only_with_a_summary_present()
    {
        Assert.True(TestRunnerSummary.Read("ERROR collecting test_missing.py\n=== 2 passed, 1 error in 0.20s ===").CollectionFailed);
        Assert.True(TestRunnerSummary.Read("Interrupted: 1 error during collection\n=== 1 error in 0.15s ===").CollectionFailed);
        Assert.True(TestRunnerSummary.Read("  ● Test suite failed to run\nTests:       0 total").CollectionFailed);
        Assert.True(TestRunnerSummary.Read(" Test Files  1 failed (1)\n Failed Suites 1\n      Tests  no tests").CollectionFailed);
        Assert.False(TestRunnerSummary.Read("=== 2 passed, 1 error in 0.20s ===").CollectionFailed);
        Assert.False(TestRunnerSummary.Read("ERROR collecting test_missing.py").CollectionFailed);
    }

    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1")]
    [InlineData("ℹ tests 1\nℹ pass 1")]
    [InlineData("# tests 1\n# pass 1")]
    [InlineData("The test passed in 0.5 seconds and tests 3 things.")]
    [InlineData("Tests 1 passed in the notes")]
    public void Output_of_the_other_runners_and_prose_is_not_a_summary(string output)
    {
        TestRunnerTotals totals = TestRunnerSummary.Read(output);

        Assert.False(totals.Found);
        Assert.Equal(0, totals.Total);
        Assert.Equal(0, totals.Skipped);
    }
}
