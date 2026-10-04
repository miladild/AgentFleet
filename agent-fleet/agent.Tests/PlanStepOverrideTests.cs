using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanStepOverrideTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nerror";

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

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify, FleetOptions? options = null,
        FleetHealthMonitor? health = null, int roundsPerRung = 1, IPlanNotifier? notifier = null) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health,
            roundsPerRung: roundsPerRung, transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, notifier: notifier);

    private FleetOptions OptionsWithNodes(params FleetNodeConfig[] nodes)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "fleet.config.json")
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        configStore.Save(configStore.Current with { Nodes = nodes });
        return FleetOptions.Load(configuration, configStore);
    }

    private sealed class HealthyNodeFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HealthyNodeHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class HealthyNodeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Method == HttpMethod.Post
                ? "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
                : "{\"models\":[{\"name\":\"m:latest\"}]}";
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private (FleetOptions Options, FleetHealthMonitor Health) HubFleet()
    {
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);
        FleetNodeConfig workerA = new("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard");
        FleetOptions options = OptionsWithNodes(hub, workerA);
        return (options, new FleetHealthMonitor(options, new HealthyNodeFactory()));
    }

    [Fact]
    public void A_plan_file_can_give_a_step_its_own_retries_rescue_and_review()
    {
        string file = Path.Combine(ProjectDirectory, "PLAN.md");
        string content = $"""
# Test Plan

Working directory: `{ProjectDirectory}`

## Step 1: With overrides

- Tier: standard
- Retries: 0
- Rescue: worker-only
- Review: off
- Files: `a.txt`
- Check: check

Do step 1.

## Step 2: Without overrides

- Tier: standard
- Files: `b.txt`
- Check: check

Do step 2.
""";
        File.WriteAllText(file, content);

        PlanFileContent plan = PlanFile.Read(file);

        Assert.Equal(2, plan.Steps.Count);

        PlanStepInput first = plan.Steps[0];
        Assert.Equal(0, first.Retries);
        Assert.Equal("worker-only", first.Rescue);
        Assert.Equal("off", first.Review);

        PlanStepInput second = plan.Steps[1];
        Assert.Null(second.Retries);
        Assert.Null(second.Rescue);
        Assert.Null(second.Review);

        // Invalid retries becomes null
        string fileInvalid = Path.Combine(ProjectDirectory, "PLAN2.md");
        string contentInvalid = """
# Test Plan

## Step 1: Invalid retries

- Tier: standard
- Retries: lots
- Files: `a.txt`
- Check: check

Do it.
""";
        File.WriteAllText(fileInvalid, contentInvalid);

        PlanFileContent planInvalid = PlanFile.Read(fileInvalid);
        Assert.Null(planInvalid.Steps[0].Retries);
    }

    [Fact]
    public void A_step_keeps_its_own_choices_clamped_and_normalised()
    {
        string file = Path.Combine(ProjectDirectory, "a.txt");
        File.WriteAllText(file, "content");

        PlanRecord plan = Store.Create("Test", "goal", ProjectDirectory, ["a"], ["q"], ["r"], null, null,
            [
                new PlanStepInput("step1", "detail", ["a.txt"], "check", "standard",
                    Retries: 9, Rescue: "WORKER-ONLY", Review: "Off"),
                new PlanStepInput("step2", "detail", ["a.txt"], "check", "standard",
                    Retries: -3, Rescue: "banana", Review: "maybe")
            ], PlanRecoveryScope.WorkerOnly);

        PlanStep step1 = plan.Steps[0];
        Assert.Equal(5, step1.AutoRetries);  // Clamped from 9
        Assert.Equal("worker-only", step1.Rescue);  // Normalized from "WORKER-ONLY"
        Assert.Equal("off", step1.Review);  // Normalized from "Off"

        PlanStep step2 = plan.Steps[1];
        Assert.Equal(0, step2.AutoRetries);  // Clamped from -3
        Assert.Null(step2.Rescue);  // Invalid "banana" becomes null
        Assert.Null(step2.Review);  // Invalid "maybe" becomes null
    }

    [Fact]
    public void The_plan_markdown_lists_a_steps_own_choices()
    {
        string file = Path.Combine(ProjectDirectory, "a.txt");
        File.WriteAllText(file, "content");

        PlanRecord planWithChoices = Store.Create("Test", "goal", ProjectDirectory, ["a"], ["q"], ["r"], null, null,
            [new PlanStepInput("step1", "detail", ["a.txt"], "check", "standard",
                Retries: 2, Rescue: "allow-hub-rescue", Review: "off")
            ], PlanRecoveryScope.AllowHubRescue);

        string markdown = FleetPlanStore.ToMarkdown(planWithChoices);
        Assert.Contains("- Retries: 2", markdown);
        Assert.Contains("- Rescue: allow-hub-rescue", markdown);
        Assert.Contains("- Review: off", markdown);

        // Plan without step-level choices should not have these lines
        PlanRecord planWithoutChoices = Store.Create("Test2", "goal", ProjectDirectory, ["a"], ["q"], ["r"], null, null,
            [new PlanStepInput("step1", "detail", ["a.txt"], "check", "standard")
            ], PlanRecoveryScope.AllowHubRescue);

        string markdownWithout = FleetPlanStore.ToMarkdown(planWithoutChoices);
        // Check that the step's section doesn't contain these three lines (they might be elsewhere like headers)
        string[] lines = markdownWithout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int stepStart = Array.FindIndex(lines, l => l.Contains("1. step1"));
        int stepEnd = stepStart + 1;
        while (stepEnd < lines.Length && !lines[stepEnd].StartsWith("- ["))
        {
            stepEnd++;
        }
        string stepSection = string.Join("\n", lines[stepStart..stepEnd]);
        Assert.DoesNotContain("- Retries:", stepSection);
        Assert.DoesNotContain("- Rescue:", stepSection);
        Assert.DoesNotContain("- Review:", stepSection);
    }

    [Theory]
    [InlineData("light", 3, "standard")]
    [InlineData("light", 5, "standard")]
    [InlineData("light", 2, "light")]
    [InlineData("light", 0, "light")]
    [InlineData("standard", 5, "standard")]
    [InlineData("heavy", 4, "heavy")]
    public void A_light_step_that_names_more_than_two_files_is_raised_to_standard(string tier, int fileCount, string expectedTier)
    {
        string project = ProjectDirectory;
        var files = Enumerable.Range(1, fileCount).Select(i => $"file{i}.txt").ToList();

        foreach (string file in files)
        {
            File.WriteAllText(Path.Combine(project, file), "content");
        }

        PlanRecord plan = Store.Create("Test", "goal", project, ["a"], ["q"], ["r"], null, null,
            [new PlanStepInput("step", "detail", files.ToArray(), "check", tier)
            ], PlanRecoveryScope.WorkerOnly);

        PlanStep step = plan.Steps[0];
        Assert.Equal(expectedTier, step.Tier);

        if (tier == "light" && fileCount > 2)
        {
            Assert.NotNull(step.Note);
            Assert.StartsWith("Raised from the light tier to standard", step.Note);
        }
        else
        {
            Assert.Null(step.Note);
        }
    }

    [Fact]
    public async Task A_step_with_zero_retries_is_parked_at_once_in_a_plan_that_retries()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("fix", "detail", ["data.txt"], "check", "standard", Retries: 0);
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.WorkerOnly);
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 2)!;

        var agent = new FakeStepAgent((_, _, _) =>
        {
            return Task.FromResult("Working...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ => Fail("check"), notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Should have no StepRetried events with FailureClass "AutoRetry"
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == FleetPlanStore.AutoRetryClass));

        // Should have StepParked event with FailureSignature "check-kept-failing"
        PlanRunEvent[] parked = after.Events!.Where(e => e.Kind == RunEventKind.StepParked).ToArray();
        Assert.Single(parked);
        Assert.Equal("check-kept-failing", parked[0].FailureSignature);
    }

    [Fact]
    public async Task A_step_with_its_own_retries_is_retried_in_a_plan_that_does_not()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("fix", "detail", ["data.txt"], "check", "standard", Retries: 1);
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.WorkerOnly);
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0)!;

        int modelCall = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            if (modelCall == 3)
            {
                File.WriteAllText(filePath, "fixed");
                return Task.FromResult("Fixed it.");
            }
            return Task.FromResult("Still working...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ =>
            Path.GetFileName(filePath) == "data.txt" && File.ReadAllText(filePath) == "fixed" ? Pass("check") : Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // Should have exactly one StepRetried event with FailureClass "AutoRetry"
        PlanRunEvent[] retried = after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == FleetPlanStore.AutoRetryClass).ToArray();
        Assert.Single(retried);
        Assert.Contains("retry 1 of 1", retried[0].Detail);
    }

    [Fact]
    public async Task A_step_set_to_worker_only_never_calls_the_hub_in_a_plan_that_allows_it()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        string filePath = Path.Combine(ProjectDirectory, "test.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("work", "detail", ["test.txt"], "check", "standard", Rescue: "worker-only");
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.AllowHubRescue);
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out _)!;
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0)!;

        var agent = new FakeStepAgent((_, _, _) =>
        {
            return Task.FromResult("Tried but not editing.");
        }, toolCalls: 2, editToolCalled: false);

        await RepairRunner(agent, _ => Fail("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;

        // No call should have Machine "hub"
        Assert.Empty(agent.Calls.Where(c => c.Machine == "hub"));

        // Should have no RungChanged event (step never went to hub rung 2)
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.RungChanged && e.Rung == 2));
    }

    [Fact]
    public async Task A_step_set_to_allow_hub_rescue_calls_the_hub_in_a_worker_only_plan()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        string filePath = Path.Combine(ProjectDirectory, "test.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("work", "detail", ["test.txt"], "check", "standard", Rescue: "allow-hub-rescue");
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.WorkerOnly);
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out _)!;
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0)!;

        int callCount = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            callCount++;
            if (callCount >= 3)  // After worker fails on rung 1, hub is called on rung 2
            {
                File.WriteAllText(filePath, "fixed");
                return Task.FromResult("Fixed by hub.");
            }
            return Task.FromResult("Trying...");
        });

        await RepairRunner(agent, _ =>
            Path.GetFileName(filePath) == "test.txt" && File.ReadAllText(filePath) == "fixed" ? Pass("check") : Fail("check"),
            options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // Should have at least one call to the hub
        Assert.NotEmpty(agent.Calls.Where(c => c.Machine == "hub"));
    }

    [Fact]
    public async Task A_step_with_review_off_is_not_reviewed_in_a_plan_that_reviews()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        string filePath = Path.Combine(ProjectDirectory, "test.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("work", "detail", ["test.txt"], "check", "standard", Review: "off");
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.AllowHubRescue);
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out _)!;
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto)!;

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(filePath, "done");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("PASS\nLooks good.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // No audit calls for step review
        Assert.Empty(agent.Audits.Where(a => a.Role == PlanRunnerToolPolicy.StepReviewRole));
    }

    [Fact]
    public async Task A_step_with_review_auto_is_reviewed_in_a_plan_that_does_not()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        string filePath = Path.Combine(ProjectDirectory, "test.txt");
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("work", "detail", ["test.txt"], "check", "standard", Review: "auto");
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [step], PlanRecoveryScope.AllowHubRescue);
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out _)!;
        plan = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Off)!;

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(filePath, "done");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("PASS\nLooks good.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // Should have exactly one audit call for step review
        FakeAuditCall[] audits = agent.Audits.Where(a => a.Role == PlanRunnerToolPolicy.StepReviewRole).ToArray();
        Assert.Single(audits);
        Assert.Equal("step-review", audits[0].Role);
    }
}
