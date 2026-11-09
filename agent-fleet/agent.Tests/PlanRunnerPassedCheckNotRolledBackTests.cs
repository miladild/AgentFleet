using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// The repair loop's best state and its undo. A round whose own check passed is never undone because of an extra round it is
/// given (the whole-project check, or debugging output it left in its files): the extra round's message is not a check output,
/// so that round is neither compared with the best round nor kept as the best one. A round whose own check failed is still
/// undone when it leaves the step worse than the best round. The project folder is real and git is faked, as in the other repair tests.
/// </summary>
public sealed class PlanRunnerPassedCheckNotRolledBackTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private const string FinalCheck = "final";
    private const string FinalCheckRoundKind = "final-check-round";
    private const string DebugOutputRoundKind = "debug-output";
    private const string DebugLine = "console.log(\"DEBUG\", SyntaxError);\n";

    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private string SourceFile => Path.Combine(ProjectDirectory, "a.cs");

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string FailWith(params string[] failing) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join("\n", failing.Select(name => "FAILED " + name));

    private static string ReadSource(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    // Git as the runner sees it, over the real project folder: every file in it is "changed or new".
    private PlanTools RepairTools(Func<string, string> verify)
    {
        string project = ProjectDirectory;
        return new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(command switch
        {
            "git rev-parse --show-toplevel" => $"Exit code: 0\n--- stdout ---\n{project}",
            "git rev-parse HEAD" => "Exit code: 0\n--- stdout ---\nabc123",
            _ when command.Contains("--untracked-files=all", StringComparison.Ordinal) =>
                "Exit code: 0\n--- stdout ---\n" + string.Join("\n",
                    Directory.GetFiles(project, "*", SearchOption.AllDirectories)
                        .Select(file => "?? " + Path.GetRelativePath(project, file).Replace('\\', '/'))),
            _ when command.StartsWith("git ", StringComparison.Ordinal) => "Exit code: 0\n--- stdout ---\n",
            _ => verify(command)
        }));
    }

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, roundsPerRung: RepairLadder.DefaultRoundsPerRung,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);

    private PlanRecord Plan(params PlanStepInput[] steps) =>
        Store.Approve(Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null, steps).Id)!;

    private static PlanStepInput Listed(string title, string? verify, params string[] files) => new(title, "detail", files, verify, "standard");

    [Fact]
    public async Task A_round_whose_check_passed_is_not_undone_when_the_whole_project_check_then_fails()
    {
        // Round 1 leaves two failing: the best round so far. Round 2 makes the step's own check pass, but the whole-project check
        // fails with three. Round 2 is given its extra round, and the file it fixed stays as it is for round 3.
        PlanRecord plan = Plan(Listed("edit a", "check1", "a.cs"), Listed("last", FinalCheck, "b.cs"));
        string? seenAtRoundThree = null;
        int rounds = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("This step (2 of 2)", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "b.cs"), "new b");
                return Task.FromResult("Done.");
            }

            // The model's file tools run before the check: what it finds on disk at the start of round 3 is round 2's file.
            if (++rounds == 3) seenAtRoundThree = ReadSource(SourceFile);
            File.WriteAllText(SourceFile, rounds switch { 1 => "one", 2 => "fixed", _ => "settled" });
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => command switch
        {
            "check1" => ReadSource(SourceFile) == "one" ? FailWith("t1", "t2") : Pass(command),
            FinalCheck => ReadSource(SourceFile) == "fixed" ? FailWith("t3", "t4", "t5") : Pass(command),
            _ => Pass(command)
        }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("fixed", seenAtRoundThree);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        PlanRunEvent extra = Assert.Single(after.Events!, runEvent => runEvent.Kind == FinalCheckRoundKind);
        Assert.Equal(2, extra.Round);
        Assert.Equal(4, agent.Calls.Count);
    }

    [Fact]
    public async Task A_round_whose_check_passed_is_not_kept_as_the_best_state_that_a_later_failing_round_is_undone_to()
    {
        // Round 1 passes its own check, but the whole-project check fails with two. That round is given its extra round and is not
        // a best state, so round 2, which fails its own check with three, has nothing to be compared with and stays as it is.
        PlanRecord plan = Plan(Listed("edit a", "check1", "a.cs"), Listed("last", FinalCheck, "b.cs"));
        string? seenAtRoundThree = null;
        int rounds = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("This step (2 of 2)", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "b.cs"), "new b");
                return Task.FromResult("Done.");
            }

            if (++rounds == 3) seenAtRoundThree = ReadSource(SourceFile);
            File.WriteAllText(SourceFile, rounds switch { 1 => "one", 2 => "two", _ => "fixed" });
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => command switch
        {
            "check1" => ReadSource(SourceFile) == "two" ? FailWith("t1", "t2", "t5") : Pass(command),
            FinalCheck => ReadSource(SourceFile) == "one" ? FailWith("t3", "t4") : Pass(command),
            _ => Pass(command)
        }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("two", seenAtRoundThree);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        PlanRunEvent extra = Assert.Single(after.Events!, runEvent => runEvent.Kind == FinalCheckRoundKind);
        Assert.Equal(1, extra.Round);
    }

    [Fact]
    public async Task A_round_whose_check_passed_is_not_undone_for_the_debugging_output_it_left_behind()
    {
        // Round 1 leaves two failing. Round 2 passes its check but leaves a debugging print whose text names a syntax error: the
        // extra round it is given is not a check output, so round 2 stays, and round 3 (which removes the print) is checked on it.
        PlanRecord plan = Plan(Listed("edit a", "check1", "a.cs"));
        string? seenAtRoundThree = null;
        int rounds = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            if (++rounds == 3) seenAtRoundThree = ReadSource(SourceFile);
            File.WriteAllText(SourceFile, rounds switch { 1 => "one", 2 => DebugLine, _ => "fixed" });
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => ReadSource(SourceFile) == "one" ? FailWith("t1", "t2") : Pass(command))
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(DebugLine, seenAtRoundThree);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        PlanRunEvent debug = Assert.Single(after.Events!, runEvent => runEvent.Kind == DebugOutputRoundKind);
        Assert.Equal(2, debug.Round);
    }

    [Fact]
    public async Task A_failing_round_that_leaves_more_failures_than_the_best_round_is_still_undone()
    {
        PlanRecord plan = Plan(Listed("edit a", "check1", "a.cs"));
        string? seenAtRoundThree = null;
        int rounds = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            if (++rounds == 3) seenAtRoundThree = ReadSource(SourceFile);
            File.WriteAllText(SourceFile, rounds switch { 1 => "one", 2 => "broke it", _ => "fixed" });
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => ReadSource(SourceFile) switch
        {
            "one" => FailWith("t1", "t2"),
            "broke it" => FailWith("t1", "t2", "t3", "t4"),
            _ => Pass(command)
        }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("one", seenAtRoundThree);
        PlanRunEvent undone = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        Assert.Equal("RolledBack", undone.FailureClass);
        Assert.Equal(2, undone.Round);
        Assert.Contains("4 failing where round 1 had 2 failing, so it was undone", undone.Detail, StringComparison.Ordinal);
    }
}
