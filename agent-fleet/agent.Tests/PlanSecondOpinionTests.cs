using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanSecondOpinionTests : PlanTestBase
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

    private PlanRecord CreateOneStepPlan(string fileName, string scope)
    {
        string filePath = Path.Combine(ProjectDirectory, fileName);
        File.WriteAllText(filePath, "initial");

        var step = new PlanStepInput("review me", $"Write to {fileName}", [fileName], "check", "standard");
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null, [step], scope);
        return Store.SelectMachine(plan.Id, 1, "worker-a", out _)!;
    }

    [Fact]
    public async Task A_step_whose_check_passes_is_read_by_the_hub_and_accepted_on_PASS()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);

        int call = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), $"version {++call}");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("PASS\nThe rule and its test agree.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);

        // Exactly one audit call
        FakeAuditCall[] audits = agent.Audits.Where(a => a.Role == PlanRunnerToolPolicy.StepReviewRole).ToArray();
        Assert.Single(audits);
        FakeAuditCall audit = audits[0];
        Assert.Equal("step-review", audit.Role);
        Assert.Equal("hub", audit.Machine);
        Assert.Equal("heavy", audit.Tier);
        Assert.Contains("review me", audit.Message);
        Assert.Contains("FAIL", audit.Message);
        Assert.Contains("version 1", audit.Message);

        // StepReview event with FailureClass "Passed"
        PlanRunEvent[] reviews = after.Events!.Where(e => e.Kind == RunEventKind.StepReview && e.FailureClass == "Passed").ToArray();
        Assert.Single(reviews);
        Assert.Contains("Reviewed by hub", reviews[0].Detail);
    }

    [Fact]
    public async Task A_FAIL_goes_back_to_the_model_as_a_failed_round_and_the_next_pass_is_accepted()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);

        int call = 0;

        // Each review is a conversation of its own, so the answer is chosen by how many reviews there have been.
        int reviews = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), $"version {++call}");
            return Task.FromResult("Worked on it.");
        })
        {
            Auditor = _ => ++reviews == 1
                ? new StepAgentReply("FAIL\n1. src.cs: the July 3 rule is wrong", 0, false)
                : new StepAgentReply("PASS\nNow the rule and its test agree.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Audits.Count(a => a.Role == PlanRunnerToolPolicy.StepReviewRole));

        // The step's model is told, in the same conversation, why its passing check was not enough.
        FakeStepSession session = Assert.Single(agent.Sessions);
        Assert.Equal(2, session.Messages.Count);
        Assert.Contains("Not accepted: a reviewer", session.Messages[1].Text);
        Assert.Contains("the July 3 rule is wrong", session.Messages[1].Text);

        Assert.Equal(["Failed", "Passed"],
            after.Events!.Where(e => e.Kind == RunEventKind.StepReview).Select(e => e.FailureClass).ToArray());
    }

    [Fact]
    public async Task A_reviewer_that_keeps_rejecting_parks_the_step_with_cause_review()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version X");
            return Task.FromResult("Tried.");
        })
        {
            Auditor = _ => new StepAgentReply("FAIL\nStill not right.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // StepParked event with FailureSignature "review"
        PlanRunEvent? parked = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepParked);
        Assert.NotNull(parked);
        Assert.Equal("review", parked.FailureSignature);
    }

    [Fact]
    public async Task Approval_without_a_review_choice_defaults_off_and_does_not_ask_a_reviewer()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        PlanRecord approved = Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0)!;
        Assert.Equal(PlanSecondOpinion.Off, approved.Review);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Empty(agent.Audits);
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.StepReview));
    }

    [Fact]
    public async Task A_saved_plan_with_no_review_setting_does_not_ask_a_reviewer()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);
        Store.Update(plan.Id, saved => saved with { Review = null });

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("PASS\nLooks good.", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Empty(agent.Audits.Where(a => a.Role == PlanRunnerToolPolicy.StepReviewRole));
    }

    [Fact]
    public async Task A_worker_only_plan_is_reviewed_by_another_worker_never_by_the_hub()
    {
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);
        FleetNodeConfig workerA = new("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard");
        FleetNodeConfig workerB = new("worker-b", "http://worker-b.example.test/v1", "m", "worker", "light");
        FleetOptions options = OptionsWithNodes(hub, workerA, workerB);
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());

        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.WorkerOnly);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0, review: PlanSecondOpinion.Auto);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("PASS", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // The reviewer is worker-b, not the hub
        FakeAuditCall? audit = agent.Audits.FirstOrDefault(a => a.Role == PlanRunnerToolPolicy.StepReviewRole);
        Assert.NotNull(audit);
        Assert.Equal("worker-b", audit.Machine);
        Assert.NotEqual("hub", audit.Machine);
    }

    [Fact]
    public async Task With_no_other_machine_the_step_is_accepted_on_its_check_and_the_log_says_why()
    {
        FleetOptions options = OptionsWithNodes(
            new FleetNodeConfig("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
            new FleetNodeConfig("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard"));
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());

        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.WorkerOnly);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0, review: PlanSecondOpinion.Auto);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Empty(agent.Audits);

        // StepReview event with FailureClass "Skipped"
        PlanRunEvent? review = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepReview && e.FailureClass == "Skipped");
        Assert.NotNull(review);
    }

    [Fact]
    public async Task A_reviewer_that_cannot_be_reached_never_blocks_the_step()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => throw new HttpRequestException("down")
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // StepReview event with FailureClass "Unavailable"
        PlanRunEvent? review = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepReview && e.FailureClass == "Unavailable");
        Assert.NotNull(review);
    }

    [Fact]
    public async Task A_reviewer_that_gives_no_verdict_never_blocks_the_step()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = CreateOneStepPlan("test.txt", PlanRecoveryScope.AllowHubRescue);
        Store.Approve(plan.Id, recoveryScope: PlanRecoveryScope.AllowHubRescue, autoRetries: 0, review: PlanSecondOpinion.Auto);

        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "test.txt"), "version 1");
            return Task.FromResult("Done.");
        })
        {
            Auditor = _ => new StepAgentReply("I think it is fine", 0, false)
        };

        await RepairRunner(agent, _ => Pass("check"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);

        // StepReview event with FailureClass "NoVerdict"
        PlanRunEvent? review = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepReview && e.FailureClass == "NoVerdict");
        Assert.NotNull(review);
    }

    [Theory]
    [InlineData("PASS", "Pass")]
    [InlineData("PASS: it matches", "Pass")]
    [InlineData("FAIL\n1. a is wrong", "Fail")]
    [InlineData("**FAIL**\n1. x", "Fail")]
    [InlineData("Let me look at it.\nFAIL\n1. y", "Fail")]
    [InlineData("Passing tests are not a verdict", "None")]
    [InlineData("The code fails the example", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    public void The_verdict_is_read_from_the_first_line_that_starts_with_PASS_or_FAIL(string? input, string expectedKind)
    {
        ReviewVerdict verdict = StepReview.Parse(input);
        Assert.Equal(expectedKind, verdict.Kind.ToString());
    }

    [Fact]
    public void The_reviewer_can_only_read()
    {
        // Can offer read tools
        Assert.True(PlanRunnerToolPolicy.Offers("read_file", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.True(PlanRunnerToolPolicy.Offers("list_directory", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.True(PlanRunnerToolPolicy.Offers("find_files", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.True(PlanRunnerToolPolicy.Offers("search_files", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.True(PlanRunnerToolPolicy.Offers("project_overview", true, PlanRunnerToolPolicy.StepReviewRole));

        // Cannot offer write tools
        Assert.False(PlanRunnerToolPolicy.Offers("write_file", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.False(PlanRunnerToolPolicy.Offers("edit_file", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.False(PlanRunnerToolPolicy.Offers("run_command", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.False(PlanRunnerToolPolicy.Offers("run_git_command", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.False(PlanRunnerToolPolicy.Offers("report_blocker", true, PlanRunnerToolPolicy.StepReviewRole));
        Assert.False(PlanRunnerToolPolicy.Offers("propose_check", true, PlanRunnerToolPolicy.StepReviewRole));

        // Refusal messages
        Assert.Null(PlanRunnerToolPolicy.Refusal("read_file", null, PlanRunnerToolPolicy.StepReviewRole));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("run_command", "dir", PlanRunnerToolPolicy.StepReviewRole));
    }
}
