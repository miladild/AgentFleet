using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

// Runner-level tests of the requirement list: the tests step's prompt, and the coverage check on the last tests step.
public sealed partial class PlanRunnerTests
{
    private const string LimitTestsCoveringBoth = "public class LimitTests\n{\n    [Fact] public void R1_rejects_zero() { }\n    [Fact] public void R2_rejects_negatives() { }\n}\n";

    private static PlanStepInput LimitTestsStep(string file = "tests/LimitTests.cs") =>
        new("Write limit tests", "Reject zero and negative input", [file], "dotnet test", "standard", Kind: "tests");

    private static List<PlanRequirement> PlanRequirementsOf(params string[] texts) =>
        texts.Select((text, index) => new PlanRequirement("R" + (index + 1), text, [])).ToList();

    // The model's rounds in order: round n writes texts[n], and the last text is kept for any later round.
    private static FakeStepAgent WritesTestRounds(string project, string relative, params string[] texts)
    {
        int round = 0;
        return new FakeStepAgent((_, _, _) =>
        {
            string path = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, texts[Math.Min(round++, texts.Length - 1)]);
            return Task.FromResult("Done.");
        });
    }

    private static IReadOnlyList<string> AcceptedOutcomes(PlanRecord plan) =>
        plan.Events!.Where(runEvent => runEvent.Kind == RunEventKind.TestFirst).Select(runEvent => OutcomeOf(runEvent)!).ToArray();

    [Fact]
    public async Task Test_first_The_requirement_list_and_the_naming_rule_reach_the_tests_step_prompt()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero") });
            var fake = WritesTestRounds(project, "tests/LimitTests.cs", LimitTestsCoveringBoth);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            string prompt = Assert.Single(fake.Calls).Prompt;
            Assert.Contains(PlanTestsStepPrompt.Addendum, prompt);
            Assert.Contains("R1: Rejects zero", prompt);
            Assert.Contains("start with the ids of the requirements it covers", prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_A_last_tests_step_covering_every_requirement_is_accepted_and_the_names_are_stored()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            var fake = WritesTestRounds(project, "tests/LimitTests.cs", LimitTestsCoveringBoth);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["accepted"], AcceptedOutcomes(after));
            Assert.Equal(["R1_rejects_zero"], after.Requirements![0].CoveredBy);
            Assert.Equal(["R2_rejects_negatives"], after.Requirements![1].CoveredBy);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_An_uncovered_requirement_refuses_the_step_and_is_named_by_id_and_text_in_the_feedback()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            var fake = WritesTestRounds(project, "tests/LimitTests.cs",
                "public class LimitTests\n{\n    [Fact] public void R1_rejects_zero() { }\n}\n", LimitTestsCoveringBoth);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["uncovered", "accepted"], AcceptedOutcomes(after));
            Assert.Contains("R2 (Rejects negatives)", fake.Calls[1].Prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_Only_the_last_tests_step_is_held_to_full_coverage()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep("tests/A.cs"), LimitTestsStep("tests/B.cs"));
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            int call = 0;
            var fake = new FakeStepAgent((_, _, _) =>
            {
                call++;
                string relative = call == 1 ? "tests/A.cs" : "tests/B.cs";
                string text = call == 1 ? "[Fact] public void R1_rejects_zero() { }\n" : "[Fact] public void R2_rejects_negatives() { }\n";
                Directory.CreateDirectory(Path.Combine(project, "tests"));
                File.WriteAllText(Path.Combine(project, relative), text);
                return Task.FromResult("Done.");
            });
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["accepted", "accepted"], AcceptedOutcomes(after));
            Assert.Equal(["R1_rejects_zero"], after.Requirements![0].CoveredBy);
            Assert.Equal(["R2_rejects_negatives"], after.Requirements![1].CoveredBy);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    // An earlier tests step covers only part of the list and must still be accepted; the last one covers the rest.
    // The writer picks the file by what exists, so each step's own test file is written in its own round.
    [Fact]
    public async Task Test_first_An_earlier_tests_step_is_accepted_with_part_of_the_list_and_the_last_completes_it()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep("tests/A.cs"), LimitTestsStep("tests/B.cs"));
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            var fake = new FakeStepAgent((_, _, _) =>
            {
                bool firstWritten = File.Exists(Path.Combine(project, "tests", "A.cs"));
                string relative = firstWritten ? "tests/B.cs" : "tests/A.cs";
                string text = firstWritten ? "[Fact] public void R2_rejects_negatives() { }\n" : "[Fact] public void R1_rejects_zero() { }\n";
                Directory.CreateDirectory(Path.Combine(project, "tests"));
                File.WriteAllText(Path.Combine(project, relative), text);
                return Task.FromResult("Done.");
            });
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, fake.Calls.Count);
            Assert.Equal(["accepted", "accepted"], AcceptedOutcomes(after));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    // The covering names are read back through a new store instance over the same folder, so only persisted data counts.
    [Fact]
    public async Task Test_first_The_covering_names_are_persisted_in_order_and_read_back_from_a_new_store()
    {
        string project = NewGitProject();
        try
        {
            var step = new PlanStepInput("Write limit tests", "Reject zero and negative input", ["tests/LimitTests.cs", "tests/notes.rb"],
                "dotnet test", "standard", Kind: "tests");
            var setup = WorkerPlan(project, step);
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            var fake = new FakeStepAgent((_, _, _) =>
            {
                Directory.CreateDirectory(Path.Combine(project, "tests"));
                File.WriteAllText(Path.Combine(project, "tests", "LimitTests.cs"), LimitTestsCoveringBoth);
                File.WriteAllText(Path.Combine(project, "tests", "notes.rb"), "# R1 is also noted here\n");
                return Task.FromResult("Done.");
            });
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);

            var reopened = new FleetPlanStore(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = PlansDirectory })
                .Build());
            PlanRecord persisted = reopened.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, persisted.Status);
            Assert.Equal(["R1_rejects_zero", "tests/notes.rb (text match)"], persisted.Requirements![0].CoveredBy);
            Assert.Equal(["R2_rejects_negatives"], persisted.Requirements![1].CoveredBy);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    // A retry of a tests step is sent with the requirement list and the naming rule, not only the first round.
    [Fact]
    public async Task Test_first_The_retry_message_of_a_tests_step_carries_the_requirement_list_and_the_naming_rule()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            var fake = WritesTestRounds(project, "tests/LimitTests.cs",
                "public class LimitTests\n{\n    [Fact] public void R1_rejects_zero() { }\n}\n", LimitTestsCoveringBoth);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            Assert.Equal(["uncovered", "accepted"], AcceptedOutcomes(Store.Get(setup.Plan.Id)!));
            string retry = fake.Calls[1].Prompt;
            Assert.Contains("- R2: Rejects negatives", retry);
            Assert.Contains("start with the ids of the requirements it covers", retry);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    // A test the step wrote into a file it did not declare counts toward coverage, when its name carries the id.
    [Fact]
    public async Task Test_first_Tests_written_into_an_undeclared_test_file_count_toward_the_last_step_coverage()
    {
        string project = NewGitProject();
        try
        {
            var step = new PlanStepInput("Write limit tests", "Reject zero", ["tests/a.test.mjs"], "node --test", "standard", Kind: "tests");
            var setup = WorkerPlan(project, step);
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero") });
            var fake = new FakeStepAgent((_, _, _) =>
            {
                Directory.CreateDirectory(Path.Combine(project, "tests"));
                File.WriteAllText(Path.Combine(project, "tests", "b.test.mjs"), "test(\"R1: rejects zero\", () => {});\n");
                return Task.FromResult("Done.");
            });
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["accepted"], AcceptedOutcomes(after));
            Assert.Equal(["R1: rejects zero"], after.Requirements![0].CoveredBy);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_Off_does_not_check_requirement_coverage()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero") });
            var fake = WritesTestsOnly(project);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsPassOutput, enabled: false).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestFirst);
            Assert.All(after.Requirements!, requirement => Assert.Empty(requirement.CoveredBy));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
