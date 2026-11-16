namespace AgentFleet.Tests;

/// <summary>
/// The files a test run leaves in the project folder are removed right after the run: the ones the run created (in the listing after
/// it and not before), except the folders and kinds of file that are never the run's to delete, and the files the plan names. Any
/// doubt, such as a listing that is missing or a path that leaves the folder, removes nothing and leaves the old rule in place.
/// </summary>
public sealed class TestRunLeftoversTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-leftovers-" + Guid.NewGuid().ToString("N"));

    public TestRunLeftoversTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllText(Full(relative), "made by the run");
    }

    [Fact]
    public void A_test_run_that_wrote_116_random_named_data_files_has_all_of_them_removed()
    {
        string[] before = ["src/app.ts"];
        string[] created = Enumerable.Range(0, 116).Select(_ => $"data/{Guid.NewGuid():N}.json").ToArray();
        foreach (string file in created) Write(file);

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, before, [.. before, .. created], []);

        Assert.False(plan.Fallback);
        Assert.Null(plan.Reason);
        Assert.Equal(created.Order(StringComparer.Ordinal), plan.Remove.Order(StringComparer.Ordinal));
        IReadOnlyList<string> removed = TestRunLeftovers.Remove(_root, plan.Remove, before);
        Assert.Equal(116, removed.Count);
        Assert.All(created, file => Assert.False(File.Exists(Full(file))));
        Assert.False(Directory.Exists(Full("data")), "the folder the run made is empty now and goes too");
    }

    [Fact]
    public void A_file_that_was_there_before_the_run_is_kept()
    {
        Write("keep.json");
        Write("new.json");

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, ["keep.json"], ["keep.json", "new.json"], []);

        Assert.False(plan.Fallback);
        Assert.Equal(["new.json"], plan.Remove);
        Assert.Empty(TestRunLeftovers.Remove(_root, ["keep.json"], ["keep.json"]));
        Assert.True(File.Exists(Full("keep.json")));
    }

    [Theory]
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData(".git/x")]
    [InlineData(".agent-fleet/run.json")]
    [InlineData("reports/build.log")]
    [InlineData("tsconfig.tsbuildinfo")]
    public void Dependency_folders_version_control_and_incidental_files_are_kept(string relative)
    {
        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], [relative], []);

        Assert.False(plan.Fallback);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void A_file_the_plan_names_for_the_step_is_kept_and_so_is_a_file_in_a_folder_it_names()
    {
        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], ["out/report.json", "out/nested/extra.json", "scratch.json"],
            [Full("out/report.json"), Full("out")]);

        Assert.False(plan.Fallback);
        Assert.Equal(["scratch.json"], plan.Remove);
    }

    [Fact]
    public void More_than_500_files_to_remove_is_a_fallback_that_removes_nothing()
    {
        string[] created = Enumerable.Range(0, 501).Select(index => $"data/{index}.json").ToArray();

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], created, []);

        Assert.True(plan.Fallback);
        Assert.Empty(plan.Remove);
        Assert.Contains("500", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_500_files_to_remove_is_still_removed()
    {
        string[] created = Enumerable.Range(0, 500).Select(index => $"data/{index}.json").ToArray();

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], created, []);

        Assert.False(plan.Fallback);
        Assert.Equal(500, plan.Remove.Count);
    }

    [Fact]
    public void A_listing_that_is_missing_after_the_run_is_a_fallback()
    {
        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], null, []);

        Assert.True(plan.Fallback);
        Assert.Empty(plan.Remove);
        Assert.NotNull(plan.Reason);
    }

    [Fact]
    public void A_listing_that_is_missing_before_the_run_is_a_fallback_too_because_every_file_would_look_new()
    {
        Write("mine.txt");

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, null, ["mine.txt"], []);

        Assert.True(plan.Fallback);
        Assert.Empty(plan.Remove);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("data/../../outside.json")]
    [InlineData("/outside/x.json")]
    [InlineData("\\\\server\\share\\x.json")]
    [InlineData("C:/outside/x.json")]
    [InlineData("data/x.json:stream")]
    public void A_path_that_is_absolute_or_leaves_the_project_is_a_fallback_that_removes_nothing(string relative)
    {
        Write("ok.json");

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, [], ["ok.json", relative], []);

        Assert.True(plan.Fallback);
        Assert.Empty(plan.Remove);
        Assert.NotNull(plan.Reason);
        Assert.True(File.Exists(Full("ok.json")));
    }

    [Fact]
    public void A_folder_the_run_left_empty_is_removed_but_a_folder_that_held_a_file_before_the_run_is_not()
    {
        Write("keep/a.json");
        Write("keep/b.json");
        Write("data/new/x.json");

        LeftoverPlan plan = TestRunLeftovers.Plan(_root, ["keep/a.json"], ["keep/a.json", "keep/b.json", "data/new/x.json"], []);
        IReadOnlyList<string> removed = TestRunLeftovers.Remove(_root, plan.Remove, ["keep/a.json"]);

        Assert.Equal(["data/new/x.json", "keep/b.json"], removed.Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Full("data")));
        Assert.True(File.Exists(Full("keep/a.json")));
        Assert.True(Directory.Exists(Full("keep")));
    }

    [Fact]
    public void A_file_already_gone_is_skipped_without_an_error_and_a_folder_with_contents_is_never_removed()
    {
        Write("here.json");
        Write("folder/inner.json");

        IReadOnlyList<string> removed = TestRunLeftovers.Remove(_root, ["gone.json", "here.json", "folder"], []);

        Assert.Equal(["here.json"], removed);
        Assert.True(File.Exists(Full("folder/inner.json")));
    }
}
