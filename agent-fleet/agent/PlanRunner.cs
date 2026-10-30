using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;

namespace AgentFleet;

/// <summary>Runs one focused piece of work with a model of the requested tier.</summary>
internal interface IStepAgent
{
    Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken);

    Task<StepAgentReply> RunStepAsync(string prompt, string tier, string? machine, CancellationToken cancellationToken) =>
        RunStepAsync(prompt, tier, cancellationToken);

    /// <summary>
    /// A conversation with a model that the runner keeps sending messages into: the check output of a failed round goes
    /// back into the same conversation, so the model still has the task and everything it did.
    /// </summary>
    IStepSession OpenSession();

    /// <summary>
    /// A conversation for one job that is not carrying out a step: <see cref="PlanRunnerToolPolicy.CheckAuditRole"/> is
    /// a model asked whether a step's check is broken, with read-only tools and propose_check and nothing else. Doubles
    /// without a notion of roles open an ordinary conversation.
    /// </summary>
    IStepSession OpenSession(string? role) => OpenSession();
}

/// <summary>
/// One conversation. The tier and machine are given per message, so a conversation can continue on another machine
/// (measured: the history, tool calls included, is understood by the second model, in both directions).
/// </summary>
internal interface IStepSession
{
    /// <summary>
    /// True once a message has been answered. A message that failed or timed out left nothing in the conversation, so
    /// until then the next message has to be the whole task, not a follow-up.
    /// </summary>
    bool HasHistory { get; }

    void ConfigurePrompt(PlanStepPromptData? data) { }

    /// <summary>Whether the next message may offer dispute_test (an ordinary step while frozen tests are in play). Off by default.</summary>
    void OfferDispute(bool offered) { }

    /// <summary>Where the next message's tool calls are counted, so a timed-out attempt can still say what it did. Doubles ignore it.</summary>
    void TrackActivity(StepToolActivity activity) { }

    Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken);
}

/// <summary>A tool the model called during a turn, with its arguments as text.</summary>
internal sealed record AgentToolCall(string Name, IReadOnlyDictionary<string, string?> Arguments);

internal sealed record StepAgentReply(string Text, int ToolCalls, bool EditToolCalled, IReadOnlyList<AgentToolCall>? Calls = null, int LoopGuardNotes = 0);

/// <summary>
/// The tool calls one attempt has made so far, counted as they run. A reply is the only other place these come from, and an
/// attempt that times out has no reply, so the runner reads this instead.
/// </summary>
internal sealed class StepToolActivity
{
    public const string Key = "fleet.runner.activity";

    public int ToolCalls { get; private set; }

    public bool EditToolCalled { get; private set; }

    /// <summary>Told of each tool call as the model makes it, in order. The runner uses it to see that a model call is still working.</summary>
    public Action<string>? Observed { get; set; }

    public void Record(string toolName)
    {
        ToolCalls++;
        EditToolCalled |= IsEdit(toolName);
        Observed?.Invoke(toolName);
    }

    public static bool IsEdit(string toolName) => toolName is "write_file" or "edit_file" or "move_file" or "delete_file";
}

/// <summary>
/// Drives the real agent. Each conversation is its own session, so a step never inherits another step's
/// conversation. The requested tier goes through the router (see FleetRoutingChatClient.RunnerTierKey);
/// routing mode and an explicit machine choice determine which configured machine answers.
/// </summary>
internal sealed class FleetStepAgent(AIAgent agent) : IStepAgent
{
    internal const string Nudge =
        "Your last turn only inspected files or gave no usable result. Finish this step now: make the change it asks for with your tools. The fleet runs the approved check afterwards, and a step that names files to change is not done until the work has changed at least one of them. " + PlanRunner.ActFirst;

    public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken) =>
        RunStepAsync(prompt, tier, null, cancellationToken);

    public Task<StepAgentReply> RunStepAsync(string prompt, string tier, string? machine, CancellationToken cancellationToken) =>
        OpenSession().SendAsync(prompt, tier, machine, cancellationToken);

    public IStepSession OpenSession() => new FleetStepSession(agent);

    public IStepSession OpenSession(string? role) => new FleetStepSession(agent, role);
}

internal sealed class FleetStepSession(AIAgent agent, string? role = null) : IStepSession
{
    private readonly AIAgent _agent = agent.AsBuilder().UseAIContextProviders(
        new PlanEnvironmentContextProvider(), new PlanFilesContextProvider(), new PlanContinuationContextProvider()).Build();
    private AgentSession? _session;
    private PlanStepPromptData? _promptData;
    private bool _offerDispute;

    public void ConfigurePrompt(PlanStepPromptData? data) => _promptData = data;

    public void OfferDispute(bool offered) => _offerDispute = offered;

    private StepToolActivity? _activity;

    public void TrackActivity(StepToolActivity activity) => _activity = activity;

    public bool HasHistory { get; private set; }

    public async Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken)
    {
        PlanStepPromptData? data = _promptData;
        _promptData = null;
        _session ??= await _agent.CreateSessionAsync(cancellationToken);
        var properties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = tier };
        if (_activity is not null)
        {
            properties[StepToolActivity.Key] = _activity;
        }
        if (!string.IsNullOrWhiteSpace(machine))
        {
            properties[FleetRoutingChatClient.RunnerMachineKey] = machine;
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            properties[FleetRoutingChatClient.RunnerRoleKey] = role;
        }

        if (_offerDispute && string.IsNullOrWhiteSpace(role))
        {
            properties[FleetRoutingChatClient.RunnerDisputeKey] = true;
        }

        var options = new ChatClientAgentRunOptions(new ChatOptions
        {
            AdditionalProperties = properties
        });
        AgentResponse response;
        try
        {
            if (data is not null)
            {
                _session.StateBag.SetValue(PlanStepPromptProvider.StateKey, data);
            }
            response = await _agent.RunAsync(message, _session, options, cancellationToken);
        }
        finally
        {
            _session.StateBag.TryRemoveValue(PlanStepPromptProvider.StateKey);
        }
        HasHistory = true;

        // Measured: a worker spent 75 s on one reply and ended with no tool call and no text (a thinking model that
        // only thought), which cost the step an attempt and sent it to the hub's queue. Asked once more in the same
        // session, it gets on with it.
        FunctionCallContent[] calls = response.Messages.SelectMany(reply => reply.Contents)
            .OfType<FunctionCallContent>()
            .ToArray();
        int toolCalls = calls.Length;
        bool editToolCalled = calls.Any(IsEditTool);
        bool readOnlyOnly = calls.Length > 0 && calls.All(call => call.Name is
            "read_file" or "list_directory" or "find_files" or "search_files" or "project_overview" or "get_plan" or "search_context");
        var seen = calls.ToList();
        // A reader role has no change to make, and an answer in words is a valid result.
        // A turn that wrote text and called no tool at all (an analysis of what is wrong, ending where the work should start) is
        // the same: measured, a small worker wrote the right diagnosis of its failing tests and stopped there, round after round.
        if (role is null or PlanRunnerToolPolicy.TestWriterRole && (calls.Length == 0 || readOnlyOnly))
        {
            response = await _agent.RunAsync(FleetStepAgent.Nudge, _session, options, cancellationToken);
            FunctionCallContent[] nudgedCalls = response.Messages.SelectMany(reply => reply.Contents)
                .OfType<FunctionCallContent>()
                .ToArray();
            toolCalls += nudgedCalls.Length;
            editToolCalled |= nudgedCalls.Any(IsEditTool);
            seen.AddRange(nudgedCalls);
        }

        return new StepAgentReply(response.Text, toolCalls, editToolCalled, seen.Select(AsToolCall).ToList(), CountLoopGuardNotes(response.Messages));
    }

    internal static int CountLoopGuardNotes(IEnumerable<ChatMessage> messages) => messages
        .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
        .Count(result => result.Result?.ToString()?.Contains(ToolLoopGuard.Marker, StringComparison.Ordinal) == true);

    private static AgentToolCall AsToolCall(FunctionCallContent call) => new(
        call.Name,
        (call.Arguments ?? new Dictionary<string, object?>()).ToDictionary(
            argument => argument.Key,
            argument => argument.Value switch
            {
                null => null,
                string text => text,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
                System.Text.Json.JsonElement element => element.GetRawText(),
                var other => other.ToString()
            },
            StringComparer.Ordinal));

    private static bool IsEditTool(FunctionCallContent call) => StepToolActivity.IsEdit(call.Name);
}

/// <summary>
/// Executes an approved plan without a chat open, one step at a time. Measured with the fleet's
/// own models: when the plan was handed to the model to carry out, the standard worker wrote the
/// files but never reported the step done, drifted from the plan (Jest instead of the node:test
/// the plan named), and gave up with advice. So the loop is code, not the model's discretion:
///
///  - the model gets one step and only that step, with the files, the plan's goal, what earlier
///    steps did, and how its work will be checked;
///  - when the model stops, the runner itself runs the step's verify command;
///  - a failure goes back to the model with the real output at the step's original tier;
///  - everything is persisted after every step, so a restart resumes where it left off, and the
///    plan card shows live progress for whoever looks in the morning.
/// One plan runs at a time. Within an approved plan, explicitly grouped independent steps may share the
/// machines concurrently when their files and ready routing tiers are disjoint.
/// </summary>
internal sealed partial class PlanRunner
{
    public const int MaxAttemptsPerStep = 3;

    private const int MaxStepContextFiles = 8;
    private const int MaxStepContextBytesPerFile = 32 * 1024;
    private const int MaxStepContextCharacters = 48_000;

    /// <summary>
    /// The longest one model attempt (one round: its tool calls, not the check) may run. It is not what decides that a step is
    /// stuck, the ladder is: a slow machine, 11 tokens a second, needs 20 minutes for a round that writes a test file.
    /// </summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The longest a model call may stay silent, with no tool call, before its round is counted as a failed round. The silence
    /// starts again at each tool call, so a round that keeps working is never cut short by it; the attempt timeout still bounds
    /// the round as a whole. Thirty minutes matches the attempt timeout: a worker that writes a large file in one reply is quiet
    /// for a long time, and a shorter silence would stop work that is going well.
    /// </summary>
    public static readonly TimeSpan DefaultStallLimit = TimeSpan.FromMinutes(30);

    /// <summary>After a silent call is asked to stop, how long it has to return before the runner stops waiting for it.</summary>
    public static readonly TimeSpan DefaultStallGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Run-log kinds that record an interrupted worker round's tool calls, so that a restart can tell whether the round could have
    /// changed anything besides the worker's files: the round's mark is followed by no changing call only if it made none.
    /// </summary>
    internal const string ToolLoggedEvent = "tool-logged";
    internal const string ChangingToolEvent = "changing-tool-called";

    // The tools that only read. Any other tool call, however small, counts as one that may have changed something.
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "read_file", "list_directory", "find_files", "search_files", "project_overview", "get_plan", "search_context"
    };

    /// <summary>The silence after which a model call counts as stalled. Tests shorten it; production uses <see cref="DefaultStallLimit"/>.</summary>
    internal TimeSpan StallLimit { get; set; } = DefaultStallLimit;

    /// <summary>How long a silent call gets to return once it is asked to stop. Tests shorten it.</summary>
    internal TimeSpan StallGrace { get; set; } = DefaultStallGrace;

    /// <summary>
    /// The most a step may work (its model calls and its checks, not the time it waits for a machine) before it stops with
    /// everything it tried. This is a backstop and not the rule: what stops a step that is not getting anywhere is the repair
    /// ladder (rounds that change nothing or do not improve climb a rung, and the last rung parks), and a step that is getting
    /// closer is never stopped for how long it has been at it. It used to be 45 minutes, which cut off slow machines while
    /// their code was partly right. A retry gives the step a fresh clock.
    /// </summary>
    public static readonly TimeSpan DefaultStepClock = TimeSpan.FromHours(4);
    private static readonly char[] LineBreaks = ['\r', '\n'];
    private static readonly TimeSpan[] ReconnectPauses =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15)
    ];

    private readonly FleetPlanStore _store;
    private readonly PlanTools _tools;
    private readonly IStepAgent _agent;
    private readonly ILogger _logger;
    private readonly TimeSpan _attemptTimeout;
    private readonly FleetOptions? _fleetOptions;
    private readonly IWorkerWorkspaceManager? _workerWorkspaces;
    private readonly FleetModeService? _modeService;
    private readonly FleetHealthMonitor? _healthMonitor;
    private readonly PlanContextRecorder? _recorder;
    private readonly FleetContextJournal? _journal;
    private readonly ConcurrentDictionary<string, string> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<int, string>> _lastFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _filesToCreate = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _filesAtStart = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PlanTools.WorkSnapshot> _failFirstSnapshots = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _stopRequested = new(StringComparer.Ordinal);
    private readonly Func<int, TimeSpan> _transientDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ISleepGuard _sleepGuard;
    private readonly int _roundsPerRung;
    private readonly TimeSpan _stepClock;
    private readonly IPlanNotifier? _notifier;
    private readonly bool _stepGuidance;
    private readonly FitProbeService? _fitProbes;
    private readonly FailFirstMode _failFirstMode;
    private readonly TestFirstMode _testFirstMode;
    private readonly ConcurrentDictionary<string, bool> _testFirstSnapshotAttempts = new(StringComparer.Ordinal);
    private readonly Func<string?>? _testWriterNode;
    private readonly EditCheckSettings _editCheck;

    public PlanRunner(
        FleetPlanStore store,
        PlanTools tools,
        IStepAgent agent,
        ILogger logger,
        TimeSpan? attemptTimeout = null,
        FleetOptions? fleetOptions = null,
        FleetHealthMonitor? healthMonitor = null,
        PlanContextRecorder? recorder = null,
        FleetContextJournal? journal = null,
        Func<int, TimeSpan>? transientDelay = null,
        ISleepGuard? sleepGuard = null,
        IWorkerWorkspaceManager? workerWorkspaces = null,
        FleetModeService? modeService = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        int roundsPerRung = RepairLadder.DefaultRoundsPerRung,
        TimeSpan? stepClock = null,
        IPlanNotifier? notifier = null,
        bool stepGuidance = false,
        FitProbeService? fitProbes = null,
        FailFirstMode failFirst = FailFirstMode.Off,
        TestFirstMode testFirst = TestFirstMode.Off,
        Func<string?>? testWriterNode = null,
        EditCheckSettings? editCheck = null)
    {
        _notifier = notifier;
        _stepGuidance = stepGuidance;
        _fitProbes = fitProbes;
        _failFirstMode = failFirst;
        _testFirstMode = testFirst;
        _testWriterNode = testWriterNode;
        _editCheck = editCheck ?? new EditCheckSettings();
        _roundsPerRung = Math.Max(1, roundsPerRung);
        _stepClock = stepClock is { } clock && clock > TimeSpan.Zero ? clock : DefaultStepClock;
        _transientDelay = transientDelay ?? DefaultTransientDelay;
        _delayAsync = delayAsync ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sleepGuard = sleepGuard ?? NoSleepGuard.Instance;
        _recorder = recorder;
        _journal = journal;
        _store = store;
        _tools = tools;
        _agent = agent;
        _logger = logger;
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
        _fleetOptions = fleetOptions;
        _workerWorkspaces = workerWorkspaces;
        _modeService = modeService;
        _healthMonitor = healthMonitor;
    }

    public void Enqueue(string planId) => _queue.Writer.TryWrite(planId);

    // Starts the worker and picks up anything that was in flight: a plan approved or running when
    // the backend last stopped (a reboot overnight, say) carries on instead of being forgotten.
    public void Start(CancellationToken stopping)
    {
        foreach (PlanSummary summary in _store.List())
        {
            if (summary.Status is PlanStatus.Approved or PlanStatus.Running)
            {
                _logger.LogInformation("Resuming plan {PlanId} ({Title}) after a restart.", summary.Id, summary.Title);
                Enqueue(summary.Id);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (string planId in _queue.Reader.ReadAllAsync(stopping))
                {
                    try
                    {
                        await RunPlanAsync(planId, stopping);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogError(exception, "Plan {PlanId} stopped unexpectedly.", planId);
                        PlanRecord? failed = _store.Update(planId, plan => plan.Status is PlanStatus.Done
                            ? plan
                            : plan with
                            {
                                Status = PlanStatus.Blocked,
                                Steps = plan.Steps.Select(step => step.Status == StepStatus.Running
                                    ? step with { Status = StepStatus.Failed, Note = "The runner stopped unexpectedly; inspect the run log and worker workspace before retrying." }
                                    : step).ToList()
                            });
                        if (failed is { Status: PlanStatus.Blocked })
                        {
                            string reason = $"Runner stopped unexpectedly ({exception.GetType().Name}): {exception.Message}";
                            int? stepId = failed.Steps.FirstOrDefault(step => step.Status == StepStatus.Running)?.Id;
                            stepId ??= failed.Steps.FirstOrDefault(step => step.Status == StepStatus.Failed)?.Id;
                            _store.AddEvent(planId, stepId, null, RunEventKind.PlanBlocked,
                                detail: reason);
                            FinishInContext(planId, PlanStatus.Blocked, reason);
                            await RecordChangedFilesAsync(planId);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down: a running plan stays "running" and resumes on the next start.
            }
        });
    }

    // Cancels a run in progress, or blocks a plan that is queued but not started.
    public bool Stop(string planId)
    {
        if (_active.TryGetValue(planId, out CancellationTokenSource? active))
        {
            _stopRequested[planId] = true;
            active.Cancel();
            return true;
        }

        PlanRecord? plan = _store.Get(planId);
        if (plan is null || plan.Status is not (PlanStatus.Approved or PlanStatus.Running))
        {
            return false;
        }

        // Queued but not started: blocking it is enough, RunPlanAsync skips a plan that is not approved.
        _store.Update(planId, current => current with { Status = PlanStatus.Blocked });
        return true;
    }

    internal async Task RunPlanAsync(string planId, CancellationToken stopping)
    {
        PlanRecord? plan = _store.Get(planId);
        if (plan is null || plan.Status is not (PlanStatus.Approved or PlanStatus.Running))
        {
            return;
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (!_active.TryAdd(planId, runCancellation)) return;
        using FileStream? runLease = _store.TryAcquireRunLease(planId);
        if (runLease is null)
        {
            _store.AddEvent(planId, null, null, RunEventKind.RunLeaseBusy,
                detail: "No run lease was available (another backend may own it, or this backend may lack access). This instance did not start duplicate work.");
            _active.TryRemove(planId, out _);
            return;
        }
        _fitProbes?.CancelAll("Stopped: a plan needed this machine. Nothing changed.");
        _stopRequested.TryRemove(planId, out _);

        // A plan left running overnight must not be stopped by the computer going to sleep.
        using IDisposable awake = _sleepGuard.Hold($"Agent Fleet is running the plan \"{plan.Title}\"");

        try
        {
            plan = _store.Update(planId, current => current.RunDeadlineUtc is null
                ? current with { RunDeadlineUtc = _utcNow() + FleetPlanStore.DefaultRunDuration }
                : current)!;

            // An interrupted worker attempt is reconciled against the hashes captured at staging. Never infer
            // that an edit is safe from event ordering alone, and never overwrite a conflicting worker copy.
            PlanStep? unsafeInterrupted = plan.Steps.FirstOrDefault(step => step.Status == StepStatus.Running &&
                HasUnsyncedInterruptedWorkspace(plan, step));
            string? recoveryBlock = null;
            if (unsafeInterrupted is not null)
            {
                recoveryBlock = await ReconcileInterruptedWorkspaceAsync(plan, unsafeInterrupted, runCancellation.Token);
                plan = _store.Get(planId)!;
            }
            PlanStep? unsafeReplay = plan.Steps.FirstOrDefault(step => step.Status == StepStatus.Running && (!step.RetrySafe || IsTestsStep(step)) &&
                HasInterruptedAttempt(plan, step));

            if (recoveryBlock is not null || unsafeReplay is not null)
            {
                PlanStep blockedStep = recoveryBlock is not null ? unsafeInterrupted! : unsafeReplay!;
                string reason = recoveryBlock ?? (IsTestsStep(blockedStep)
                    ? "The tests step was interrupted and its original change baseline is unavailable; review it before retrying."
                    : $"Step {blockedStep.Id} is not marked safe to replay after a backend restart. Its workspace was preserved; review the changes, then approve the plan to continue.");
                _store.Update(planId, current => FleetPlanStoreSteps.With(current, blockedStep.Id, step => step with
                {
                    Status = StepStatus.Failed,
                    Note = reason
                }) with { Status = PlanStatus.Blocked });
                AddBlockedEvent(planId, blockedStep, reason);
                await RecordChangedFilesAsync(planId);
                return;
            }

            bool continuing = plan.Steps.Any(step => step.Status is StepStatus.Running or StepStatus.Failed or StepStatus.Parked or StepStatus.Done);
            _lastFailures[planId] = LastFailures(plan);
            _store.Update(planId, current => current with
            {
                Status = current.Steps.All(step => step.Status == StepStatus.Done) ? PlanStatus.Done : PlanStatus.Running,
                Steps = current.Steps
                    .Select(step => step.Status is StepStatus.Running or StepStatus.Failed ? step with { Status = StepStatus.Pending } : step)
                    .ToList()
            });
            _store.AddEvent(
                planId,
                null,
                null,
                continuing ? RunEventKind.Resumed : RunEventKind.RunStarted,
                detail: continuing
                    ? $"Continuing after {plan.Steps.Count(step => step.Status == StepStatus.Done)} finished step(s)."
                    : $"{plan.Steps.Count} step(s), working in {plan.WorkingDirectory ?? "the default folder"}. Run deadline: {plan.RunDeadlineUtc:O}.");

            // The plan lives in one durable context; every step and handoff is written into it.
            if (_recorder?.ResolveContext(_store.Get(planId) ?? plan) is { } contextId)
            {
                _contexts[planId] = contextId;
                _recorder.RunStarted(contextId, _store.Get(planId) ?? plan, continuing);
            }

            // The plan is a graph, not a queue: the next step is the first one whose dependencies are done. A step that gets
            // stuck is parked, and only the steps that depend on it wait; everything else goes on.
            while (true)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                PlanRecord current = _store.Get(planId)!;
                List<PlanStep> ready = current.Steps
                    .Where(candidate => candidate.Status == StepStatus.Pending && PlanGraph.IsReady(current, candidate))
                    .ToList();
                if (ready.Count == 0)
                {
                    // Nothing can run now. The run is closed in one update, so a step the user retried a moment ago is not missed.
                    bool nothingElse = false;
                    _store.Update(planId, latest =>
                    {
                        nothingElse = !latest.Steps.Any(candidate => candidate.Status == StepStatus.Pending && PlanGraph.IsReady(latest, candidate));
                        return nothingElse && latest.Steps.Any(candidate => candidate.Status != StepStatus.Done)
                            ? latest with { Status = PlanStatus.Blocked }
                            : latest;
                    });
                    if (nothingElse)
                    {
                        break;
                    }

                    continue;
                }

                PlanStep step = ready[0];
                if (current.RunDeadlineUtc is { } runDeadline && _utcNow() >= runDeadline)
                {
                    BlockForDeadline(current, step, step.Attempts + 1, deferPlanBlock: false);
                    await RecordChangedFilesAsync(planId);
                    return;
                }

                int position = current.Steps.ToList().FindIndex(candidate => candidate.Id == step.Id);
                if (step.ParallelGroup is not null)
                {
                    int groupEnd = ParallelGroupEnd(current.Steps, position);
                    List<PlanStep> group = current.Steps
                        .Skip(position)
                        .Take(groupEnd - position)
                        .Where(candidate => ready.Any(waiting => waiting.Id == candidate.Id))
                        .ToList();
                    if (group.Count > 1)
                    {
                        await PreflightChecksAsync(planId, group.Select(candidate => candidate.Id).ToList(), runCancellation.Token);
                        current = _store.Get(planId)!;
                        group = group
                            .Select(member => current.Steps.First(candidate => candidate.Id == member.Id))
                            .Where(member => member.Status == StepStatus.Pending)
                            .ToList();
                        if (group.Count == 0)
                        {
                            continue;
                        }

                        if (group.Count > 1)
                        {
                            string? fallbackReason = await ParallelFallbackReasonAsync(current, group, runCancellation.Token);
                            if (fallbackReason is null)
                            {
                                _store.AddEvent(planId, null, null, RunEventKind.ParallelStarted,
                                    detail: $"Steps {string.Join(", ", group.Select(candidate => candidate.Id))} are starting concurrently on distinct ready tiers.");
                                if (!await RunParallelGroupAsync(current, group, runCancellation.Token))
                                {
                                    await RecordChangedFilesAsync(planId);
                                    return;
                                }

                                continue;
                            }

                            bool firstInGroup = position == 0 || current.Steps[position - 1].ParallelGroup != step.ParallelGroup;
                            if (firstInGroup)
                            {
                                _store.AddEvent(planId, null, null, RunEventKind.ParallelFallback,
                                    detail: $"Parallel group '{step.ParallelGroup}' will run in order because {fallbackReason}.");
                            }
                        }

                        step = group[0];
                    }
                }

                await PreflightChecksAsync(planId, [step.Id], runCancellation.Token);
                current = _store.Get(planId)!;
                step = current.Steps.First(candidate => candidate.Id == step.Id);
                if (step.Status != StepStatus.Pending)
                {
                    continue;
                }

                runCancellation.Token.ThrowIfCancellationRequested();

                // A step the user retried while the run went on starts from what the log says went wrong, as one retried at approval does.
                _lastFailures[planId] = LastFailures(current);
                if (!await RunStepAsync(current, step, runCancellation.Token, firstAttempt: step.Attempts + 1,
                        previousFailure: LastFailure(planId, step.Id)) &&
                    _store.Get(planId)!.Status == PlanStatus.Blocked)
                {
                    await RecordChangedFilesAsync(planId);
                    return;
                }
            }

            PlanRecord finished = _store.Get(planId)!;
            if (finished.Steps.All(candidate => candidate.Status == StepStatus.Done))
            {
                int reviewedSteps = finished.Steps.Count(step =>
                    finished.Events?.Where(e => e.Kind == RunEventKind.StepDone && e.StepId == step.Id)
                        .LastOrDefault() is { Attempt: int attempt } && HasPassedReview(finished, step.Id, attempt));
                _store.AddEvent(planId, null, null, RunEventKind.PlanDone,
                    detail: $"Every step passed its check. Independent review: {reviewedSteps} of {finished.Steps.Count} steps.");
                FinishInContext(planId, PlanStatus.Done, "Every step passed its check.");
                Notify(finished, "plan-done");
            }
            else
            {
                EndWithStoppedSteps(finished);
            }

            await RecordChangedFilesAsync(planId);
        }
        catch (OperationCanceledException) when (_stopRequested.TryRemove(planId, out bool userStop) && userStop)
        {
            _store.Update(planId, current => current with
            {
                Status = PlanStatus.Blocked,
                Steps = current.Steps
                    .Select(step => step.Status == StepStatus.Running
                        ? step with { Status = StepStatus.Pending, Note = "Stopped by the user." }
                        : step)
                    .ToList()
            });
            _store.AddEvent(planId, null, null, RunEventKind.Stopped, detail: "Stopped by the user.");
            FinishInContext(planId, PlanStatus.Blocked, "Stopped by the user.");
            await RecordChangedFilesAsync(planId);
            _logger.LogInformation("Plan {PlanId} was stopped by the user.", planId);
        }
        finally
        {
            _lastFailures.TryRemove(planId, out _);
            RemoveFailFirstSnapshots(planId);
            _active.TryRemove(planId, out _);
            _contexts.TryRemove(planId, out _);
        }
    }

    private string? ContextFor(string planId) => _contexts.TryGetValue(planId, out string? id) ? id : null;

    // Keys are "plan:step", so a finished or stopped plan's snapshots are the ones with its prefix.
    private void RemoveFailFirstSnapshots(string planId)
    {
        string prefix = planId + ":";
        foreach (string key in _testFirstSnapshotAttempts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            _testFirstSnapshotAttempts.TryRemove(key, out _);
        foreach (string key in _failFirstSnapshots.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            _failFirstSnapshots.TryRemove(key, out _);
    }

    // What last went wrong with each unfinished step, from the run log, which survives a restart. Measured: a step
    // retried after its plan blocked started from nothing, with only a short excerpt of its failure in the durable
    // record, because the attempt count and the failure lived in the run that had ended.
    private static Dictionary<int, string> LastFailures(PlanRecord plan)
    {
        var failures = new Dictionary<int, string>();
        foreach (PlanRunEvent runEvent in plan.Events ?? [])
        {
            if (runEvent.StepId is { } stepId && !string.IsNullOrWhiteSpace(runEvent.Detail) &&
                runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed or RunEventKind.ModelFailed or RunEventKind.TimedOut &&
                plan.Steps.Any(step => step.Id == stepId && step.Status != StepStatus.Done))
            {
                failures[stepId] = runEvent.Detail;
            }
        }

        return failures;
    }

    private string? LastFailure(string planId, int stepId) =>
        _lastFailures.TryGetValue(planId, out Dictionary<int, string>? failures) && failures.TryGetValue(stepId, out string? failure) ? failure : null;

    /// <summary>
    /// The user's call on a stopped or blocked plan (see <see cref="FleetPlanStore.SkipStep"/>), written into the
    /// plan's record too: the next step's handoff says the step was skipped and its check never ran, instead of the
    /// last handoff, which still pointed at the skipped step.
    /// </summary>
    public PlanRecord? SkipStep(string planId, int stepId)
    {
        PlanRecord? before = _store.Get(planId);
        PlanRecord? after = _store.SkipStep(planId, stepId);
        PlanStep? step = after?.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
        bool skipped = before?.Steps.FirstOrDefault(candidate => candidate.Id == stepId)?.Status != StepStatus.Done && step?.Status == StepStatus.Done;
        if (skipped && _recorder is not null && after is not null && step is not null && (ContextFor(planId) ?? _recorder.ResolveContext(after)) is { } contextId)
        {
            _recorder.StepDone(contextId, after, step, verificationEventId: null, node: null, skipped: true);
        }

        return after;
    }

    private void FinishInContext(string planId, string status, string note)
    {
        if (_recorder is not null && ContextFor(planId) is { } contextId && _store.Get(planId) is { } plan)
        {
            _recorder.PlanFinished(contextId, plan, status, note);
        }
    }

    // The morning question is "what did it change?". When the project is a git repository, the run
    // log ends with git's own list of changed files. Best effort: never lets a failure here hide how
    // the run ended.
    private static int ParallelGroupEnd(IReadOnlyList<PlanStep> steps, int start)
    {
        string? group = steps[start].ParallelGroup;
        if (group is null)
        {
            return start + 1;
        }

        int end = start + 1;
        while (end < steps.Count && steps[end].ParallelGroup == group)
        {
            end++;
        }

        return end;
    }

    private async Task<string?> ParallelFallbackReasonAsync(PlanRecord plan, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        if (steps.Any(IsTestsStep)) return "tests steps need an isolated step-start change baseline";
        if (_workerWorkspaces is not null)
        {
            return "worker workspaces sync each completed step to the shared project before the next one starts";
        }

        if (_fleetOptions is null || _healthMonitor is null)
        {
            return "fleet routing health is unavailable";
        }

        if (steps.Select(step => step.Tier).Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            return "the steps do not use distinct model tiers";
        }

        string? fileReason = ParallelFileOverlapReason(plan, steps);
        if (fileReason is not null)
        {
            return fileReason;
        }

        foreach (string tier in steps.Select(step => step.Tier).Distinct(StringComparer.Ordinal))
        {
            FleetNodeDefinition[] candidates = _fleetOptions.Nodes
                .Where(node => !node.Vision && node.Tier == tier)
                .ToArray();
            if (candidates.Length == 0)
            {
                return $"tier '{tier}' has no configured node";
            }

            bool ready = await WaitOutShortRestAsync(
                forceProbe => Task.WhenAll(candidates.Select(node => _healthMonitor.GetNodeAsync(node.Name, cancellationToken, forceProbe))),
                ShortRestWait,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (!ready)
            {
                return $"no node in tier '{tier}' is ready";
            }
        }

        return null;
    }

    /// <summary>
    /// A machine that fails one request rests for a minute (see ResilientChatClient). Measured: a worker dropped one
    /// request as step 1 ended, and the group of three that started 18 s later ran one step at a time for its whole
    /// length. So a tier that is out only for that short rest is waited for before the group gives up on running in
    /// parallel; a machine that is slow or unreachable is not.
    /// </summary>
    internal static readonly TimeSpan ShortRestWait = TimeSpan.FromSeconds(75);

    internal static async Task<bool> WaitOutShortRestAsync(
        Func<bool, Task<NodeHealthSnapshot[]>> check,
        TimeSpan wait,
        TimeSpan poll,
        CancellationToken cancellationToken)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow + wait;
        for (bool again = false; ; again = true)
        {
            NodeHealthSnapshot[] health = await check(again);
            if (health.Any(snapshot => snapshot.Ready))
            {
                return true;
            }

            if (!health.Any(snapshot => snapshot.Failure == "request_failed") || DateTimeOffset.UtcNow >= giveUp)
            {
                return false;
            }

            await Task.Delay(poll, cancellationToken);
        }
    }

    private static string? ParallelFileOverlapReason(PlanRecord plan, IReadOnlyList<PlanStep> steps)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            return "the project folder is unavailable";
        }

        string root;
        try
        {
            root = Path.GetFullPath(plan.WorkingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return "the project folder path is invalid";
        }

        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var filesUsedByEarlierSteps = new HashSet<string>(comparer);

        foreach (PlanStep step in steps)
        {
            if (step.Files.Count == 0)
            {
                return $"step {step.Id} does not name files, so independence cannot be checked";
            }

            var stepFiles = new HashSet<string>(comparer);
            foreach (string requestedPath in step.Files)
            {
                string path;
                try
                {
                    if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
                    {
                        return $"step {step.Id} has an invalid file path";
                    }

                    path = Path.GetFullPath(Path.IsPathFullyQualified(requestedPath)
                        ? requestedPath
                        : Path.Combine(root, requestedPath));
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
                {
                    return $"step {step.Id} has an invalid file path";
                }

                // A whole folder could hold the other steps' files, so it cannot be shown to be separate from them.
                if (Directory.Exists(path) || Path.EndsInDirectorySeparator(requestedPath))
                {
                    return $"step {step.Id} names a whole folder";
                }

                if (!path.StartsWith(rootPrefix, comparison) || HasLinkedPathComponent(root, path))
                {
                    return $"step {step.Id} names a path outside the project or through a link";
                }

                stepFiles.Add(path);
            }

            if (stepFiles.Overlaps(filesUsedByEarlierSteps))
            {
                return "two steps name the same file";
            }

            filesUsedByEarlierSteps.UnionWith(stepFiles);
        }

        return null;
    }

    private async Task RecordChangedFilesAsync(string planId)
    {
        try
        {
            PlanRecord? plan = _store.Get(planId);
            string? changed = await _tools.ChangedFilesAsync(plan?.WorkingDirectory, CancellationToken.None);
            if (changed is not null)
            {
                _store.AddEvent(planId, null, null, RunEventKind.FilesChanged,
                    detail: changed.Length == 0 ? "git reports no changed files." : changed);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not list the files changed by plan {PlanId}.", planId);
        }
    }

    /// <summary>
    /// One step, as rounds in a conversation: the model works, the runner runs the approved check, and a failure goes back
    /// into the same conversation (a round) instead of starting a new one. Rounds stop paying off when a round changes
    /// nothing, the same failures come back with no file changed, or a rung's round budget is used: the step then climbs
    /// the repair ladder (see <see cref="RepairLadder"/>). An attempt is one conversation with a model; machine outages and
    /// other failures that give no verdict wait or reroute without using a round or an attempt.
    /// </summary>
    private async Task<bool> RunStepAsync(
        PlanRecord plan,
        PlanStep step,
        CancellationToken cancellationToken,
        int firstAttempt = 1,
        string? previousFailure = null,
        bool deferPlanBlock = false,
        string? initialModelOverride = null,
        string? initialWorkspaceOverride = null,
        IStepSession? continuingSession = null)
    {
        string? lastFailure = previousFailure;
        string? workspaceOverride = initialWorkspaceOverride;
        string? modelOverride = initialModelOverride;
        PlanRunEvent[] earlierEvents = StepEventsSinceRetry(plan.Id, step.Id);
        int waits = earlierEvents.Count(runEvent => runEvent.Kind == RunEventKind.Waiting);

        // Machines that failed since the step last waited or got an answer. A failed call may move to another machine at
        // once, but never to one that failed a moment ago: with the worker and the hub both down that would bounce between
        // them as fast as the refusals come back, instead of waiting.
        var failedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedWorkspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RepairPosition position = RepairLadder.PositionFrom(earlierEvents);
        int rung = position.Rung;
        int roundsInRung = position.RoundsInRung;
        int roundsTotal = position.RoundsTotal;
        int? bestFailing = RepairLadder.BestFailing(earlierEvents);
        TimeSpan worked = TimeSpan.FromSeconds(earlierEvents
            .Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified).Sum(runEvent => runEvent.DurationSeconds ?? 0));
        string? hub = _fleetOptions?.Nodes.SingleOrDefault(node => node.Fallback)?.Name;
        bool hubAllowed = hub is not null && HubRescueAllowed(plan, step);

        // A restart loses the open conversation, but not the rung: a step that had climbed to the hub stays on it.
        if (modelOverride is null && rung >= RepairLadder.HubRung && hubAllowed)
        {
            modelOverride = hub;
        }

        IStepSession? session = continuingSession;
        string? conversationWorkspace = null;
        RoundOutcome? lastRound = null;
        string? lastCheckOutput = null;
        string? lastUnverified = null;
        bool lastReviewFailed = false;
        string? handover = null;
        bool gaveUp = false;
        StepCheckpoint? best = null;
        RollbackNote? rollback = null;

        // The restart landed after the last round of a rung and before the climb was written down.
        if (roundsInRung >= _roundsPerRung)
        {
            RepairDecision spent = RepairLadder.Decide(rung, roundsInRung, _roundsPerRung, noChange: false, string.Empty, hubRungAvailable: false);
            if (spent.Move == RepairMove.GiveUp)
            {
                gaveUp = true;
            }
            else
            {
                modelOverride = await ClimbAsync(plan, step, spent, rung, firstAttempt, step.Tier, modelOverride, workspaceOverride, null, hub, hubAllowed, cancellationToken);
                rung = spent.ToRung;
                roundsInRung = 0;
            }
        }

        for (int attempt = firstAttempt; attempt <= MaxAttemptsPerStep && !gaveUp; attempt++)
        {
            session ??= IsTestsStep(step) ? _agent.OpenSession(PlanRunnerToolPolicy.TestWriterRole) : _agent.OpenSession();
            IStepSession conversation = session;
            if (!conversation.HasHistory)
            {
                conversationWorkspace = null;
            }

            bool conversationOver = false;
            while (!conversationOver)
            {
                if (plan.RunDeadlineUtc is { } deadline && _utcNow() >= deadline)
                {
                    BlockForDeadline(plan, step, attempt, deferPlanBlock);
                    return false;
                }

                if (worked >= _stepClock)
                {
                    ParkForStepClock(plan, step, attempt, worked, best);
                    return false;
                }

                string tier = step.Tier;
                int round = roundsInRung + 1;
                string? repairMessage = null;
                string? brief = null;
                if (conversation.HasHistory)
                {
                    repairMessage = BuildRepairMessage(plan, step, lastFailure, round, lastRound, handover, rollback);
                    if (IsTestsStep(step)) repairMessage = PlanTestsStepPrompt.Append(repairMessage, plan.Requirements);
                }
                else if (RepairMessages.Brief(StepEventsSinceRetry(plan.Id, step.Id)) is { Length: > 0 } earlierRounds)
                {
                    brief = earlierRounds;
                }

                handover = null;
                bool continuing = repairMessage is not null;
                DateTimeOffset roundStarted = _utcNow();
                using Activity? roundActivity = FleetTelemetry.StartPlanRound(plan.Id, step.Id, attempt, round, rung,
                    workspaceOverride ?? conversationWorkspace ?? step.Machine, modelOverride);
                ModelAttemptResult model = await RunModelAttemptAsync(
                    plan, step, attempt, lastFailure, tier, parallelGroup: false, cancellationToken: cancellationToken,
                    workspaceOverride: workspaceOverride ?? (continuing ? conversationWorkspace : null), modelMachineOverride: modelOverride,
                    round: new RoundContext(conversation, rung, round, repairMessage, brief), limit: LimitFor(plan, worked));
                roundActivity?.SetTag("fleet.model_node", model.ModelNode);
                roundActivity?.SetTag("fleet.workspace_node", model.Machine);
                if (_testFirstMode == TestFirstMode.On && !IsTestsStep(step) && model.Dispute is { } dispute)
                    RecordDispute(plan, step, attempt, tier, model, rung, round, dispute);
                if ((!IsTestsStep(step) || _testFirstSnapshotAttempts.TryAdd($"{plan.Id}:{step.Id}", true)) && model.WorkBefore is { } firstSnapshot)
                    _failFirstSnapshots.TryAdd($"{plan.Id}:{step.Id}", firstSnapshot);
                if (model.Machine is not null)
                {
                    conversationWorkspace = model.Machine;
                }

                if (!model.ShouldVerify)
                {
                    model.Workspace?.Dispose();
                    WorkChange failedAttemptChange = await ChangedFilesSinceAsync(plan, model.WorkBefore, cancellationToken);
                    RoundOutcome failedRound = await RecordRoundClassifiedAsync(plan, step, attempt, model, model.Failure ?? string.Empty,
                        passed: false, failedAttemptChange.Files, rung);
                    FailureClass roundClass = failedRound.Class;
                    if (model.WorkspaceFailure is { } workspaceFailure)
                    {
                        if (model.ReroutableWorkspaceFailure)
                        {
                            failedWorkspaces.Add(model.Machine ?? string.Empty);
                            (string? alternate, string detail) = await SelectRetryMachineAsync(plan, step, tier, cancellationToken);
                            if (alternate is not null && failedWorkspaces.Contains(alternate))
                            {
                                detail = $"{alternate} failed a moment ago too, so Fleet waits instead of moving between machines that are both down.";
                                alternate = null;
                            }

                            if (alternate is not null)
                            {
                                workspaceOverride = alternate;
                                if (step.Machine is not null)
                                {
                                    // A user-selected worker failed before the model could edit anything. Persist the
                                    // safe same-tier replacement so a later restart keeps using it.
                                    step = step with { Machine = alternate };
                                    _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id,
                                        currentStep => currentStep with { Machine = alternate }));
                                    plan = _store.Get(plan.Id)!;
                                }
                                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RecoveryRouted, tier,
                                    node: null,
                                    detail: $"Worker staging failed before model tools or edits. {detail}",
                                    modelNode: null, workspaceNode: alternate, rung: rung, round: round);
                                continue;
                            }

                            GiveBackAttempt(plan.Id, step.Id, attempt, conversation);
                            int waitNumber = ++waits;
                            TimeSpan delay = _transientDelay(waitNumber - 1);
                            delay = DelayWithinRunDeadline(plan, delay);
                            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.Waiting, tier, node: model.Machine,
                                detail: $"Worker staging failed before model tools or edits ({Summarize(workspaceFailure)}). {detail} Waiting {Duration(delay)} (wait {waitNumber}) before retrying; this does not count as a model attempt.",
                                modelNode: null, workspaceNode: model.Machine, rung: rung, round: round);
                            await DelayBeforeRetryAsync(delay, cancellationToken);
                            failedWorkspaces.Clear();
                            continue;
                        }

                        ParkForEnvironment(plan, step, attempt, tier, model.Machine, workspaceFailure,
                            "the selected worker workspace is not configured or could not be staged");
                        return false;
                    }

                    if (roundClass == FailureClass.Environment)
                    {
                        ParkForEnvironment(plan, step, attempt, tier, model.ModelNode, model.Failure ?? "The model route failed with an environment error.",
                            "the selected model machine has a permission, security, or runtime problem");
                        return false;
                    }

                    if (roundClass is FailureClass.Infra or FailureClass.Unknown)
                    {
                        // The next message is about the check's output; a failed call only gives the failure to speak of
                        // when there is nothing better, as for a first message that never reached a model.
                        if (!conversation.HasHistory && string.IsNullOrWhiteSpace(lastFailure))
                        {
                            lastFailure = model.Failure;
                        }

                        int unknownFailures = CountClassifiedRounds(plan.Id, step.Id, FailureClass.Unknown);
                        if (roundClass == FailureClass.Unknown && unknownFailures >= 5)
                        {
                            ParkForUnknown(plan, step, attempt, model.FailureException, model.Failure);
                            return false;
                        }

                        if (roundClass == FailureClass.Infra)
                        {
                            failedModels.Add(model.ModelNode ?? string.Empty);
                            string? route = IsWorkerTransportFailure(model.FailureException)
                                ? null
                                : await RouteFailedModelAsync(plan, step, tier, model.ModelNode, model.Machine,
                                    attempt, cancellationToken, recentlyFailed: failedModels);
                            if (route is not null)
                            {
                                modelOverride = route;
                                continue;
                            }
                        }

                        GiveBackAttempt(plan.Id, step.Id, attempt, conversation);
                        if (roundClass == FailureClass.Unknown)
                        {
                            TimeSpan delay = DelayWithinRunDeadline(plan, TimeSpan.FromSeconds(20 * unknownFailures));
                            string failedNode = model.ModelNode ?? "the selected model machine";
                            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.Waiting, tier, node: model.ModelNode,
                                detail: $"Unknown failure on {failedNode}: {Summarize(model.Failure ?? "unknown failure")}. Waiting at least {Duration(delay)} (unknown failure {unknownFailures} of 5) before retrying; this does not count as an attempt.",
                                modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                            await DelayBeforeRetryAsync(delay, cancellationToken);
                            failedModels.Clear();
                            continue;
                        }

                        failedModels.Clear();
                        (modelOverride, waits) = await WaitForInfrastructureRecoveryAsync(plan, step, tier,
                            model.ModelNode, model.Machine, attempt, model.Failure, waits, cancellationToken,
                            allowRoute: !IsWorkerTransportFailure(model.FailureException));
                        continue;
                    }

                    // A failure that is neither a machine nor an environment problem and gave no verdict: it costs a round.
                    if (!conversation.HasHistory && string.IsNullOrWhiteSpace(lastFailure))
                    {
                        lastFailure = model.Failure;
                    }

                    roundsInRung++;
                    roundsTotal++;
                    RepairDecision failedDecision = RepairLadder.Decide(rung, roundsInRung, _roundsPerRung, noChange: false, string.Empty,
                        HubRungAvailable(step, hub, hubAllowed, model.ModelNode));
                    if (failedDecision.Move != RepairMove.NextRound)
                    {
                        (rung, roundsInRung, modelOverride, conversationOver, gaveUp, handover) = await MoveAsync(
                            plan, step, failedDecision, rung, attempt, tier, model, modelOverride, workspaceOverride, hub, hubAllowed, cancellationToken);
                        if (conversationOver) session = null;
                    }

                    continue;
                }

                failedModels.Clear();
                failedWorkspaces.Clear();
                StepCompletion result;
                string? checkNote = null;
                string? parkReason = null;
                FailFirstResult? failFirst = null;
                FinalCheckFailure? finalCheckFailure = null;
                List<string> restoredNow = [];
                List<string> frozenNow = [];
                bool isFinalValidation = !IsTestsStep(step) && step.Id == plan.Steps.Max(candidate => candidate.Id);
                try
                {
                    using IDisposable? workerScope = model.Workspace?.Enter();
                    if (isFinalValidation)
                    {
                        string snapshot = model.Workspace?.SnapshotId() ?? "unavailable";
                        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FinalValidationStarted, tier,
                            node: ViaNode(model.Summary), detail: $"Runner is executing the approved final check: `{step.Verify}` on source snapshot `{snapshot}`.",
                            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                    }

                    // A check that cannot run or finish, or a machine without the project's own dependencies, is repaired here and
                    // the check runs again in the same workspace, so it costs no round.
                    using Activity? checkActivity = FleetTelemetry.StartPlanCheck(plan.Id, step.Id, attempt, round, rung,
                        model.Machine, model.ModelNode);
                    if (model.Workspace is not null) await RestoreFrozenTestsAsync(plan, model.Workspace, restoredNow, cancellationToken);
                    else RestoreHubFrozenTests(plan, restoredNow);
                    CheckRun run = model.Workspace is null
                        ? await RunCheckWithRepairsAsync(plan, step, attempt, tier, rung, round, model, cancellationToken)
                        : await RunWithReconnectsAsync(
                            model.Workspace,
                            token => RunCheckWithRepairsAsync(plan, step, attempt, tier, rung, round, model, token),
                            "running the check", cancellationToken, cancellationToken,
                            plan, step, attempt, tier,
                            (exception, token) => IsWorkerCheckTransportFailureAsync(exception, model.Workspace, token));
                    result = run.Result;
                    checkNote = run.Note;
                    parkReason = run.ParkReason;
                    if (!ReferenceEquals(run.Step, step))
                    {
                        step = run.Step;
                        plan = _store.Get(plan.Id) ?? plan;
                    }

                    if (result.Done && ImplementationSetsFor(plan, step) is { Count: > 0 } frozenSets)
                    {
                        if (model.Workspace is not null) await RestoreFrozenTestsAsync(plan, model.Workspace, restoredNow, cancellationToken);
                        else RestoreHubFrozenTests(plan, restoredNow);
                        if (await FrozenTestsRefusalAsync(plan, frozenSets, cancellationToken) is { } frozenRefusal)
                            result = new StepCompletion(false, frozenRefusal, frozenRefusal, result.Restored);
                    }

                    // The plan's whole-project check runs on this step's changes before they are synced back (see PlanRunnerRegression).
                    if (result.Done)
                    {
                        finalCheckFailure = await RunFinalCheckAsync(plan, step, isFinalValidation, cancellationToken);
                    }

                    if (model.Workspace is not null)
                    {
                        await SyncToHubWithReconnectAsync(plan, step, attempt, model.Workspace, cancellationToken, cancellationToken);
                        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkspaceSynced, tier, model.Machine,
                            "Synced the worker project after verification; the checked changes are now in the hub checkout.",
                            modelNode: model.ModelNode, workspaceNode: model.Machine);

                        if (!IsTestsStep(step) && result.Done && !(isFinalValidation && plan.Steps.Count > 1) && _failFirstMode != FailFirstMode.Off &&
                            _failFirstSnapshots.TryGetValue($"{plan.Id}:{step.Id}", out PlanTools.WorkSnapshot? stepStart))
                        {
                            WorkChange current = await ChangedFilesSinceAsync(plan, stepStart, cancellationToken);
                            string[] tests = current.Files.Where(IsTestPath).ToArray();
                            string[] code = current.Files.Where(path => !IsTestPath(path)).ToArray();
                            if (tests.Length == 0)
                                failFirst = new FailFirstResult("no-test-changes", [] , null);
                            else if (code.Length == 0)
                                failFirst = new FailFirstResult("tests-only", tests, null);
                            else if (UnverifiedPassReason(plan, step, model, current.Files, isFinalValidation, result.Output ?? result.Message) is null)
                            {
                                IReadOnlyDictionary<string, byte[]?>? overlay = FailFirst.CreateOverlay(stepStart, current.Files, plan.WorkingDirectory!,
                                    (head, relative) => FailFirst.ReadHead(plan.WorkingDirectory!, head, relative));
                                if (overlay is null)
                                    failFirst = new FailFirstResult("unavailable", tests, null);
                                else
                                {
                                    try
                                    {
                                        FailFirstCheckResult scratch = await _workerWorkspaces!.RunScratchCheckAsync(plan, step, model.Machine!, model.Workspace,
                                            overlay, step.Verify ?? string.Empty, cancellationToken);
                                        string output = scratch.Output;
                                        string failOutcome = scratch.TimedOut ? "timed-out" : EnvironmentBlockReason(output) is not null ? "unavailable" : scratch.ExitCode == 0 ? "passed-before" : "failed-before";
                                        failFirst = new FailFirstResult(failOutcome, tests, scratch.ExitCode, scratch.DurationSeconds);
                                    }
                                    catch (WorkerCommandTimeoutException)
                                    {
                                        failFirst = new FailFirstResult("timed-out", tests, null);
                                    }
                                    catch (Exception exception) when (exception is not OperationCanceledException)
                                    {
                                        failFirst = new FailFirstResult("unavailable", tests, null);
                                    }
                                }
                            }
                            if (failFirst is not null)
                                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FailFirst, tier, model.Machine,
                                    FailFirst.Evidence(failFirst), modelNode: model.ModelNode, workspaceNode: model.Machine,
                                    durationSeconds: (int)Math.Ceiling(failFirst.DurationSeconds));
                        }
                        else if (!IsTestsStep(step) && result.Done && !(isFinalValidation && plan.Steps.Count > 1) && _failFirstMode != FailFirstMode.Off)
                        {
                            failFirst = new FailFirstResult("unavailable", [], null);
                            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FailFirst, tier, model.Machine,
                                FailFirst.Evidence(failFirst), modelNode: model.ModelNode, workspaceNode: model.Machine);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException && model.Workspace is not null)
                {
                    _logger.LogError(exception, "Worker verification or sync failed for plan {PlanId} step {StepId} on {Machine}.", plan.Id, step.Id, model.Machine);
                    IWorkerWorkspaceSession workspace = model.Workspace;
                    bool commandTimedOut = false;
                    string? partialOutput = null;
                    for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
                    {
                        if (cause is WorkerCommandTimeoutException timedOut)
                        {
                            commandTimedOut = true;
                            partialOutput = timedOut.PartialOutput;
                            break;
                        }
                    }

                    string failure;
                    if (commandTimedOut && await workspace.AnswersAsync(cancellationToken))
                    {
                        failure = TimedOutCheckFailure(workspace.CommandTimeout, partialOutput, plan.WorkingDirectory);
                    }
                    else
                    {
                        failure = $"Worker workspace unavailable: verification or sync failed on {model.Machine}: {exception.Message}";
                    }
                    result = new StepCompletion(false, failure, failure);
                }
                catch (FrozenTestsUnrestorableException exception) when (model.Workspace is null)
                {
                    // A hub round whose fixed test cannot be put back fails at once: the check would otherwise judge the wrong test.
                    _logger.LogWarning("Plan {PlanId} step {StepId}: {Message}", plan.Id, step.Id, exception.Message);
                    result = new StepCompletion(false, exception.Message, exception.Message);
                }
                finally
                {
                    model.Workspace?.Dispose();
                }

                if (parkReason is not null)
                {
                    ParkForCheck(plan, step, parkReason);
                    return false;
                }

                WorkChange change = await ChangedFilesSinceAsync(plan, model.WorkBefore, cancellationToken);
                if (IsTestsStep(step))
                {
                    _failFirstSnapshots.TryGetValue($"{plan.Id}:{step.Id}", out PlanTools.WorkSnapshot? start);
                    WorkChange total = await ChangedFilesSinceAsync(plan, start, cancellationToken);
                    bool known = start is not null && total.After is not null && start.Uncaptured.Count == 0 &&
                        total.After.Uncaptured.Count == 0 && start.Head == total.After.Head;
                    IReadOnlyList<string>? accepted = known ? total.Files : null;
                    result = DecideTestsStep(plan, step, attempt, tier, rung, round, model, result, accepted);
                    if (result.Done && accepted is not null) FreezeAcceptedTests(plan, step, accepted, frozenNow);
                }
                // A check that passes although the step changed nothing it names cannot show that the step's work is done.
                lastUnverified = !IsTestsStep(step) && result.Done ? UnverifiedPassReason(plan, step, model, change.Files, isFinalValidation, result.Output ?? result.Message) : null;
                if (lastUnverified is not null)
                {
                    string unverified = UnverifiedPassMessage(step, lastUnverified);
                    result = new StepCompletion(false, unverified, unverified, result.Restored);
                }

                if (result.Done && lastUnverified is null && _failFirstMode == FailFirstMode.Enforce && failFirst?.Outcome == "passed-before")
                {
                    const string feedback = "Your tests also pass on the code as it was before this step, so they do not test your change. Add tests for what this step must make true; they must fail without your change.";
                    result = new StepCompletion(false, feedback, feedback, result.Restored);
                }

                // A passing check does not finish a step that left a file it lists as it was: the worker gets one more round in this
                // conversation, once per attempt. If a listed file is still untouched after that round, the step is sent back too,
                // unless the round's report_blocker names the file (see UntouchedListedFilesAsync). It comes before the review, so a
                // step sent back for it is not reviewed first.
                IReadOnlyList<string> untouched = result.Done
                    ? await UntouchedListedFilesAsync(plan, step, isFinalValidation, cancellationToken)
                    : [];
                if (untouched.Count > 0 && !UntouchedRoundGiven(plan, step, attempt))
                {
                    string note = UntouchedFilesMessage(untouched);
                    _store.AddEvent(plan.Id, step.Id, attempt, UntouchedRoundKind, tier, node: model.ModelNode ?? model.Machine,
                        detail: $"The check passed, but these listed files were not changed: {string.Join(", ", untouched)}. The worker gets one more round before the step is accepted.",
                        modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                    result = new StepCompletion(false, note, note, result.Restored);
                }
                else if (untouched.Count > 0 && NotNamedByBlocker(untouched, model.Blocker) is { Count: > 0 } stillUntouched)
                {
                    string note = StillUntouchedMessage(stillUntouched);
                    result = new StepCompletion(false, note, note, result.Restored);
                }

                // Files the step left behind that the plan does not name get one extra round too, once per attempt (see
                // UnlistedNewFiles). A step the untouched rule just sent back is not given a second message in the same round.
                IReadOnlyList<string> unlisted = result.Done && !UnlistedRoundGiven(plan, step, attempt)
                    ? UnlistedNewFiles(plan, step, isFinalValidation)
                    : [];
                if (unlisted.Count > 0)
                {
                    string note = UnlistedFilesMessage(unlisted);
                    _store.AddEvent(plan.Id, step.Id, attempt, UnlistedRoundKind, tier, node: model.ModelNode ?? model.Machine,
                        detail: $"The check passed, but these files were created and are not part of this step: {UnlistedNames(unlisted)}. The worker gets one more round before the step is accepted.",
                        modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round, changedFiles: unlisted);
                    result = new StepCompletion(false, note, note, result.Restored);
                }

                // A passing step whose whole-project check failed gets one round too, once per attempt (see PlanRunnerRegression).
                // It comes before the review, so a step sent back for it is not reviewed first.
                if (finalCheckFailure is not null)
                {
                    result = WithFinalCheckRound(plan, step, attempt, tier, model, rung, round, result, finalCheckFailure);
                }

                // Debugging output the step left in its changed files gets one more round too, once per attempt (see DebugOutputLines).
                // It comes after the rules above, so a step any of them sent back is looked at on its next passing check. Lines
                // still there after that round are named in the step-done note, which reads the completion's note.
                IReadOnlyList<DebugLine> debugLines = result.Done ? DebugOutputLines(plan, step, change.Files) : [];
                if (debugLines.Count > 0 && !DebugRoundGiven(plan, step, attempt))
                {
                    string note = DebugOutputMessage(debugLines);
                    _store.AddEvent(plan.Id, step.Id, attempt, DebugOutputRoundKind, tier, node: model.ModelNode ?? model.Machine,
                        detail: $"The check passed, but these lines print debugging output: {DebugLocations(debugLines)}. The worker gets one more round before the step is accepted.",
                        modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                    result = new StepCompletion(false, note, note, result.Restored);
                }
                else if (debugLines.Count > 0)
                {
                    result = result with { Restored = JoinNotes(result.Restored ?? string.Empty, $"Debug output still in: {DebugLocations(debugLines)}.") };
                }

                // A check that passes is not yet a step that is done: a model that did not write the code reads it against the step.
                string? findings = !IsTestsStep(step) && result.Done
                    ? await ReviewAcceptedStepAsync(plan, step, attempt, tier, model, change.Files, rung, round, cancellationToken)
                    : null;
                lastReviewFailed = findings is not null;
                if (findings is not null)
                {
                    string rejected = StepReview.RejectionMessage(findings);
                    result = new StepCompletion(false, rejected, rejected, result.Restored);
                }

                RecordFrozenTests(plan, step, attempt, tier, model, rung, round, frozenNow, restoredNow);
                long? verification = RecordCheck(plan, step, attempt, result, model.ModelNode);
                TimeSpan roundTime = _utcNow() - roundStarted;
                worked += roundTime;
                RoundOutcome outcome = await RecordRoundClassifiedAsync(plan, step, attempt, model,
                    result.Output ?? result.Message, result.Done, change.Files, rung, round, (int)Math.Ceiling(roundTime.TotalSeconds));
                if (result.Done)
                {
                    _tools.CommitStepDone(plan.Id, step.Id, Summarize(model.Summary));
                    _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.CheckPassed, tier,
                        node: model.ModelNode ?? model.Machine,
                        detail: IsTestsStep(step) ? $"`{step.Verify}` failed on the new tests as required." : isFinalValidation ? $"Final validation passed: `{step.Verify}`." : $"`{step.Verify}` passed.",
                        modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                    if (isFinalValidation)
                    {
                        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FinalValidationPassed, tier,
                            node: model.ModelNode ?? model.Machine, detail: $"`{step.Verify}` passed on the final synced checkout.",
                            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                    }
                    _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.StepDone, tier,
                        detail: StepDoneDetail(plan, step, attempt, result.Restored));
                    RecordStepDone(plan, step, verification, model.ModelNode ?? model.Machine);
                    return true;
                }

                lastFailure = WithStrayNote(plan, step, result.Output ?? result.Message);
                if (restoredNow.Count > 0) lastFailure = $"{RestoredNote(restoredNow)}\n\n{lastFailure}";
                if (checkNote is not null)
                {
                    lastFailure = $"{lastFailure}\n\n{checkNote}";
                }

                if (EnvironmentBlockReason(lastFailure) is { } environmentReason)
                {
                    if (IsToolchainEnvironmentIssue(environmentReason))
                    {
                        (string? alternate, string detail) = await SelectRetryMachineAsync(plan, step, tier, cancellationToken);
                        if (alternate is not null)
                        {
                            workspaceOverride = alternate;
                            if (step.Machine is not null)
                            {
                                step = step with { Machine = alternate };
                                _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id,
                                    currentStep => currentStep with { Machine = alternate }));
                                plan = _store.Get(plan.Id)!;
                            }
                            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RecoveryRouted, tier,
                                node: null,
                                detail: $"The worker check reported a missing or incompatible runtime. {detail}",
                                modelNode: null, workspaceNode: alternate, rung: rung, round: round);

                            // The conversation so far was held on a machine whose toolchain cannot run the check: start
                            // a new one on the replacement, with what was tried. The round is in the log, so it counts.
                            roundsInRung++;
                            roundsTotal++;
                            session = null;
                            conversationOver = true;
                            continue;
                        }
                    }

                    ParkForEnvironment(plan, step, attempt, tier, model.ModelNode, lastFailure, environmentReason);
                    return false;
                }

                _store.AddEvent(plan.Id, step.Id, attempt,
                    step.Id == plan.Steps.Max(candidate => candidate.Id) ? RunEventKind.FinalValidationFailed : RunEventKind.CheckFailed,
                    tier, node: model.ModelNode ?? model.Machine, detail: lastFailure,
                    modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                plan = _store.Get(plan.Id)!;

                // The best state the project has been in so far is kept; a round that leaves it worse is undone before the
                // next one, so a repair loop cannot end up further from passing than it has been.
                rollback = null;
                if (change.After is { } after && RoundScore.Of(outcome.Signature, result.Output ?? result.Message) is { } score)
                {
                    if (best is null || !score.IsWorseThan(best.Score))
                    {
                        best = new StepCheckpoint(rung, round, score, outcome.Signature.Value, outcome.Signature.Items, lastFailure, after);
                    }
                    else
                    {
                        rollback = await RollBackAsync(plan, step, attempt, tier, model, rung, round, score, outcome, best, cancellationToken);
                        if (rollback is not null)
                        {
                            lastFailure = best.Output;
                        }
                    }
                }

                lastRound = outcome;
                lastCheckOutput = result.Output ?? result.Message;
                // A round that left fewer failing names than any round before it is progress, and progress is not charged to
                // the rung (RepairLadder.Improved): the step climbs when rounds stop paying off, not because it used three.
                int? failingNow = outcome.Signature.Items.Count > 0 ? outcome.Signature.Items.Count : null;
                bool improved = RepairLadder.Improved(failingNow, bestFailing);
                if (failingNow is { } countNow && (bestFailing is null || countNow < bestFailing))
                {
                    bestFailing = countNow;
                }

                if (improved)
                {
                    _logger.LogInformation(
                        "Plan {PlanId} step {StepId} round {Round} left {Failing} failing, fewer than before: it does not use up the rung's rounds.",
                        plan.Id, step.Id, round, failingNow);
                }
                else
                {
                    roundsInRung++;
                }

                roundsTotal++;
                // An independent model rules on a frozen test the step disputed. A corrected test gives the rung its rounds back.
                TestRulingOutcome ruling = NoRuling;
                if (_testFirstMode == TestFirstMode.On && model.Dispute is not null)
                {
                    ruling = await RuleOnDisputeAsync(plan, step, attempt, tier, model, change.Files, result.Output ?? result.Message,
                        rung, round, cancellationToken);
                    plan = _store.Get(plan.Id) ?? plan;
                }

                if (ruling.TestCorrected)
                {
                    // A checkpoint taken before the correction holds the old test, and undoing a later round would put it back.
                    roundsInRung = 0;
                    best = null;
                }

                if (ruling.Note is not null) lastFailure = $"{ruling.Note}\n\n{lastFailure}";
                // A worker that ran out of time on a round is not given the step's remaining time again when the hub may take it
                // over: the rescue starts at once (a worker-only plan keeps its worker, as before).
                bool hubRung = HubRungAvailable(step, hub, hubAllowed, model.ModelNode);
                bool tooSlow = model.TimedOut && hubRung && rung == RepairLadder.RequestedTierRung;
                RepairDecision decision = RepairLadder.Decide(rung, roundsInRung, _roundsPerRung, outcome.NoChange || tooSlow,
                    tooSlow && !outcome.NoChange ? "the worker ran out of time on this round" : NoChangeReason(outcome), hubRung);
                if (decision.Move != RepairMove.NextRound)
                {
                    (rung, roundsInRung, modelOverride, conversationOver, gaveUp, handover) = await MoveAsync(
                        plan, step, decision, rung, attempt, tier, model, modelOverride, workspaceOverride, hub, hubAllowed, cancellationToken);
                    if (conversationOver)
                    {
                        session = null;
                    }
                }

                // The call left behind by a stalled round may still be running in this conversation: the next round opens a new one.
                if (model.Stalled)
                {
                    session = IsTestsStep(step) ? _agent.OpenSession(PlanRunnerToolPolicy.TestWriterRole) : _agent.OpenSession();
                    conversation = session;
                }
            }

            if (gaveUp)
            {
                break;
            }
        }

        int attemptsUsed = _store.Get(plan.Id)?.Steps.FirstOrDefault(candidate => candidate.Id == step.Id)?.Attempts ?? 0;
        string rounds = $"{roundsTotal} round{(roundsTotal == 1 ? string.Empty : "s")} over {attemptsUsed} attempt{(attemptsUsed == 1 ? string.Empty : "s")}";
        string kept = best is null
            ? string.Empty
            : $" The project is left as it was after its best round (round {best.Round}, {best.Score.Describe()}).";
        if (lastUnverified == UnverifiedNoTestsRan)
        {
            ParkStep(plan, step, $"Its check ran no test after {rounds}.", "no-tests-ran",
                reason: $"Step {step.Id} ({step.Title}) was not done: after {rounds} its check still passed without running a single test (the test runner counted only a file, " +
                        "which happens when the test file registers no test), so the check cannot show that the work is done. Retry it, or skip it if the work is already in place.");
            return false;
        }

        if (lastUnverified is not null)
        {
            bool testsOnly = lastUnverified == UnverifiedNoTest;
            ParkStep(plan, step, $"Did not do its work after {rounds}: {(testsOnly ? "no test file it names changed" : "nothing it names changed")}.", "no-change",
                reason: $"Step {step.Id} ({step.Title}) was not done: after {rounds} it still changed {(testsOnly ? "none of the test files it names" : "none of the files it names")}, " +
                        "and its check passes without that, so the check cannot show that the work is done. Retry it, or skip it if the work is already in place.");
            return false;
        }

        if (lastReviewFailed)
        {
            ParkStep(plan, step, $"Its reviewer was not satisfied after {rounds}.{kept}", "review",
                reason: $"Step {step.Id} ({step.Title}) passed its check but a reviewer that did not write the code still found its requirements unmet after {rounds}: " +
                        $"every rung of the repair ladder had its rounds.{kept} The reviewer's findings are in the run log. Retry it, or skip it if the reviewer is wrong.");
            return false;
        }

        if (SuspectTestNames(plan, step, lastCheckOutput) is { } suspects)
        {
            string note = $"The model says the fixed test {suspects} is wrong, and the fixed tests still fail: look at the test.";
            ParkStep(plan, step, note, "test-suspect",
                reason: $"Step {step.Id} ({step.Title}) did not pass its check after {rounds}. {note}{kept}");
            return false;
        }

        ParkStep(plan, step, $"Did not pass its check after {rounds}.{kept}", "check-kept-failing",
            reason: $"Step {step.Id} ({step.Title}) did not pass its check after {rounds}: every rung of the repair ladder had its rounds.{kept}");
        return false;
    }

    // The attempt was counted when its model call began. A first message that failed before any verdict never started a
    // conversation, so the attempt is given back; a later round of a conversation that did start keeps its attempt.
    private void GiveBackAttempt(string planId, int stepId, int attempt, IStepSession session)
    {
        if (session.HasHistory)
        {
            return;
        }

        _store.Update(planId, current => FleetPlanStoreSteps.With(current, stepId, currentStep => currentStep with
        {
            Attempts = Math.Min(currentStep.Attempts, attempt - 1)
        }));
    }

    private PlanRunEvent[] StepEventsSinceRetry(string planId, int stepId)
    {
        PlanRunEvent[] events = (_store.Get(planId)?.Events ?? []).ToArray();
        return events.Skip(RetryBoundary(events, stepId) + 1).Where(runEvent => runEvent.StepId == stepId).ToArray();
    }

    private bool HubRescueAllowed(PlanRecord plan, PlanStep step) =>
        PlanRecoveryScope.AllowsHubRescue(plan, step) && (_modeService is null || _modeService.Mode == FleetMode.Aggressive);

    private string? HubModeDeclineReason(PlanRecord plan, PlanStep step) =>
        PlanRecoveryScope.AllowsHubRescue(plan, step) && _modeService?.Mode == FleetMode.Conservative
            ? "Hub rescue is off while Hub mode is Conservative."
            : null;

    // The hub rung needs recovery scope, an enabled Hub mode, a hub that is not already the model, and a conversation to continue.
    private bool HubRungAvailable(PlanStep step, string? hub, bool hubAllowed, string? modelNode)
    {
        if (!hubAllowed || hub is null)
        {
            return false;
        }

        if (string.Equals(modelNode, hub, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Without routing information the tier tells: a heavy step on the hub's own tier already runs there.
        return modelNode is not null ||
               !string.Equals(_fleetOptions?.Nodes.SingleOrDefault(node => node.Fallback)?.Tier, step.Tier, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HubReadyAsync(string hub, CancellationToken cancellationToken)
    {
        if (_healthMonitor is null)
        {
            return false;
        }

        try
        {
            return (await _healthMonitor.GetNodeAsync(hub, cancellationToken, forceProbe: true, forceInferenceProbe: true)).Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not check hub model {Machine} before climbing the repair ladder.", hub);
            return false;
        }
    }

    /// <summary>
    /// Acts on a ladder decision that is not "another round": climbs to rung 2 (the same conversation on the hub) or rung 3
    /// (a fresh conversation), or gives up. The hub is only used when recovery scope and Hub mode allow it and its inference
    /// answers right now; otherwise rung 3 is a fresh conversation on the workers, and the reason says so.
    /// </summary>
    private async Task<(int Rung, int RoundsInRung, string? ModelOverride, bool ConversationOver, bool GaveUp, string? Handover)> MoveAsync(
        PlanRecord plan,
        PlanStep step,
        RepairDecision decision,
        int rung,
        int attempt,
        string tier,
        ModelAttemptResult model,
        string? modelOverride,
        string? workspaceOverride,
        string? hub,
        bool hubAllowed,
        CancellationToken cancellationToken)
    {
        if (decision.Move == RepairMove.GiveUp)
        {
            return (rung, 0, modelOverride, true, true, null);
        }

        if (decision.Move == RepairMove.ContinueOnHub && !await HubReadyAsync(hub!, cancellationToken))
        {
            decision = new RepairDecision(RepairMove.FreshConversation, RepairLadder.FreshRung,
                $"{decision.Why}; the hub did not answer a probe, so this stays on the workers");
        }

        string? newOverride = await ClimbAsync(plan, step, decision, rung, attempt, tier, modelOverride, workspaceOverride ?? model.Machine, model.ModelNode, hub, hubAllowed, cancellationToken);
        string? handover = decision.Move == RepairMove.ContinueOnHub
            ? "A more capable model (the hub) has taken over this conversation because the earlier rounds did not get the check passing. Find the root cause first, then fix it."
            : null;
        return (decision.ToRung, 0, newOverride, decision.Move == RepairMove.FreshConversation, false, handover);
    }

    // Writes the climb into the run log and returns the model machine the new rung uses.
    private async Task<string?> ClimbAsync(
        PlanRecord plan,
        PlanStep step,
        RepairDecision decision,
        int fromRung,
        int attempt,
        string tier,
        string? modelOverride,
        string? workspace,
        string? currentModel,
        string? hub,
        bool hubAllowed,
        CancellationToken cancellationToken)
    {
        string? target = modelOverride;
        if (decision.Move == RepairMove.ContinueOnHub)
        {
            target = hub;
        }
        else if (decision.Move == RepairMove.FreshConversation)
        {
            // Rung 3 uses the hub when the plan allows it and it answers; otherwise whatever machine routing gives.
            bool onHub = string.Equals(modelOverride, hub, StringComparison.OrdinalIgnoreCase) && modelOverride is not null;
            if (hubAllowed && hub is not null && (onHub || await HubReadyAsync(hub, cancellationToken)))
            {
                target = hub;
            }
            else if (onHub)
            {
                target = null;
            }
        }

        string where = target ?? currentModel ?? "the machine routing picks";
        string detail = $"Climbing from rung {fromRung} ({RepairLadder.Name(fromRung)}) to rung {decision.ToRung} ({RepairLadder.Name(decision.ToRung)}) on {where}: {decision.Why}.";
        if (hub is not null && HubModeDeclineReason(plan, step) is { } modeReason)
            detail += $" {modeReason}";
        _logger.LogInformation("Plan {PlanId} step {StepId}: {Detail}", plan.Id, step.Id, detail);
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RungChanged, tier, node: target, detail: detail,
            modelNode: target, workspaceNode: workspace, rung: decision.ToRung);
        return target;
    }

    private string BuildRepairMessage(PlanRecord plan, PlanStep step, string? failure, int round, RoundOutcome? last, string? handover,
        RollbackNote? rollback = null)
    {
        IReadOnlyList<string> current = last is not null
            ? last.Signature.Items
            : PlanFailure.FailureSignature(failure).Items;
        IReadOnlyList<string> previous = last is null ? [] : RepairMessages.FailingNames(last.PreviousSignature);
        return RepairMessages.Build(plan, step, failure ?? string.Empty, round, previous, current, last?.SameAsPrevious ?? false,
            last is { FilesKnown: true } ? last.ChangedFiles : null, handover, rollback);
    }

    /// <summary>The best state the step's project has been in, as the approved check saw it, and the files as they were then.</summary>
    private sealed record StepCheckpoint(
        int Rung,
        int Round,
        RoundScore Score,
        string Signature,
        IReadOnlyList<string> Names,
        string? Output,
        PlanTools.WorkSnapshot Snapshot);

    // Puts the project back to the best round's files after a round that left it worse, and writes that into the run log.
    // Null when the files could not be put back (the log then says why); the step goes on from the files as they are.
    private async Task<RollbackNote?> RollBackAsync(
        PlanRecord plan,
        PlanStep step,
        int attempt,
        string tier,
        ModelAttemptResult model,
        int rung,
        int round,
        RoundScore worse,
        RoundOutcome outcome,
        StepCheckpoint best,
        CancellationToken cancellationToken)
    {
        PlanTools.WorkRestore restored = await _tools.RestoreToSnapshotAsync(plan, best.Snapshot, cancellationToken);
        if (restored.Refusal is not null)
        {
            _logger.LogWarning("Plan {PlanId} step {StepId}: round {Round} left the step worse but could not be undone: {Reason}.",
                plan.Id, step.Id, round, restored.Refusal);
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RoundRolledBack, tier, node: model.ModelNode,
                detail: $"Round {round} left {worse.Describe()}, worse than round {best.Round}'s {best.Score.Describe()}, but it could not be undone ({restored.Refusal}). The step goes on from the files as they are.",
                modelNode: model.ModelNode, workspaceNode: model.Machine, failureClass: "Refused", rung: rung, round: round);
            return null;
        }

        string[] newFailures = outcome.Signature.Items.Except(best.Names, StringComparer.OrdinalIgnoreCase).ToArray();
        string left = restored.Left.Count == 0
            ? string.Empty
            : $" {restored.Left.Count} changed file(s) could not be put back: {string.Join(", ", restored.Left.Take(5))}.";
        string files = restored.Restored.Count == 0 ? "nothing needed putting back" : string.Join(", ", restored.Restored.Take(8)) + (restored.Restored.Count > 8 ? ", ..." : string.Empty);
        string detail = $"Round {round} (rung {rung}) left {worse.Describe()} where round {best.Round} had {best.Score.Describe()}, so it was undone: " +
                        $"{files}, as they were after round {best.Round}.{left}";
        _logger.LogInformation("Plan {PlanId} step {StepId}: {Detail}", plan.Id, step.Id, detail);
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RoundRolledBack, tier, node: model.ModelNode, detail: detail,
            modelNode: model.ModelNode, workspaceNode: model.Machine, failureClass: "RolledBack", failureSignature: best.Signature,
            failureSignatureSize: best.Score.Failures, filesChanged: restored.Restored.Count, rung: rung, round: round,
            changedFiles: restored.Restored);
        return new RollbackNote(round, worse, best.Round, best.Score, newFailures, restored.Restored);
    }

    private static string NoChangeReason(RoundOutcome outcome)
    {
        string apart = outcome.ChangedFiles.Count > 0 && outcome.MeaningfulFiles.Count == 0 ? " apart from lock or generated files" : string.Empty;
        return outcome.NoOp
            ? $"the last round made no edit and changed no file{apart}"
            : $"the last round left the same failures and changed no file{apart}";
    }

    private static bool StepNames(PlanRecord plan, PlanStep step, string relative)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return false;
        }

        string full = Path.GetFullPath(Path.Combine(plan.WorkingDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        return FleetPlanContext.DeclaredPaths(plan, step).Any(declared => FleetPlanContext.Names(declared, full));
    }

    /// <summary>What one round did, as the repair ladder reads it.</summary>
    private sealed record RoundOutcome(
        FailureClass Class,
        FailureFingerprint Signature,
        string? PreviousSignature,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> MeaningfulFiles,
        bool FilesKnown,
        bool EditToolCalled)
    {
        public bool SameAsPrevious => PreviousSignature is not null && Signature.Size > 0 &&
            string.Equals(PreviousSignature, Signature.Value, StringComparison.Ordinal);

        // Where git could not tell, an edit tool call stands for "changed something". Lock files and build caches do not count.
        public bool Changed => FilesKnown ? MeaningfulFiles.Count > 0 : EditToolCalled;

        public bool NoOp => !EditToolCalled && !Changed;

        public bool Stall => SameAsPrevious && !Changed;

        public bool NoChange => NoOp || Stall;
    }

    /// <summary>A model call is stopped sooner than the usual attempt timeout when the step or the plan is about to run out of time.</summary>
    /// <param name="Reason">Said in the run log: why the call was stopped when it was ("the step's working time ran out").</param>
    private sealed record CallLimit(TimeSpan Duration, string Reason);

    private CallLimit? LimitFor(PlanRecord plan, TimeSpan worked)
    {
        CallLimit? limit = null;
        TimeSpan left = _stepClock - worked;
        if (left < _attemptTimeout)
        {
            limit = new CallLimit(left, "the step's working time ran out");
        }

        if (plan.RunDeadlineUtc is { } deadline)
        {
            TimeSpan untilDeadline = deadline - _utcNow();
            if (untilDeadline < (limit?.Duration ?? _attemptTimeout))
            {
                limit = new CallLimit(untilDeadline, "the plan's run deadline arrived");
            }
        }

        return limit is null ? null : limit with { Duration = limit.Duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : limit.Duration };
    }

    /// <summary>The conversation a model call belongs to, and where on the repair ladder it is.</summary>
    /// <param name="RepairMessage">The follow-up to send; null when the conversation has to be told the whole task.</param>
    /// <param name="Brief">What the rounds before this conversation tried, for a fresh conversation after them.</param>
    private sealed record RoundContext(IStepSession Session, int Rung, int Round, string? RepairMessage = null, string? Brief = null);

    private async Task<string?> ReconcileInterruptedWorkspaceAsync(PlanRecord plan, PlanStep step, CancellationToken cancellationToken)
    {
        if (IsTestsStep(step))
            return $"Step {step.Id} was interrupted while writing tests. The original change baseline is unavailable; review it before retrying.";

        PlanRunEvent? staged = (plan.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.WorkspaceStaged && runEvent.Attempt is not null)
            .OrderByDescending(runEvent => runEvent.AtUtc)
            .FirstOrDefault();
        if (staged?.Attempt is not { } attempt || _workerWorkspaces is null)
            return $"Step {step.Id} was interrupted in a worker workspace without a recoverable staged baseline. The workspace was preserved for review.";

        WorkerWorkspaceBaseline? baseline = _store.GetWorkerBaseline(plan.Id, step.Id, attempt);
        if (baseline is null)
            return $"Step {step.Id} was interrupted before its staged baseline could be read. The worker workspace was preserved for review.";

        WorkerWorkspaceResume resumed;
        try
        {
            resumed = await _workerWorkspaces.ResumeInterruptedAsync(plan, step, baseline, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not reconcile interrupted worker {Machine} for plan {PlanId} step {StepId}.", baseline.Machine, plan.Id, step.Id);
            return $"Step {step.Id} worker recovery could not compare its staged files ({exception.GetType().Name}: {exception.Message}). The workspace was preserved for review.";
        }

        WorkerWorkspaceRecoveryAnalysis analysis = resumed.Analysis;
        if (!analysis.Safe)
        {
            string conflicts = string.Join(", ", analysis.Conflicts.Take(20));
            return $"Step {step.Id} was interrupted with worker changes that conflict with the hub or are outside the step's declared files: {conflicts}. Nothing was overwritten; review the worker workspace.";
        }

        string detail = analysis.ChangedFiles.Count == 0
            ? "The interrupted worker files match their saved staged baseline. The workspace was safely re-staged from the hub."
            : $"Recovered the interrupted worker edits and re-staged the hub snapshot: {string.Join(", ", analysis.ChangedFiles.Take(20))}.";
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkspaceReconciled, step.Tier,
            node: baseline.Machine, detail: detail, modelNode: staged.ModelNode, workspaceNode: baseline.Machine);

        if (analysis.ChangedFiles.Count == 0)
        {
            resumed.Session?.Dispose();
            _store.DeleteWorkerBaseline(plan.Id, step.Id, attempt);
            if (!step.RetrySafe && !RoundMadeNoChangingCall(_store.Get(plan.Id) ?? plan, step, attempt))
                return $"Step {step.Id} has no worker edits to recover, but it is not marked safe to replay after an uncertain outcome. Review it before retrying.";
            _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, currentStep => currentStep with
            {
                Status = StepStatus.Pending,
                Attempts = Math.Max(0, attempt - 1)
            }));
            return null;
        }

        IWorkerWorkspaceSession session = resumed.Session!;
        try
        {
            if (_recorder?.ResolveContext(_store.Get(plan.Id) ?? plan) is { } contextId)
                _contexts[plan.Id] = contextId;
            StepCompletion check;
            bool final = step.Id == plan.Steps.Max(candidate => candidate.Id);
            using (session.Enter())
            {
                if (final)
                    _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FinalValidationStarted, step.Tier,
                        detail: $"Runner is rechecking `{step.Verify}` on reconciled worker edits.",
                        modelNode: staged.ModelNode, workspaceNode: baseline.Machine);
                check = await _tools.TryCompleteStepAsync(plan.Id, step.Id,
                    "Recovered worker edits after a backend restart.", cancellationToken, FilesToCreate(plan, step), deferCommit: true);
                await SyncToHubWithReconnectAsync(plan, step, attempt, session, cancellationToken, cancellationToken);
            }
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkspaceSynced, step.Tier, baseline.Machine,
                "Synced the reconciled worker workspace after restart verification.",
                modelNode: staged.ModelNode, workspaceNode: baseline.Machine);
            _store.DeleteWorkerBaseline(plan.Id, step.Id, attempt);
            await RefreshFromHubWithReconnectAsync(plan, step, attempt, session, cancellationToken, cancellationToken);

            var recoveredAttempt = new ModelAttemptResult(step.Id, "Recovered worker edits after a backend restart.", null,
                ShouldVerify: true, Machine: baseline.Machine, ModelNode: staged.ModelNode,
                EditToolCalled: true);
            long? verification = RecordCheck(plan, step, attempt, check, staged.ModelNode);
            // The recovered edits are a round of the step's current rung: a failed check counts against its budget.
            RepairPosition position = RepairLadder.PositionFrom(StepEventsSinceRetry(plan.Id, step.Id));
            await RecordRoundClassifiedAsync(plan, step, attempt, recoveredAttempt,
                check.Output ?? check.Message, check.Done, analysis.ChangedFiles, position.Rung, position.RoundsInRung + 1);
            if (check.Done)
            {
                _tools.CommitStepDone(plan.Id, step.Id, "Recovered worker edits.");
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.CheckPassed, step.Tier,
                    detail: final ? $"Final validation passed: `{step.Verify}`." : $"`{step.Verify}` passed after worker recovery.",
                    modelNode: staged.ModelNode, workspaceNode: baseline.Machine);
                if (final)
                    _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FinalValidationPassed, step.Tier,
                        detail: $"`{step.Verify}` passed on the reconciled worker snapshot.",
                        modelNode: staged.ModelNode, workspaceNode: baseline.Machine);
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.StepDone, step.Tier,
                    detail: StepDoneDetail(plan, step, attempt, check.Restored));
                RecordStepDone(plan, step, verification, staged.ModelNode ?? baseline.Machine);
                return null;
            }

            string failure = WithStrayNote(plan, step, check.Output ?? check.Message);
            if (EnvironmentBlockReason(failure) is { } environmentReason)
            {
                return $"Environment issue: {environmentReason} Automatic retries stopped because the selected worker workspace cannot execute or verify this step. Configure or repair that worker, then retry the step.\n\nCheck output:\n{failure}";
            }

            _store.AddEvent(plan.Id, step.Id, attempt,
                final ? RunEventKind.FinalValidationFailed : RunEventKind.CheckFailed,
                step.Tier, node: staged.ModelNode ?? baseline.Machine, detail: failure,
                modelNode: staged.ModelNode, workspaceNode: baseline.Machine);
            if (!step.RetrySafe)
                return $"Step {step.Id} is not marked safe to replay after restart verification failed. The recovered worker edits were kept for review: {failure}";
            _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, currentStep => currentStep with
            {
                Status = StepStatus.Pending,
                Attempts = attempt,
                Note = failure
            }));
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not verify reconciled worker edits for plan {PlanId} step {StepId}.", plan.Id, step.Id);
            return $"Step {step.Id} worker edits were recovered but restart verification failed ({exception.GetType().Name}: {exception.Message}). The workspace was preserved for review.";
        }
        finally
        {
            session.Dispose();
        }
    }

    // The run log is the only record of an interrupted round's tool calls. The round is known to have made no changing call only
    // when its staging left the logged mark and no changing call was logged after that mark. A round without the mark is unknown
    // (it ran before this record existed, or it never got as far as staging) and stays blocked for a step not marked safe to replay.
    private static bool RoundMadeNoChangingCall(PlanRecord plan, PlanStep step, int attempt)
    {
        PlanRunEvent[] events = (plan.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Attempt == attempt)
            .ToArray();
        int mark = Array.FindLastIndex(events, runEvent => runEvent.Kind == ToolLoggedEvent);
        return mark >= 0 && !events.Skip(mark + 1).Any(runEvent => runEvent.Kind == ChangingToolEvent);
    }

    // A package manager's lock file or a build cache rewritten as a side effect is not progress on the step.
    private IReadOnlyList<string> MeaningfulFiles(PlanRecord plan, PlanStep step, IEnumerable<string> changedFiles) =>
        changedFiles.Where(file => !RepairLadder.IsIncidentalFile(file) || StepNames(plan, step, file)).ToArray();

    private const string UnverifiedNoChange = "no-change";
    private const string UnverifiedNoTest = "no-test";
    private const string UnverifiedNoTestsRan = "no-tests-ran";

    /// <summary>
    /// Why a passing check does not show that the step's work is done, or null when it does. First: the step names files to
    /// change and no round of it (this one or an earlier one, also before a retry) changed any, so the check passes on the
    /// project as it was. Then: the step names test files and none of them changed in any round, so the check can only be
    /// running the tests as they were, which say nothing about the new work. The last step of a longer plan is left out: it
    /// is the whole-project check, and its files are only the ones it may touch to fix what that check finds.
    /// </summary>
    private string? UnverifiedPassReason(PlanRecord plan, PlanStep step, ModelAttemptResult model, IReadOnlyList<string> changedFiles, bool isFinalValidation,
        string? checkOutput)
    {
        // A check that ran no test says nothing about the work, whatever the step names (see CheckVacuity).
        if (CheckVacuity.NoTestsRan(checkOutput))
        {
            return UnverifiedNoTestsRan;
        }

        if (step.Files.Count == 0 || (isFinalValidation && plan.Steps.Count > 1))
        {
            return null;
        }

        IReadOnlyList<string> meaningful = MeaningfulFiles(plan, step, changedFiles);
        PlanRunEvent[] earlier = (_store.Get(plan.Id)?.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.RoundClassified).ToArray();

        // An edit tool call counts as a change even where git saw none (the same content written again is rare, and a
        // project that git cannot read has no other sign); where git cannot tell, a model that ran tools may have edited
        // through a shell command, so only a round with no tool call at all is held to account. Rounds before this one (and
        // before a retry) may have made the changes that this round's check now passes on.
        bool edited = model.EditToolCalled || (model.WorkBefore is not null && meaningful.Count > 0) ||
                      earlier.Any(runEvent => runEvent.EditToolCalled == true ||
                          (runEvent.ChangedFiles is { } files ? MeaningfulFiles(plan, step, files).Count > 0 : (runEvent.FilesChanged ?? 0) > 0));
        if (!edited && !(model.WorkBefore is null && model.ToolCalls > 0))
        {
            return UnverifiedNoChange;
        }

        // The test files the step names must change too, where git can say what changed.
        if (model.WorkBefore is not null && step.Files.Any(IsTestPath))
        {
            bool testChanged = meaningful
                .Concat(earlier.SelectMany(runEvent => runEvent.ChangedFiles ?? []))
                .Any(file => IsTestPath(file) && StepNames(plan, step, file));
            if (!testChanged)
            {
                return UnverifiedNoTest;
            }
        }

        return null;
    }

    // A test file by where it lives or how it is named (tests/, __tests__, foo.test.ts, foo_spec.rb, FooTests.cs, test_foo.py).
    internal static bool IsTestPath(string path) => TestPath().IsMatch(path.Replace('\\', '/'));

    [GeneratedRegex(@"(^|/)(tests?|__tests__|specs?)(/|$)|[._-](test|spec)s?\.[^/.]+$|(?-i:[a-z0-9](Test|Tests|Spec)\.[^/.]+$)|(^|/)test[_-][^/]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TestPath();

    private static string UnverifiedPassMessage(PlanStep step, string reason) => reason == UnverifiedNoTestsRan
        ? "Not accepted: the check passed, but it ran no test. The test runner counted only a file as a test, which happens when the test file does not " +
          "register any test with it: a script that prints its results does not fail when a result is wrong. Write the tests as test('name', () => { ... }) " +
          "(or it(...)) from the test runner the project uses, each asserting with its assert module, in the test file the step names, and make the whole check pass."
        : reason == UnverifiedNoTest
            ? $"Not accepted: this step names test files ({string.Join(", ", step.Files.Where(IsTestPath).Take(6))}), but none of them was changed. " +
          "Its check passes on the tests as they were, so it cannot show that the new work behaves as the step asks. " +
          "Add or extend a test in one of those files that fails without your change and passes with it, and make the whole check pass."
        : $"Not accepted: this step names files to change ({string.Join(", ", step.Files.Take(6))}), but nothing was changed in them or anywhere else, " +
          "and its check already passes on the project as it was, so the check cannot show that the work is done. " +
          "Do the work the step describes. Where the step names a test file, add or extend a test there that fails without your change and passes with it.";

    private Task<RoundOutcome> RecordRoundClassifiedAsync(
        PlanRecord plan,
        PlanStep step,
        int attempt,
        ModelAttemptResult model,
        string failureOutput,
        bool passed,
        IReadOnlyList<string> changedFiles,
        int? rung = null,
        int? round = null,
        int? durationSeconds = null)
    {
        IReadOnlyList<string> meaningful = MeaningfulFiles(plan, step, changedFiles);
        int filesChanged = meaningful.Count;
        PlanRecord current = _store.Get(plan.Id) ?? plan;
        FailureRound[] history = (current.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.FailureSignatureSize > 0 &&
                ((runEvent.Kind == RunEventKind.RoundClassified && !string.Equals(runEvent.FailureClass, "Passed", StringComparison.Ordinal)) ||
                 (runEvent.Kind == RunEventKind.RoundRolledBack && string.Equals(runEvent.FailureClass, "RolledBack", StringComparison.Ordinal))))
            .Select(runEvent => new FailureRound(runEvent.FailureSignature, runEvent.FailureSignatureSize ?? 0,
                runEvent.FilesChanged ?? 0, runEvent.EditToolCalled ?? false))
            .ToArray();
        FailureFingerprint signature = passed ? new FailureFingerprint(string.Empty, 0, []) : PlanFailure.FailureSignature(failureOutput);
        FailureClass classification = passed
            ? FailureClass.Unknown
            : model.WorkspaceFailure is not null && model.ReroutableWorkspaceFailure
                ? FailureClass.Infra
                : PlanFailure.Classify(model.FailureException, failureOutput, history, filesChanged, model.EditToolCalled);
        string failureClass = passed ? "Passed" : classification.ToString();
        string files = changedFiles.Count == 0
            ? "0"
            : $"{changedFiles.Count} ({string.Join(", ", changedFiles.Take(5))}{(changedFiles.Count > 5 ? ", ..." : string.Empty)})" +
              (filesChanged == 0 ? ", only lock or generated files, so not counted as progress" : string.Empty);
        string detail = $"Round classified: {failureClass}; signature size: {signature.Size}; files changed: {files}; " +
            $"tool calls: {model.ToolCalls}; edit tool called: {(model.EditToolCalled ? "yes" : "no")}.";
        FleetNodeDefinition? modelDefinition = null;
        if (model.ModelNode is not null && _fleetOptions is not null)
        {
            _fleetOptions.TryGetNode(model.ModelNode, out modelDefinition);
        }

        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RoundClassified, step.Tier,
            node: model.ModelNode ?? model.Machine, detail: detail,
            modelNode: model.ModelNode, workspaceNode: model.Machine,
            failureClass: failureClass, failureSignature: signature.Value, failureSignatureSize: signature.Size,
            filesChanged: changedFiles.Count, toolCalls: model.ToolCalls, editToolCalled: model.EditToolCalled,
            rung: rung, round: round, changedFiles: changedFiles, durationSeconds: durationSeconds,
            model: modelDefinition?.Model, contextCeiling: modelDefinition?.ContextLength,
            loopGuardNotes: model.LoopGuardNotes);
        string? previous = history.LastOrDefault(earlier => !string.IsNullOrWhiteSpace(earlier.Signature))?.Signature;
        return Task.FromResult(new RoundOutcome(classification, signature, previous, changedFiles, meaningful,
            FilesKnown: model.WorkBefore is not null, model.EditToolCalled));
    }

    /// <summary>The project files a model call changed (paths inside the project) and the project as it stands after it.</summary>
    /// <param name="After">The changed and new files now, for a checkpoint; null when git could not tell.</param>
    private sealed record WorkChange(IReadOnlyList<string> Files, PlanTools.WorkSnapshot? After);

    // The files git sees as changed or new before the call against the same files after it, and the ones it deleted. Empty
    // when git could not tell (see PlanTools.SnapshotWorkAsync).
    private async Task<WorkChange> ChangedFilesSinceAsync(PlanRecord plan, PlanTools.WorkSnapshot? before, CancellationToken cancellationToken)
    {
        if (before is null) return new WorkChange([], null);
        PlanRecord fresh = _store.Get(plan.Id) ?? plan;
        PlanTools.WorkSnapshot? after = await _tools.SnapshotWorkAsync(fresh, cancellationToken);
        if (after is null) return new WorkChange([], null);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] paths = before.Files.Keys.Concat(after.Files.Keys).Distinct(comparer).ToArray();
        string[] files = paths
            .Where(path =>
                !before.Files.TryGetValue(path, out byte[]? previous) ||
                !after.Files.TryGetValue(path, out byte[]? current) ||
                !previous.AsSpan().SequenceEqual(current))
            .Concat(after.Deleted.Where(path => !before.Deleted.Contains(path)))
            .Distinct(comparer)
            .Select(path => string.IsNullOrWhiteSpace(fresh.WorkingDirectory)
                ? path
                : Path.GetRelativePath(fresh.WorkingDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new WorkChange(files, after);
    }

    private int CountClassifiedRounds(string planId, int stepId, FailureClass failureClass)
    {
        PlanRunEvent[] events = (_store.Get(planId)?.Events ?? []).ToArray();
        return events.Skip(RetryBoundary(events, stepId) + 1).Count(runEvent => runEvent.StepId == stepId &&
            runEvent.Kind == RunEventKind.RoundClassified &&
            string.Equals(runEvent.FailureClass, failureClass.ToString(), StringComparison.Ordinal));
    }

    private int CountWaitingEvents(string planId, int stepId)
    {
        PlanRunEvent[] events = (_store.Get(planId)?.Events ?? []).ToArray();
        return events.Skip(RetryBoundary(events, stepId) + 1).Count(runEvent => runEvent.StepId == stepId && runEvent.Kind == RunEventKind.Waiting);
    }

    private TimeSpan DelayWithinRunDeadline(PlanRecord plan, TimeSpan requested)
    {
        if (plan.RunDeadlineUtc is not { } deadline) return requested;
        TimeSpan remaining = deadline - _utcNow();
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return requested > remaining ? remaining : requested;
    }

    private Task DelayBeforeRetryAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _delayAsync(delay, cancellationToken);

    /// <summary>How often a long outage pause looks whether the machine is back.</summary>
    internal static readonly TimeSpan WakeCheckInterval = TimeSpan.FromMinutes(1);

    // A real small probe of a machine, the same one that follows an outage pause. A probe that fails to run is a probe that failed.
    private async Task<NodeHealthSnapshot> ProbeMachineAsync(string machine, CancellationToken cancellationToken)
    {
        try
        {
            return await _healthMonitor!.GetNodeAsync(machine, cancellationToken, forceProbe: true, forceInferenceProbe: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not probe model machine {Machine} during a plan outage wait.", machine);
            return new NodeHealthSnapshot(machine, string.Empty, false, false, false, DateTimeOffset.UtcNow, exception.Message);
        }
    }

    /// <summary>
    /// Waits out an outage pause. When the machine is down at the start of the pause (its probe says so), a long pause is cut into
    /// minutes and the machine is asked between them, so one that comes back two seconds in does not leave the step idle for
    /// fifteen minutes. A machine whose probe passes from the start (the real call failed, the probe did not) keeps the whole pause:
    /// asking it every minute would only hammer it. Returns the probe that ended the pause early, or null when all of it was waited.
    /// </summary>
    private async Task<NodeHealthSnapshot?> WaitUnlessItAnswersAsync(TimeSpan delay, string? machine, CancellationToken cancellationToken)
    {
        if (_healthMonitor is null || machine is null || delay <= WakeCheckInterval ||
            (await ProbeMachineAsync(machine, cancellationToken)).Ready)
        {
            await DelayBeforeRetryAsync(delay, cancellationToken);
            return null;
        }

        TimeSpan left = delay;
        while (left > WakeCheckInterval)
        {
            await DelayBeforeRetryAsync(WakeCheckInterval, cancellationToken);
            left -= WakeCheckInterval;
            NodeHealthSnapshot probe = await ProbeMachineAsync(machine, cancellationToken);
            if (probe.Ready)
            {
                return probe;
            }
        }

        await DelayBeforeRetryAsync(left, cancellationToken);
        return null;
    }

    private async Task<(string? ModelOverride, int Waits)> WaitForInfrastructureRecoveryAsync(
        PlanRecord plan,
        PlanStep step,
        string tier,
        string? failedModel,
        string? workspace,
        int attempt,
        string? failure,
        int waits,
        CancellationToken cancellationToken,
        bool allowRoute = true)
    {
        while (true)
        {
            if (plan.RunDeadlineUtc is { } deadline && _utcNow() >= deadline)
                return (null, waits);

            int waitNumber = ++waits;
            TimeSpan delay = DelayWithinRunDeadline(plan, _transientDelay(waitNumber - 1));
            string failedNode = failedModel ?? "the selected model machine";
            _logger.LogWarning("Plan {PlanId} step {StepId}: infrastructure failure on {Machine}; waiting {Delay} without spending an attempt.",
                plan.Id, step.Id, failedNode, delay);
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.Waiting, tier, node: failedModel,
                detail: $"Infra failure on {failedNode}: {Summarize(failure ?? "unknown failure")}. Waiting {Duration(delay)} (wait {waitNumber}) before probing and retrying; this does not count as an attempt.",
                modelNode: failedModel, workspaceNode: workspace);
            NodeHealthSnapshot? early = await WaitUnlessItAnswersAsync(delay, failedModel, cancellationToken);

            if (plan.RunDeadlineUtc is { } runDeadline && _utcNow() >= runDeadline)
                return (null, waits);
            if (_healthMonitor is null || failedModel is null)
                return (null, waits);

            NodeHealthSnapshot probe = early ?? await ProbeMachineAsync(failedModel, cancellationToken);
            if (probe.Ready)
            {
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.InferenceProbePassed, tier,
                    node: failedModel,
                    detail: early is null
                        ? $"A small inference probe passed on {failedModel}; retrying the model attempt now."
                        : $"{failedModel} answered a small inference probe again before the pause ({Duration(delay)}) was over; retrying the model attempt now.",
                    modelNode: failedModel, workspaceNode: workspace);
                return (failedModel, waits);
            }

            if (plan.RunDeadlineUtc is { } afterProbeDeadline && _utcNow() >= afterProbeDeadline)
                return (null, waits);
            if (allowRoute)
            {
                string? route = await RouteFailedModelAsync(plan, step, tier, failedModel, workspace, attempt, cancellationToken);
                if (route is not null) return (route, waits);
            }
        }
    }

    private async Task<string?> RouteFailedModelAsync(
        PlanRecord plan,
        PlanStep step,
        string tier,
        string? failedModel,
        string? workspace,
        int attempt,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? recentlyFailed = null)
    {
        (string? alternate, string detail) = await SelectRetryMachineAsync(
            plan, step, tier, cancellationToken, modelRoute: true);
        if (alternate is not null && recentlyFailed?.Contains(alternate) == true)
        {
            alternate = null;
        }

        if (alternate is not null)
        {
            RecordModelRecoveryRoute(plan, step, tier, attempt, alternate, workspace,
                $"Model recovery moved from {failedModel ?? "the selected model machine"} to {alternate}. {detail}");
            return alternate;
        }

        if (_fleetOptions is null || _healthMonitor is null)
        {
            return null;
        }

        FleetNodeDefinition? hub = _fleetOptions.Nodes.SingleOrDefault(node => node.Fallback);
        if (hub is null || string.Equals(hub.Name, failedModel, StringComparison.OrdinalIgnoreCase) || recentlyFailed?.Contains(hub.Name) == true) return null;
        if (!HubRescueAllowed(plan, step))
        {
            if (HubModeDeclineReason(plan, step) is { } reason)
                _logger.LogInformation("Plan {PlanId} step {StepId}: {Reason}", plan.Id, step.Id, reason);
            return null;
        }

        try
        {
            NodeHealthSnapshot health = await _healthMonitor.GetNodeAsync(hub.Name, cancellationToken,
                forceProbe: true, forceInferenceProbe: true);
            if (!health.Ready) return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not check hub model {Machine} for plan {PlanId} step {StepId}.", hub.Name, plan.Id, step.Id);
            return null;
        }

        string hubDetail = $"Model recovery moved from {failedModel ?? "the selected model machine"} to {hub.Name}. No ready same-tier model alternate was available; recovery scope allows the hub model and inference is ready. The worker workspace remains on {workspace ?? "the selected worker"}.";
        RecordModelRecoveryRoute(plan, step, tier, attempt, hub.Name, workspace, hubDetail);
        return hub.Name;
    }

    private void RecordModelRecoveryRoute(PlanRecord plan, PlanStep step, string tier, int attempt,
        string model, string? workspace, string detail) =>
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RecoveryRouted, tier,
            node: model, detail: detail, modelNode: model, workspaceNode: workspace);

    private void ParkForUnknown(PlanRecord plan, PlanStep step, int attempt, Exception? exception, string? failure)
    {
        string exceptionName = exception?.GetType().Name ?? "unclassified failure";
        string message = string.IsNullOrWhiteSpace(exception?.Message) ? failure ?? "No failure message was recorded." : exception.Message;
        string reason = $"Automatic retries stopped after five unknown failures ({exceptionName}: {Summarize(message)}). Review the run log before approving another attempt.";
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.ModelFailed, step.Tier,
            detail: reason);
        ParkStep(plan, step, reason, "unknown-failure");
    }

    private bool HasPriorAttempt(string planId, int stepId) =>
        _store.Get(planId)?.Events?.Any(runEvent =>
            runEvent.StepId == stepId && (runEvent.Kind is RunEventKind.AttemptStarted or RunEventKind.AttemptEnded)) == true;

    internal static string? ModelMachineFor(
        FleetMode? mode,
        string? explicitlyPinnedMachine,
        string? workspaceMachine,
        string? aggressiveHubMachine) =>
        explicitlyPinnedMachine ?? (mode == FleetMode.Aggressive ? aggressiveHubMachine : workspaceMachine);

    private async Task<(string? Machine, string Detail)> SelectRetryMachineAsync(
        PlanRecord plan,
        PlanStep step,
        string tier,
        CancellationToken cancellationToken,
        bool modelRoute = false)
    {
        if (_fleetOptions is null || _healthMonitor is null)
        {
            return (null, $"No alternate machine could be checked; normal routing will retry at the requested {tier} tier.");
        }

        PlanRunEvent[] history = (_store.Get(plan.Id)?.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id &&
                (runEvent.Kind is RunEventKind.AttemptStarted or RunEventKind.AttemptEnded or RunEventKind.ModelFailed or RunEventKind.WorkspaceFailed or RunEventKind.WorkspaceStaged or RunEventKind.WorkspaceSynced) &&
                !string.IsNullOrWhiteSpace(modelRoute ? runEvent.ModelNode ?? runEvent.Node : runEvent.WorkspaceNode ?? runEvent.Node))
            .ToArray();
        if (history.Length == 0)
        {
            return (null, $"The previous attempt did not reach a machine that can be identified; normal routing will retry at the requested {tier} tier.");
        }

        var tried = history.Select(runEvent => modelRoute ? runEvent.ModelNode ?? runEvent.Node! : runEvent.WorkspaceNode ?? runEvent.Node!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? previous = modelRoute
            ? history.LastOrDefault()?.ModelNode ?? history.LastOrDefault()?.Node
            : history.LastOrDefault()?.WorkspaceNode ?? history.LastOrDefault()?.Node;
        FleetNodeDefinition[] textNodes = _fleetOptions.Nodes.Where(node => !node.Vision &&
            !string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) &&
            (_workerWorkspaces is null || modelRoute || node.Workspace is not null) &&
            (_workerWorkspaces is null || string.Equals(node.Tier, tier, StringComparison.OrdinalIgnoreCase))).ToArray();
        FleetNodeDefinition[] untried = OrderRetryCandidates(textNodes.Where(node => !tried.Contains(node.Name)), tier);
        FleetNodeDefinition[] candidates = untried.Concat(OrderRetryCandidates(
                textNodes.Where(node => tried.Contains(node.Name) && !string.Equals(node.Name, previous, StringComparison.OrdinalIgnoreCase)), tier))
            .DistinctBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (FleetNodeDefinition candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((await _healthMonitor.GetNodeAsync(candidate.Name, cancellationToken, forceProbe: true,
                    forceInferenceProbe: modelRoute)).Ready)
                {
                    string source = previous is null ? "the previous attempt" : $"{previous}'s previous attempt";
                    return (candidate.Name,
                        $"Automatic retry moved to {candidate.Name} after {source} did not complete successfully; the requested {tier} task tier is unchanged.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not check retry machine {Machine} for plan {PlanId} step {StepId}.", candidate.Name, plan.Id, step.Id);
            }
        }

        string unavailable = candidates.Length == 0
            ? "no different configured text machine exists"
            : "no different text machine is ready";
        string previousRoute = previous is null ? "the previous attempt" : previous;
        return (null,
            $"Automatic retry could not move off {previousRoute}: {unavailable}. Normal routing will retry at the requested {tier} tier.");
    }

    private static FleetNodeDefinition[] OrderRetryCandidates(IEnumerable<FleetNodeDefinition> nodes, string tier) =>
        nodes.OrderByDescending(node => node.Fallback)
            .ThenByDescending(node => string.Equals(node.Tier, tier, StringComparison.OrdinalIgnoreCase))
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<string> SelectWorkspaceMachineAsync(PlanRecord plan, PlanStep step, string tier, CancellationToken cancellationToken)
    {
        if (_fleetOptions is null || _healthMonitor is null)
        {
            throw new InvalidOperationException("Worker workspace unavailable: worker health is not configured.");
        }

        FleetNodeDefinition[] candidates = WorkspaceCandidates(tier);
        // A tier with no worker workspace at all (the hub never has one) borrows the nearest tier that has one. A tier
        // whose workers exist but are not ready is still refused, so the step waits for its own machine as before.
        string[] tiers = WorkerWorkspaceManager.HasOwnWorkspaceWorker(_fleetOptions.Nodes, tier) ? [tier] : [tier, .. NearestTiersFirst(tier)];
        foreach (string candidateTier in tiers)
        {
            foreach (FleetNodeDefinition candidate in candidateTier == tier ? candidates : WorkspaceCandidates(candidateTier))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((await _healthMonitor.GetNodeAsync(candidate.Name, cancellationToken, forceProbe: true)).Ready)
                {
                    string detail = candidateTier == tier
                        ? $"Selected {candidate.Name}: its worker workspace is configured and ready; task tier remains {tier}."
                        : $"Selected {candidate.Name}: no {tier} worker has a configured workspace, so the nearest tier ({candidateTier}) is used; task tier remains {tier}.";
                    _store.AddEvent(plan.Id, step.Id, null, RunEventKind.MachineSelected, tier,
                        null, detail, modelNode: null, workspaceNode: candidate.Name);
                    return candidate.Name;
                }
            }
        }

        string reason = candidates.Length == 0
            ? $"no worker has a configured workspace for the {tier} tier"
            : $"no configured {tier} worker is ready";
        throw new InvalidOperationException($"Worker workspace unavailable: {reason} for step {step.Id} ({step.Title}).");
    }

    private FleetNodeDefinition[] WorkspaceCandidates(string tier) =>
        _fleetOptions!.Nodes
            .Where(node => WorkerWorkspaceManager.IsWorkspaceWorker(node, tier))
            .OrderByDescending(node => node.Fallback)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // The tiers above the requested one, nearest first, then the tiers below it, nearest first.
    private static string[] NearestTiersFirst(string tier)
    {
        string[] ascending = [FleetTiers.Light, FleetTiers.Standard, FleetTiers.Heavy];
        int index = Array.FindIndex(ascending, candidate => string.Equals(candidate, tier, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return [];
        return [.. ascending.Skip(index + 1), .. ascending.Take(index).Reverse()];
    }

    private void ParkForEnvironment(
        PlanRecord plan,
        PlanStep step,
        int attempt,
        string tier,
        string? node,
        string output,
        string cause)
    {
        string reason = $"Environment issue: {cause} Automatic retries stopped because the selected worker workspace cannot execute or verify this step. Configure or repair that worker, then retry the step.\n\nCheck output:\n{output}";
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.CheckFailed, tier, node, reason);
        _logger.LogWarning("Plan {PlanId} step {StepId} stopped early because its check needs unavailable tooling: {Cause}", plan.Id, step.Id, cause);
        ParkStep(plan, step, reason, "environment");
    }

    private void BlockForDeadline(PlanRecord plan, PlanStep step, int attempt, bool deferPlanBlock)
    {
        string deadline = plan.RunDeadlineUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "the plan deadline";
        string reason = $"The plan reached its run deadline ({deadline}). The current step was not started again. Review the run log and approve the plan to continue with a fresh deadline.";
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.RunDeadlineExceeded, step.Tier,
            detail: reason, modelNode: null, workspaceNode: step.Machine);
        _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, currentStep => currentStep with
        {
            Status = StepStatus.Failed,
            Note = reason
        }) with { Status = deferPlanBlock ? PlanStatus.Running : PlanStatus.Blocked });
        if (!deferPlanBlock) AddBlockedEvent(plan.Id, step, reason);
        Notify(plan, "plan-deadline", step, "run-deadline");
    }

    private void ParkForStepClock(PlanRecord plan, PlanStep step, int attempt, TimeSpan worked, StepCheckpoint? best)
    {
        string kept = best is null
            ? string.Empty
            : $" The project is left as it was after its best round (round {best.Round}, {best.Score.Describe()}).";
        string reason = $"Step {step.Id} ({step.Title}) used its {(int)_stepClock.TotalMinutes} minutes of working time ({(int)worked.TotalMinutes} minutes of model calls and checks) " +
                        $"without passing its check, so it was not started again.{kept} Review the run log; retrying the step gives it a fresh clock.";
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.StepTimeLimit, step.Tier,
            detail: reason, modelNode: null, workspaceNode: step.Machine);
        ParkStep(plan, step, reason, "working-time");
    }

    private static bool HasUnsyncedInterruptedWorkspace(PlanRecord plan, PlanStep step)
    {
        PlanRunEvent? latestWorkerAttempt = (plan.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.WorkspaceStaged)
            .OrderByDescending(runEvent => runEvent.AtUtc)
            .FirstOrDefault();
        if (latestWorkerAttempt is null || latestWorkerAttempt.Attempt is not { } attempt) return false;

        return !(plan.Events ?? []).Any(runEvent => runEvent.StepId == step.Id && runEvent.Attempt == attempt &&
            (runEvent.Kind is RunEventKind.WorkspaceSynced or RunEventKind.WorkspaceReconciled) &&
            runEvent.AtUtc >= latestWorkerAttempt.AtUtc);
    }

    private static bool HasInterruptedAttempt(PlanRecord plan, PlanStep step)
    {
        PlanRunEvent? latestAttempt = (plan.Events ?? [])
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.AttemptStarted)
            .OrderByDescending(runEvent => runEvent.AtUtc)
            .FirstOrDefault();
        if (latestAttempt is null) return false;
        PlanRunEvent[] attemptEvents = (plan.Events ?? []).Where(runEvent => runEvent.StepId == step.Id &&
            runEvent.Attempt == latestAttempt.Attempt).ToArray();
        if (!attemptEvents.Any(runEvent => runEvent.Kind == RunEventKind.WorkspaceStaged) &&
            attemptEvents.Any(runEvent => runEvent.Kind == RunEventKind.WorkspaceFailed)) return false;
        return !attemptEvents.Any(runEvent => runEvent.Kind == RunEventKind.StepDone);
    }

    private static string? EnvironmentBlockReason(string output)
    {
        if (output.StartsWith("Blocked by unattended plan policy:", StringComparison.OrdinalIgnoreCase))
        {
            return "the requested command exceeds the local-only capabilities allowed during unattended plan retries";
        }

        if (output.StartsWith("Worker workspace unavailable:", StringComparison.OrdinalIgnoreCase))
        {
            return "the selected worker workspace could not complete verification or sync changes to the hub";
        }

        if (Regex.IsMatch(output,
                @"(?:No compatible \.NET SDK (?:was|could be) found|No \.NET SDKs? were found|NETSDK1045|MSB4236)",
                RegexOptions.IgnoreCase))
        {
            return "the required .NET SDK is not installed or cannot be selected on the selected worker.";
        }

        Match missing = Regex.Match(output,
            @"(?:the term\s+)?(?:'|""|“|”)?(?<program>[\w.+-]+)(?:'|""|“|”)?\s+is not recognized as (?:an internal or external command|the name)|(?:^|[\r\n])[^\r\n]*?\b(?<unix>dotnet|node|npm|npx|yarn|pnpm|python(?:3)?|py)\s*:\s*(?:command not found|not found)",
            RegexOptions.IgnoreCase);
        string? program = missing.Success
            ? missing.Groups["program"].Success ? missing.Groups["program"].Value : missing.Groups["unix"].Value
            : null;
        if (program is not null && IsRuntimeProgram(program))
        {
            return $"the required {program} executable is missing from the selected worker's PATH.";
        }

        return null;
    }

    internal static bool IsRuntimeProgram(string program) =>
        program.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("node", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("npx", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("python", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("python3", StringComparison.OrdinalIgnoreCase) ||
        program.Equals("py", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerSelectionTemporarilyUnavailable(string message) =>
        message.Contains("worker is ready", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("no configured", StringComparison.OrdinalIgnoreCase);

    private static bool IsToolchainEnvironmentIssue(string reason) =>
        reason.StartsWith("the required .NET SDK", StringComparison.OrdinalIgnoreCase) ||
        reason.StartsWith("the required ", StringComparison.OrdinalIgnoreCase) &&
        reason.Contains(" executable is missing", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerTransportFailure(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Renci.SshNet.Common.SshAuthenticationException)
            {
                return false;
            }

            if (current is Renci.SshNet.Common.SshConnectionException or Renci.SshNet.Common.SshOperationTimeoutException or
                SshException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private const string InfiniteLoopSentence = "A program or test that never ends is usually an infinite loop " +
        "(for example while (true) without a limit) or a wait that never completes. Make it finish.";

    private const string TestRunHint = "The test run did not end. A test that starts a server, a timer or a connection must close it in a finally block " +
        "(or an after hook): when an assertion fails before the close, the open handle keeps the process alive and hides the failure. " +
        "Close everything in finally, then fix the failing assertion shown above.";

    // The failure of a check whose command was killed at the timeout. What it printed before the kill is the evidence: a test run
    // prints its failing assertion before the process hangs, and the retry cannot see that unless the failure carries it.
    internal static string TimedOutCheckFailure(TimeSpan timeout, string? partialOutput, string? projectRoot)
    {
        string failure = $"Error: command exceeded the {timeout.TotalSeconds:0}s timeout and was killed.\n\n";
        if (string.IsNullOrWhiteSpace(partialOutput)) return failure + "The check never finished. " + InfiniteLoopSentence;

        string printed = "\n\n--- output before it was killed ---\n" +
            PlanTools.ShortenCheckOutput(FailureLocation.ProjectRelative(partialOutput, projectRoot));
        if (PlanFailure.IsTestRunOutput(partialOutput)) return failure + "The check never finished." + printed + "\n\n" + TestRunHint;
        return failure + "The check never finished. " + InfiniteLoopSentence + printed;
    }

    internal Task SyncToHubWithReconnectAsync(IWorkerWorkspaceSession worker, CancellationToken cancellationToken) =>
        RunWithReconnectsAsync(worker, worker.SyncToHubAsync, "syncing changes to the hub", cancellationToken, cancellationToken);

    internal Task SyncToHubWithReconnectAsync(PlanRecord plan, PlanStep step, int attempt, IWorkerWorkspaceSession worker,
        CancellationToken operationToken, CancellationToken pauseToken) =>
        RunWithReconnectsAsync(worker, worker.SyncToHubAsync, "syncing changes to the hub", operationToken, pauseToken,
            plan, step, attempt, step.Tier);

    internal Task RefreshFromHubWithReconnectAsync(IWorkerWorkspaceSession worker, CancellationToken cancellationToken) =>
        RunWithReconnectsAsync(worker, worker.RefreshFromHubAsync, "refreshing from the hub", cancellationToken, cancellationToken);

    internal Task RefreshFromHubWithReconnectAsync(PlanRecord plan, PlanStep step, int attempt, IWorkerWorkspaceSession worker,
        CancellationToken operationToken, CancellationToken pauseToken) =>
        RunWithReconnectsAsync(worker, worker.RefreshFromHubAsync, "refreshing from the hub", operationToken, pauseToken,
            plan, step, attempt, step.Tier);

    private Task RunWithReconnectsAsync(
        IWorkerWorkspaceSession worker,
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken operationToken,
        CancellationToken pauseToken,
        PlanRecord? plan = null,
        PlanStep? step = null,
        int? attempt = null,
        string? tier = null)
    {
        return RunWithReconnectsAsync<bool>(worker, async token =>
        {
            await operation(token);
            return true;
        }, operationName, operationToken, pauseToken, plan, step, attempt, tier);
    }

    private async Task<T> RunWithReconnectsAsync<T>(
        IWorkerWorkspaceSession worker,
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken operationToken,
        CancellationToken pauseToken,
        PlanRecord? plan = null,
        PlanStep? step = null,
        int? attempt = null,
        string? tier = null,
        Func<Exception, CancellationToken, Task<bool>>? shouldReconnect = null)
    {
        shouldReconnect ??= (exception, _) => Task.FromResult(IsWorkerTransportFailure(exception));
        Exception lastFailure;
        try
        {
            return await operation(operationToken);
        }
        catch (Exception exception)
        {
            if (!await shouldReconnect(exception, pauseToken)) throw;
            lastFailure = exception;
        }

        foreach (TimeSpan pause in ReconnectPauses)
        {
            if (pause >= TimeSpan.FromMinutes(1) && plan is not null && step is not null)
            {
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.Waiting, tier, node: worker.Machine,
                    detail: $"Worker {worker.Machine} is unreachable ({Summarize(lastFailure.Message)}); waiting {Duration(pause)} before reconnecting. The worker keeps this step's edits; nothing is lost.",
                    modelNode: worker.Machine, workspaceNode: worker.Machine);
            }

            _logger.LogWarning(lastFailure,
                "Worker {Machine} lost its connection while {Operation}; waiting {Delay} before reconnecting.",
                worker.Machine, operationName, Duration(pause));
            await DelayBeforeRetryAsync(pause, pauseToken);
            try
            {
                await worker.ReconnectAsync(operationToken);
                return await operation(operationToken);
            }
            catch (Exception exception)
            {
                if (!await shouldReconnect(exception, pauseToken)) throw;
                lastFailure = exception;
            }
        }

        ExceptionDispatchInfo.Capture(lastFailure).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing the final worker transport failure.");
    }

    private static async Task<bool> IsWorkerCheckTransportFailureAsync(
        Exception exception, IWorkerWorkspaceSession workspace, CancellationToken cancellationToken)
    {
        bool commandTimedOut = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is WorkerCommandTimeoutException)
            {
                commandTimedOut = true;
                break;
            }
        }

        return commandTimedOut
            ? !await workspace.AnswersAsync(cancellationToken)
            : IsWorkerTransportFailure(exception);
    }

    private async Task<bool> RunParallelGroupAsync(PlanRecord plan, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        // Only the first model attempt runs concurrently. The runner waits for every edit to finish,
        // then verifies each step in order. Any retries run sequentially, and project-wide checks
        // cannot race ongoing edits.
        ModelAttemptResult[] firstAttempts = await Task.WhenAll(steps.Select(step =>
            RunModelAttemptAsync(plan, step, 1, LastFailure(plan.Id, step.Id), step.Tier, parallelGroup: true, cancellationToken: cancellationToken)));

        var retry = new List<(int StepId, string Failure, int FirstAttempt, string? ModelOverride, string? WorkspaceOverride, IStepSession? Session)>();
        var failedSteps = new List<PlanStep>();
        foreach (ModelAttemptResult attempt in firstAttempts.OrderBy(result => result.StepId))
        {
            PlanStep step = steps.Single(candidate => candidate.Id == attempt.StepId);
            if (!attempt.ShouldVerify)
            {
                FailureClass failureClass = (await RecordRoundClassifiedAsync(plan, step, 1, attempt, attempt.Failure ?? string.Empty,
                    passed: false, (await ChangedFilesSinceAsync(plan, attempt.WorkBefore, cancellationToken)).Files, RepairLadder.RequestedTierRung)).Class;
                if (attempt.WorkspaceFailure is { } workspaceFailure)
                {
                    if (!attempt.ReroutableWorkspaceFailure)
                    {
                        ParkForEnvironment(plan, step, 1, step.Tier, attempt.Machine, workspaceFailure,
                            "the selected worker workspace is not configured or could not be staged");
                        failedSteps.Add(step);
                        continue;
                    }

                    _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id,
                        currentStep => currentStep with { Attempts = Math.Min(currentStep.Attempts, 0) }));
                    (string? alternate, string routeDetail) = await SelectRetryMachineAsync(plan, step, step.Tier, cancellationToken);
                    if (alternate is not null)
                    {
                        if (step.Machine is not null)
                        {
                            step = step with { Machine = alternate };
                            _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id,
                                currentStep => currentStep with { Machine = alternate }));
                        }
                        _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.RecoveryRouted, step.Tier,
                            detail: $"Worker staging failed before model tools or edits. {routeDetail}", workspaceNode: alternate);
                        retry.Add((step.Id, workspaceFailure, 1, null, alternate, attempt.Session));
                    }
                    else
                    {
                        int waitNumber = CountWaitingEvents(plan.Id, step.Id) + 1;
                        TimeSpan delay = DelayWithinRunDeadline(plan, _transientDelay(waitNumber - 1));
                        _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.Waiting, step.Tier, node: attempt.Machine,
                            detail: $"Worker staging failed before model tools or edits ({Summarize(workspaceFailure)}). Waiting {Duration(delay)} (wait {waitNumber}) before retrying; this does not count as an attempt.",
                            workspaceNode: attempt.Machine);
                        await DelayBeforeRetryAsync(delay, cancellationToken);
                        retry.Add((step.Id, workspaceFailure, 1, null, null, attempt.Session));
                    }
                    continue;
                }

                if (failureClass == FailureClass.Environment)
                {
                    ParkForEnvironment(plan, step, 1, step.Tier, attempt.ModelNode, attempt.Failure ?? "The model route failed with an environment error.",
                        "the selected model machine has a permission, security, or runtime problem");
                    failedSteps.Add(step);
                    continue;
                }

                bool noAttemptSpent = failureClass is FailureClass.Infra or FailureClass.Unknown;
                string? modelOverride = null;
                if (noAttemptSpent)
                {
                    _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id,
                        currentStep => currentStep with { Attempts = Math.Min(currentStep.Attempts, 0) }));
                    if (failureClass == FailureClass.Unknown)
                    {
                        int unknownFailures = CountClassifiedRounds(plan.Id, step.Id, FailureClass.Unknown);
                        if (unknownFailures >= 5)
                        {
                            ParkForUnknown(plan, step, 1, attempt.FailureException, attempt.Failure);
                            failedSteps.Add(step);
                            continue;
                        }

                        TimeSpan delay = DelayWithinRunDeadline(plan, TimeSpan.FromSeconds(20 * unknownFailures));
                        _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.Waiting, step.Tier, node: attempt.ModelNode,
                            detail: $"Unknown failure on {attempt.ModelNode ?? "the selected model machine"}: {Summarize(attempt.Failure ?? "unknown failure")}. Waiting at least {Duration(delay)} (unknown failure {unknownFailures} of 5) before retrying; this does not count as an attempt.",
                            modelNode: attempt.ModelNode, workspaceNode: attempt.Machine);
                        await DelayBeforeRetryAsync(delay, cancellationToken);
                    }
                    else
                    {
                        bool workerTransportFailure = IsWorkerTransportFailure(attempt.FailureException);
                        modelOverride = workerTransportFailure
                            ? null
                            : await RouteFailedModelAsync(plan, step, step.Tier, attempt.ModelNode,
                                attempt.Machine, 1, cancellationToken);
                        if (modelOverride is null)
                        {
                            (modelOverride, _) = await WaitForInfrastructureRecoveryAsync(plan, step, step.Tier,
                                attempt.ModelNode, attempt.Machine, 1, attempt.Failure,
                                CountWaitingEvents(plan.Id, step.Id), cancellationToken, allowRoute: !workerTransportFailure);
                        }
                    }
                }
                retry.Add((step.Id, attempt.Failure ?? "The model call did not complete.", noAttemptSpent ? 1 : 2, modelOverride, null, attempt.Session));
                continue;
            }

            StepCompletion check = await _tools.TryCompleteStepAsync(
                plan.Id, step.Id, Summarize(attempt.Summary), cancellationToken, FilesToCreate(plan, step));
            long? verification = RecordCheck(plan, step, 1, check, ViaNode(attempt.Summary));
            await RecordRoundClassifiedAsync(plan, step, 1, attempt, check.Output ?? check.Message, check.Done,
                (await ChangedFilesSinceAsync(plan, attempt.WorkBefore, cancellationToken)).Files, RepairLadder.RequestedTierRung, round: 1);
            if (check.Done)
            {
                _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.CheckPassed, step.Tier,
                    detail: step.Verify is null ? "No automatic check for this step." : $"`{step.Verify}` passed.");
                _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.StepDone, step.Tier,
                    detail: StepDoneDetail(plan, step, 1, check.Restored, allowReview: false));
                RecordStepDone(plan, step, verification, ViaNode(attempt.Summary));
            }
            else
            {
                string failure = WithStrayNote(plan, step, check.Output ?? check.Message);
                if (EnvironmentBlockReason(failure) is { } environmentReason)
                {
                    ParkForEnvironment(plan, step, 1, step.Tier, ViaNode(attempt.Summary), failure, environmentReason);
                    failedSteps.Add(step);
                }
                else
                {
                    _store.AddEvent(plan.Id, step.Id, 1, RunEventKind.CheckFailed, step.Tier, detail: failure,
                        rung: RepairLadder.RequestedTierRung, round: 1);
                    // The conversation that made the first attempt carries on: it is told what the check said.
                    retry.Add((step.Id, failure, attempt.Session is { HasHistory: true } ? 1 : 2, null, null, attempt.Session));
                }
            }
        }

        foreach ((int stepId, string failure, int firstAttempt, string? modelOverride, string? workspaceOverride, IStepSession? session) in retry.OrderBy(item => item.StepId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanRecord current = _store.Get(plan.Id)!;
            PlanStep step = current.Steps.Single(candidate => candidate.Id == stepId);
            if (!await RunStepAsync(current, step, cancellationToken, firstAttempt,
                previousFailure: failure, deferPlanBlock: true, initialModelOverride: modelOverride,
                initialWorkspaceOverride: workspaceOverride, continuingSession: session))
            {
                failedSteps.Add(step);
            }
        }

        // A parked step is not the end of the plan: the rest goes on. Only a harder stop (the run deadline) ends it here.
        PlanRecord afterGroup = _store.Get(plan.Id)!;
        PlanStep[] hardStops = failedSteps
            .Select(failed => afterGroup.Steps.First(step => step.Id == failed.Id))
            .Where(failed => failed.Status == StepStatus.Failed)
            .ToArray();
        if (hardStops.Length == 0)
        {
            return true;
        }

        _store.Update(plan.Id, current => current with { Status = PlanStatus.Blocked });
        foreach (PlanStep failed in hardStops)
        {
            AddBlockedEvent(plan.Id, failed, failed.Note);
        }

        return false;
    }

    // The edit check runs the step's own check after each edit a worker model makes (see EditCheckGate). It needs a worker whose
    // files it can read and write as bytes, and a step with a check that may run. A tests step writes tests that are allowed to
    // fail while they are written, so it is never checked this way.
    private EditCheckGate? CreateEditCheck(PlanRecord plan, PlanStep step, IWorkerWorkspaceSession worker)
    {
        if (!_editCheck.Enabled || IsTestsStep(step) || step.Kind == "tests" || string.IsNullOrWhiteSpace(step.Verify) ||
            PlanRunnerToolPolicy.CommandRefusal(step.Verify) is not null || worker is not IEditCheckFiles files)
        {
            return null;
        }

        string command = step.Verify!;
        return new EditCheckGate(files, cancellationToken => _tools.RunFrozenTestsAsync(plan, command, cancellationToken),
            output => RoundScore.Of(PlanFailure.FailureSignature(output), output), _editCheck,
            line => _logger.LogInformation("Plan {PlanId} step {StepId}: edit check: {Line}", plan.Id, step.Id, line));
    }

    // Returns the summary for the attempt's event, or null when no gate was made. Called once per attempt (the gate is cleared).
    private async Task<string?> FinishEditCheckAsync(EditCheckGate? gate, PlanRecord plan, PlanStep step, int attempt)
    {
        if (gate is null) return null;
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        string text = summary.Describe();
        _logger.LogInformation("Plan {PlanId} step {StepId} attempt {Attempt}: edit check finished: {Summary}", plan.Id, step.Id, attempt, text);
        return $"Edit check: {text}";
    }

    private async Task<ModelAttemptResult> RunModelAttemptAsync(
        PlanRecord plan,
        PlanStep step,
        int attempt,
        string? lastFailure,
        string tier,
        bool parallelGroup,
        CancellationToken cancellationToken,
        string? workspaceOverride = null,
        string? modelMachineOverride = null,
        RoundContext? round = null,
        CallLimit? limit = null)
    {
        // A model call belongs to a conversation. The first attempt of a parallel group opens its own.
        RoundContext conversation = round ?? new RoundContext(_agent.OpenSession(), RepairLadder.RequestedTierRung, 1);
        bool continuing = conversation.RepairMessage is not null;
        _logger.LogInformation(
            "Plan {PlanId} step {StepId} ({Title}): attempt {Attempt}, rung {Rung} round {Round} on the {Tier} tier{Parallel}.",
            plan.Id,
            step.Id,
            step.Title,
            attempt,
            conversation.Rung,
            conversation.Round,
            tier,
            parallelGroup ? " (parallel group)" : string.Empty);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        PlanRecord? claimed = _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, s => s with
        {
            Status = StepStatus.Running,
            Attempts = Math.Max(s.Attempts, attempt),
            StartedUtc = s.StartedUtc ?? startedUtc
        }));
        if (claimed is not null)
        {
            plan = claimed;
            step = claimed.Steps.Single(candidate => candidate.Id == step.Id);
        }

        string? machine = workspaceOverride ?? step.Machine;
        string? retryRoute = null;
        if (machine is null && !continuing && HasPriorAttempt(plan.Id, step.Id))
        {
            (machine, retryRoute) = await SelectRetryMachineAsync(plan, step, tier, cancellationToken);

            // "Could not move off this machine" is true of most retries (a fleet has one worker per tier) and means
            // nothing to the model; the note is for the log and the prompt only when the workspace really moved.
            if (machine is null)
            {
                retryRoute = null;
            }
        }

        if (_workerWorkspaces is not null && machine is null)
        {
            try
            {
                machine = await SelectWorkspaceMachineAsync(plan, step, tier, cancellationToken);
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("Worker workspace unavailable:", StringComparison.OrdinalIgnoreCase))
            {
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.AttemptStarted, tier, detail: exception.Message,
                    rung: conversation.Rung, round: conversation.Round);
                return new ModelAttemptResult(step.Id, string.Empty, exception.Message, ShouldVerify: false,
                    WorkspaceFailure: exception.Message,
                    ReroutableWorkspaceFailure: IsWorkerSelectionTemporarilyUnavailable(exception.Message),
                    Session: conversation.Session);
            }
        }

        string attemptDetail = continuing
            ? $"Round {conversation.Round}: the output of the failed check goes back into the same conversation."
            : retryRoute ?? (string.IsNullOrWhiteSpace(lastFailure)
                ? string.Empty
                : attempt > 1 ? "Retrying with the previous failure; the requested task tier is unchanged." : "Picking up after the stop, with the last failure.");
        string? fallbackHub = _fleetOptions?.Nodes.SingleOrDefault(node => node.Fallback)?.Name;

        // Climbing to the hub is the decision of the repair ladder (see RunStepAsync), passed in as the model machine.
        string? modelMachine = modelMachineOverride ?? ModelMachineFor(_modeService?.Mode, step.Machine, machine, fallbackHub);
        if (IsTestsStep(step))
        {
            string? writer = _testWriterNode?.Invoke() ?? fallbackHub;
            bool hubDeclined = writer is not null && string.Equals(writer, fallbackHub, StringComparison.OrdinalIgnoreCase) && !HubRescueAllowed(plan, step);
            modelMachine = hubDeclined
                ? _fleetOptions?.Nodes.FirstOrDefault(node => !node.Fallback && !node.Vision && node.Tier == FleetTiers.Heavy)?.Name
                : writer;
            if (hubDeclined)
            {
                tier = FleetTiers.Heavy;
                attemptDetail += " The test writer hub is not allowed by recovery scope or fleet mode; using the heavy tier in the same worker workspace.";
                if (modelMachine is null)
                    return new ModelAttemptResult(step.Id, string.Empty, "No non-hub heavy-tier test writer is configured.", ShouldVerify: false, Machine: machine);
            }
        }

        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.AttemptStarted, tier, node: modelMachine, detail: attemptDetail,
            modelNode: modelMachine, workspaceNode: machine, rung: conversation.Rung, round: conversation.Round);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(limit?.Duration ?? _attemptTimeout);
        PlanTools.WorkSnapshot? work = null;
        IWorkerWorkspaceSession? worker = null;
        bool stagingWorkerWorkspace = false;
        ModelAttemptResult result = new(step.Id, string.Empty, null, ShouldVerify: false, Machine: machine, ModelNode: modelMachine);
        var activity = new StepToolActivity();
        var stall = new ModelStallWatch(StallLimit, StallGrace);
        activity.Observed = toolName =>
        {
            stall.Progress();
            // Only a worker round is reconciled after a restart, so only a worker round's changing calls go to the run log.
            if (worker is not null && !ReadOnlyTools.Contains(toolName))
            {
                _store.AddEvent(plan.Id, step.Id, attempt, ChangingToolEvent, tier, machine, toolName,
                    modelNode: modelMachine, workspaceNode: machine, rung: conversation.Rung, round: conversation.Round);
            }
        };
        EditCheckGate? editCheck = null;
        string? editCheckNote = null;
        try
        {
            SnapshotFilesToCreate(plan, step);
            work = await _tools.SnapshotWorkAsync(plan, attemptCancellation.Token);
            // A follow-up message needs no file contents: the conversation has the task, and the files are one tool call away.
            string fileContext = continuing ? string.Empty : await LoadStepFileContextAsync(plan, step, attemptCancellation.Token);

            if (_workerWorkspaces is not null)
            {
                if (string.IsNullOrWhiteSpace(machine))
                {
                    throw new InvalidOperationException($"Worker workspace unavailable: no ready worker can serve the requested {tier} tier.");
                }

                stagingWorkerWorkspace = true;
                HealFrozenHubCopies(plan);
                worker = await _workerWorkspaces.StageAsync(plan, step, machine, attemptCancellation.Token);
                stagingWorkerWorkspace = false;
                try
                {
                    _store.SaveWorkerBaseline(plan.Id, step.Id, attempt, machine, worker.BaselineHashes());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("Worker workspace unavailable: could not save the staged file baseline for restart recovery.", exception);
                }
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkspaceStaged, tier, machine,
                    "Staged the project on this worker. File tools and this step's verification command run in that worker workspace.",
                    modelNode: modelMachine, workspaceNode: machine);
                // The mark says this round's changing tool calls are logged from here on (see RoundMadeNoChangingCall).
                _store.AddEvent(plan.Id, step.Id, attempt, ToolLoggedEvent, tier, machine,
                    "Tool calls of this round are logged from here on, so a restart can tell whether the round changed anything.",
                    modelNode: modelMachine, workspaceNode: machine);
                editCheck = CreateEditCheck(plan, step, worker);
            }

            // Everything the step's model does (routing, tool calls, decisions it pins) is recorded in the
            // plan's context under this step's task, and the router hands it the durable context for it.
            string? contextId = ContextFor(plan.Id);
            if (_recorder is not null && contextId is not null && !continuing)
            {
                _recorder.AttemptStarted(contextId, plan, step, attempt, tier);
            }

            string runId = continuing ? $"{plan.Id}:{step.Id}:{attempt}:{conversation.Round}" : $"{plan.Id}:{step.Id}:{attempt}";
            using IDisposable? scope = _journal is not null && contextId is not null
                ? _journal.Push(new FleetRequestIdentity(contextId, runId, "plan-runner", FleetPlanContext.StepTaskId(plan.Id, step.Id)))
                : null;
            // dispute_test is offered to an ordinary step while frozen tests are in play, and a dispute from any other reply is not read.
            bool disputeOffered = !IsTestsStep(step) && _testFirstMode == TestFirstMode.On && plan.FrozenTests is { Count: > 0 };
            StepAgentReply reply;
            using IDisposable? hubScope = worker is null ? HubFrozenScopeFor(plan) : null;
            using (IDisposable? workerScope = worker?.Enter())
            {
                // The edit check looks at each file edit the model makes; the context is ambient for the model call only.
                using IDisposable? editCheckScope = editCheck is null ? null : EditCheckContext.Push(editCheck);
                bool addStepGuidance = conversation.RepairMessage is null &&
                    _stepGuidance && PlanStepGuidance.AppliesTo(step.Files);
                if (addStepGuidance)
                {
                    _logger.LogInformation("Step guidance added to step {StepId} of plan {PlanId}", step.Id, plan.Id);
                }

                // The brief is read by the prompt providers, so what a tests step adds to it has to be in the data they get.
                string? stepBrief = IsTestsStep(step) ? PlanTestsStepPrompt.Append(conversation.Brief, plan.Requirements) : conversation.Brief;
                conversation.Session.OfferDispute(disputeOffered);
                conversation.Session.TrackActivity(activity);
                conversation.Session.ConfigurePrompt(conversation.RepairMessage is null
                    ? new PlanStepPromptData(plan, step, attempt, lastFailure, fileContext, parallelGroup,
                        contextId is not null, retryRoute, worker is not null, machine, worker?.Platform,
                        stepBrief, _stepGuidance)
                    : null);
                Task<StepAgentReply> call = conversation.Session.SendAsync(
                    conversation.RepairMessage ??
                    BuildPrompt(plan, step, attempt, lastFailure, fileContext, parallelGroup,
                        durableContext: contextId is not null, retryRoute: retryRoute,
                        workerWorkspace: worker is not null, workerMachine: machine, workerPlatform: worker?.Platform,
                        brief: stepBrief, stepGuidance: _stepGuidance),
                    tier,
                    modelMachine,
                    attemptCancellation.Token);
                reply = await stall.WaitAsync(call, attemptCancellation, cancellationToken);
            }
            editCheckNote = await FinishEditCheckAsync(editCheck, plan, step, attempt);
            editCheck = null;
            string summary = reply.Text;

            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.AttemptEnded, tier, modelMachine ?? ViaNode(summary) ?? _journal?.ActualNode,
                $"Model finished after {(int)stopwatch.Elapsed.TotalSeconds} s: {Summarize(summary)}{(editCheckNote is null ? string.Empty : " " + editCheckNote)}",
                modelNode: modelMachine ?? ViaNode(summary), workspaceNode: machine, rung: conversation.Rung, round: conversation.Round);
            result = new ModelAttemptResult(step.Id, summary, null, ShouldVerify: true, Workspace: worker, Machine: machine,
                ModelNode: modelMachine ?? ViaNode(summary) ?? _journal?.ActualNode, ToolCalls: reply.ToolCalls,
                EditToolCalled: reply.EditToolCalled, LoopGuardNotes: reply.LoopGuardNotes, WorkBefore: work,
                Blocker: CheckAudit.BlockerFrom(reply),
                Dispute: disputeOffered ? TestDispute.DisputeFrom(reply) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the model managed to change is still worth checking below.
            _logger.LogWarning("Plan {PlanId} step {StepId} attempt {Attempt} timed out.", plan.Id, step.Id, attempt);
            // The attempt's own timer did not fire: one model call got no reply within the network timeout (a worker that
            // makes 11 tokens a second is silent for minutes while it writes a file). It used to be reported as "ran out of
            // time after 20 minutes" although it had run for 12 and 9, and answered with the hint about a hung command.
            string failure = !attemptCancellation.IsCancellationRequested
                ? $"A model call got no reply within {Duration(_fleetOptions?.NetworkTimeout ?? TimeSpan.FromMinutes(15))}: what it was writing was too long to finish, and is lost."
                : limit is null
                    ? $"The attempt ran out of time after {_attemptTimeout.TotalMinutes:0} minutes."
                    : $"The attempt was stopped after {Duration(limit.Duration)} because {limit.Reason}.";
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TimedOut, tier, modelMachine ?? _journal?.ActualNode, failure,
                modelNode: modelMachine, workspaceNode: machine, rung: conversation.Rung, round: conversation.Round);
            result = new ModelAttemptResult(step.Id, string.Empty, failure, ShouldVerify: true, Workspace: worker, Machine: machine,
                ModelNode: modelMachine ?? _journal?.ActualNode, WorkBefore: work, TimedOut: true,
                ToolCalls: activity.ToolCalls, EditToolCalled: activity.EditToolCalled);
        }
        catch (ModelStalledException stalled)
        {
            // A timed-out round like any other: its check runs and the ladder decides. The call that was left behind may still
            // be running, so the conversation is not carried on (see the round loop).
            _logger.LogWarning("Plan {PlanId} step {StepId} attempt {Attempt}: the model stopped answering for {Limit}.",
                plan.Id, step.Id, attempt, stalled.Limit);
            string failure = $"The model stopped answering for {Duration(stalled.Limit)}; this round is counted as failed.";
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TimedOut, tier, modelMachine ?? _journal?.ActualNode, failure,
                modelNode: modelMachine, workspaceNode: machine, rung: conversation.Rung, round: conversation.Round);
            result = new ModelAttemptResult(step.Id, string.Empty, failure, ShouldVerify: true, Workspace: worker, Machine: machine,
                ModelNode: modelMachine ?? _journal?.ActualNode, WorkBefore: work, TimedOut: true, Stalled: true,
                ToolCalls: activity.ToolCalls, EditToolCalled: activity.EditToolCalled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            bool workspaceUnavailable = stagingWorkerWorkspace ||
                exception.Message.StartsWith("Worker workspace unavailable:", StringComparison.OrdinalIgnoreCase);
            string failure = workspaceUnavailable
                ? exception.Message.StartsWith("Worker workspace unavailable:", StringComparison.OrdinalIgnoreCase)
                    ? exception.Message
                    : $"Worker workspace unavailable: staging on {machine ?? "the selected worker"} failed ({exception.GetType().Name}: {exception.Message})."
                : $"The model call failed: {exception.Message}";
            if (workspaceUnavailable)
                _logger.LogWarning(exception, "Plan {PlanId} step {StepId} attempt {Attempt}: worker workspace staging failed on {Machine}.",
                    plan.Id, step.Id, attempt, machine);
            else
                _logger.LogWarning(exception, "Plan {PlanId} step {StepId} attempt {Attempt}: the model call failed.",
                    plan.Id, step.Id, attempt);
            _store.AddEvent(plan.Id, step.Id, attempt,
                workspaceUnavailable ? RunEventKind.WorkspaceFailed : RunEventKind.ModelFailed,
                tier, workspaceUnavailable ? machine : _journal?.ActualNode, failure,
                modelNode: modelMachine ?? _journal?.ActualNode, workspaceNode: machine,
                rung: conversation.Rung, round: conversation.Round);
            string? workspaceFailure = workspaceUnavailable ? failure : null;
            result = new ModelAttemptResult(step.Id, string.Empty, failure, ShouldVerify: false, Transient: IsTransient(exception),
                WorkspaceFailure: workspaceFailure, Machine: machine, ModelNode: modelMachine ?? _journal?.ActualNode,
                ReroutableWorkspaceFailure: stagingWorkerWorkspace && IsWorkerTransportFailure(exception),
                FailureException: exception, WorkBefore: work);
        }
        finally
        {
            // The edit check ends with the attempt however it ended (a timeout or a failure too), and before the sync below sends
            // the worker's files to the hub: a run left worse than its best state goes back to that state first.
            if (editCheck is not null)
            {
                await FinishEditCheckAsync(editCheck, plan, step, attempt);
                editCheck = null;
            }

            try
            {
                // Publish this attempt before restoring earlier edits on the hub; then update the worker copy so
                // verification and any retry see the same project state as the canonical checkout.
                if (worker is not null)
                {
                    await SyncToHubWithReconnectAsync(plan, step, attempt, worker, CancellationToken.None, cancellationToken);
                    _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkspaceSynced, tier, machine,
                        "Synced worker file changes to the hub checkout before verification.",
                        modelNode: result.ModelNode ?? modelMachine, workspaceNode: machine);
                    _store.DeleteWorkerBaseline(plan.Id, step.Id, attempt);
                }

                if (work is not null)
                {
                    IReadOnlyList<string> restored = await _tools.RestoreDiscardedWorkAsync(_store.Get(plan.Id) ?? plan, EarlierWorkOnly(plan, step, work), CancellationToken.None);
                    if (restored.Count > 0)
                    {
                        string files = string.Join(", ", restored);
                        _logger.LogWarning("Plan {PlanId} step {StepId} attempt {Attempt} threw away earlier work; put back {Files}.", plan.Id, step.Id, attempt, files);
                        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.WorkRestored, tier,
                            detail: $"Put back {files}: work of an earlier step that this attempt deleted or reset with git.");
                    }
                }

                if (worker is not null)
                {
                    await RefreshFromHubWithReconnectAsync(plan, step, attempt, worker, CancellationToken.None, cancellationToken);
                    result = result with { Workspace = worker };
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Could not sync worker workspace for plan {PlanId} step {StepId}.", plan.Id, step.Id);
                worker?.Dispose();
                string failure = $"Worker workspace unavailable: changes from {machine ?? "the selected worker"} could not be synced to the hub: {exception.Message}";
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.ModelFailed, tier, machine, failure,
                    modelNode: modelMachine ?? _journal?.ActualNode, workspaceNode: machine);
                result = new ModelAttemptResult(step.Id, string.Empty, failure, ShouldVerify: false, WorkspaceFailure: failure,
                    Machine: machine, ModelNode: modelMachine ?? _journal?.ActualNode);
            }
        }

        // A stalled round gives up its conversation: the call left behind must not be sent another message.
        return result.Stalled ? result : result with { Session = conversation.Session };
    }

    /// <summary>
    /// A failure of the network or a machine rather than of the model's work: nothing answered, the connection
    /// broke, or Ollama said it was overloaded or down. Worth waiting for; a bad answer is not.
    /// </summary>
    internal static bool IsTransient(Exception exception)
        => PlanFailure.IsInfrastructure(exception);

    // 30 s, 1, 2, 4 and 8 minutes, then every 15 minutes, with jitter to spread retries.
    private static TimeSpan DefaultTransientDelay(int wait) =>
        TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromMinutes(15).Ticks,
            (long)(TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, wait), 15 * 60)).Ticks * (0.8 + Random.Shared.NextDouble() * 0.4))));

    private static string Duration(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} minute{(delay.TotalMinutes >= 1.5 ? "s" : string.Empty)}" : $"{delay.TotalSeconds:0} seconds";

    // The files a step names that do not exist when it first starts: it is expected to create them, and
    // is not done until they exist. Taken once, on the first attempt, so a retry cannot move the goalposts.
    private void SnapshotFilesToCreate(PlanRecord plan, PlanStep step)
    {
        _filesAtStart.TryAdd($"{plan.Id}:{step.Id}", FleetPlanContext.SnapshotFiles(plan.WorkingDirectory));
        SnapshotRequiredFiles(plan, step);
    }

    // Files a step created that no step of the plan names. Small models leave scratch files behind ("test-slug.js"
    // next to the real test) and a check such as `node --test` then picks them up. Reported, never deleted: the
    // fleet does not remove files it did not write.
    // The notes name the first ten; the extra round for unnamed files asks for all of them, so it can say how many more there are.
    private string[] StrayFiles(PlanRecord plan, PlanStep step, int limit = 10)
    {
        if (!_filesAtStart.TryGetValue($"{plan.Id}:{step.Id}", out HashSet<string>? before) || before.Count == 0 && !Directory.Exists(plan.WorkingDirectory ?? string.Empty))
        {
            return [];
        }

        // A step that names a folder ("test/") or a pattern ("test/*.test.ts") names what it puts there.
        string[] named = plan.Steps.SelectMany(other => FleetPlanContext.DeclaredPaths(plan, other)).ToArray();
        string root = plan.WorkingDirectory ?? string.Empty;
        return FleetPlanContext.SnapshotFiles(plan.WorkingDirectory)
            .Where(file => !before.Contains(file) && !named.Any(declared => FleetPlanContext.Names(declared, file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();
    }

    private static string JoinNotes(string first, string? second) =>
        string.IsNullOrWhiteSpace(second) ? first : string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private string StepDoneDetail(PlanRecord plan, PlanStep step, int attempt, string? restored, bool allowReview = true)
    {
        PlanEvidence evidence = allowReview && HasPassedReview(_store.Get(plan.Id)!, step.Id, attempt)
            ? PlanEvidence.SecondOpinionAgreed
            : PlanEvidence.OwnCheckOnly;
        _store.Update(plan.Id, current => current with
        {
            Steps = current.Steps
                .Select(candidate => candidate.Id == step.Id ? candidate with { Evidence = evidence } : candidate)
                .ToArray()
        });
        return JoinNotes(JoinNotes(StrayDetail(plan, step), restored), PlanEvidenceRules.Detail(evidence));
    }

    private static bool HasPassedReview(PlanRecord plan, int stepId, int attempt) =>
        plan.Events?.Any(e => e.Kind == RunEventKind.StepReview && e.StepId == stepId &&
            e.Attempt == attempt && e.FailureClass == "Passed") == true;

    private string StrayDetail(PlanRecord plan, PlanStep step)
    {
        string[] strays = StrayFiles(plan, step);
        return strays.Length == 0 ? string.Empty : $"Also created files the plan does not name: {string.Join(", ", strays)}.";
    }

    private string WithStrayNote(PlanRecord plan, PlanStep step, string failure)
    {
        string[] strays = StrayFiles(plan, step);
        return strays.Length == 0
            ? failure
            : failure + $"\n\nNote: this step also created files that the plan does not name: {string.Join(", ", strays)}. " +
              "If they are leftovers from experiments, delete them (a check can pick them up); if they are needed, keep them.";
    }

    private void SnapshotRequiredFiles(PlanRecord plan, PlanStep step) =>
        _filesToCreate.TryAdd(
            $"{plan.Id}:{step.Id}",
            // Without a project folder on disk there is nothing to check against (and nowhere the model could create files).
            !string.IsNullOrWhiteSpace(plan.WorkingDirectory) && Directory.Exists(plan.WorkingDirectory)
                ? FleetPlanContext.DeclaredPaths(plan, step).Where(path => !FleetPlanContext.DeclaredExists(path)).ToArray()
                : []);

    private string[]? FilesToCreate(PlanRecord plan, PlanStep step) =>
        _filesToCreate.TryGetValue($"{plan.Id}:{step.Id}", out string[]? files) ? files : null;

    private long? RecordCheck(PlanRecord plan, PlanStep step, int attempt, StepCompletion result, string? node) =>
        _recorder is not null && ContextFor(plan.Id) is { } contextId
            ? _recorder.CheckResult(contextId, plan, step, attempt, result.Done, result.Output ?? result.Message, node)
            : null;

    private void RecordStepDone(PlanRecord plan, PlanStep step, long? verificationEventId, string? node)
    {
        if (_recorder is not null && ContextFor(plan.Id) is { } contextId)
        {
            _recorder.StepDone(contextId, plan, step, verificationEventId, node);
        }
    }

    private void AddBlockedEvent(string planId, PlanStep step, string? reason = null)
    {
        reason ??= $"Step {step.Id} ({step.Title}) did not pass after {MaxAttemptsPerStep} attempts.";
        if (_recorder is not null && ContextFor(planId) is { } contextId && _store.Get(planId) is { } blockedPlan)
        {
            _recorder.StepBlocked(contextId, blockedPlan, step, reason);
        }

        _store.AddEvent(planId, step.Id, null, RunEventKind.PlanBlocked, detail: reason);
        _logger.LogWarning("Plan {PlanId} is blocked at step {StepId} ({Title}).", planId, step.Id, step.Title);
    }

    private sealed record ModelAttemptResult(
        int StepId,
        string Summary,
        string? Failure,
        bool ShouldVerify,
        bool Transient = false,
        IWorkerWorkspaceSession? Workspace = null,
        string? WorkspaceFailure = null,
        string? Machine = null,
        string? ModelNode = null,
        bool ReroutableWorkspaceFailure = false,
        int ToolCalls = 0,
        bool EditToolCalled = false,
        int? LoopGuardNotes = null,
        PlanTools.WorkSnapshot? WorkBefore = null,
        Exception? FailureException = null,
        IStepSession? Session = null,
        ModelBlocker? Blocker = null,
        bool TimedOut = false,
        TestDisputeCall? Dispute = null,
        bool Stalled = false);

    /// <summary>
    /// What is worth saying again to a model whose step failed, from the step and the failure: used by the first prompt of
    /// a fresh conversation and by every follow-up message of a round.
    /// </summary>
    internal static IEnumerable<string> RetryHints(PlanStep step, string failure) =>
        PlanStepPromptProvider.RetryHints(step, failure);

    internal static string BuildPrompt(
        PlanRecord plan,
        PlanStep step,
        int attempt,
        string? lastFailure,
        string? fileContext = null,
        bool parallelGroup = false,
        bool durableContext = false,
        string? retryRoute = null,
        bool workerWorkspace = false,
        string? workerMachine = null,
        string? workerPlatform = null,
        string? brief = null,
        bool stepGuidance = false)
    {
        return PlanStepPromptParts.Create(plan, step, parallelGroup, workerWorkspace, workerMachine).Text;
    }

    /// <summary>
    /// Said to a model carrying out a step, in the first prompt, every repair message and the nudge. Measured on a 9B worker
    /// that was shown its failing tests: half of its turns wrote an analysis ("let me recount... wait...") until the 4096-token
    /// limit and called no tool (one to two minutes lost each time, then a round that changed nothing); with this sentence none
    /// did, and every turn began with a tool call within seconds.
    /// </summary>
    internal const string ActFirst = PlanStepPromptProvider.ActFirst;

    private static async Task<string> LoadStepFileContextAsync(PlanRecord plan, PlanStep step, CancellationToken cancellationToken)
    {
        if (step.Files.Count == 0)
        {
            return string.Empty;
        }

        string root;
        try
        {
            root = Path.GetFullPath(plan.WorkingDirectory ?? Environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return "(File context was unavailable because the project folder path is invalid.)";
        }

        if (!Directory.Exists(root))
        {
            return "(File context was unavailable because the project folder does not exist.)";
        }

        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var result = new StringBuilder();
        int filesAdded = 0;
        int skipped = 0;

        foreach (string requestedPath in step.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filesAdded >= MaxStepContextFiles || result.Length >= MaxStepContextCharacters)
            {
                skipped++;
                continue;
            }

            string path;
            try
            {
                if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
                {
                    skipped++;
                    continue;
                }

                path = Path.GetFullPath(Path.IsPathFullyQualified(requestedPath)
                    ? requestedPath
                    : Path.Combine(root, requestedPath));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                skipped++;
                continue;
            }

            string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!path.StartsWith(rootPrefix, pathComparison) || !File.Exists(path) || HasLinkedPathComponent(root, path) ||
                !WorkerWorkspaceSession.IsAllowedProjectFile(relativePath))
            {
                skipped++;
                continue;
            }

            byte[] bytes;
            bool truncated;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
                int count = Math.Min(MaxStepContextBytesPerFile, checked((int)Math.Min(stream.Length, MaxStepContextBytesPerFile)));
                truncated = stream.Length > count;
                bytes = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int current = await stream.ReadAsync(bytes.AsMemory(read, count - read), cancellationToken);
                    if (current == 0) break;
                    read += current;
                }

                if (read != bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (bytes.AsSpan().Contains((byte)0))
            {
                skipped++;
                continue;
            }

            string contents;
            try
            {
                contents = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException)
            {
                skipped++;
                continue;
            }

            if (truncated)
            {
                contents += "\n[truncated: file context is limited to 32 KiB per file]";
            }

            int remaining = MaxStepContextCharacters - result.Length;
            if (contents.Length > remaining)
            {
                contents = contents[..remaining] + "\n[truncated: step file context limit reached]";
            }

            string displayPath = Path.GetRelativePath(root, path);
            result.AppendLine($"--- BEGIN UNTRUSTED FILE: {displayPath} ---");
            result.AppendLine(contents);
            result.AppendLine($"--- END UNTRUSTED FILE: {displayPath} ---");
            filesAdded++;
        }

        if (skipped > 0)
        {
            result.AppendLine($"({skipped} named file(s) were omitted from prompt context because they were missing, outside the project folder, linked, binary, unreadable, or over the context limit.)");
        }

        return result.ToString().TrimEnd();
    }

    internal static bool HasLinkedPathComponent(string root, string filePath)
    {
        try
        {
            if (new FileInfo(filePath).LinkTarget is not null || new DirectoryInfo(filePath).LinkTarget is not null ||
                ((File.Exists(filePath) || Directory.Exists(filePath)) &&
                 (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0))
            {
                return true;
            }

            string? directory = Path.GetDirectoryName(filePath);
            while (directory is not null && !string.Equals(directory, root, OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal))
            {
                if (new DirectoryInfo(directory).LinkTarget is not null ||
                    (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0))
                {
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The model's closing sentence becomes the step's note. The router appends a "via <node>"
    // footer to every answer; that is not part of what was done.
    internal static string Summarize(string text)
    {
        string cleaned = ViaFooter().Replace(text ?? string.Empty, string.Empty).Trim();
        string oneLine = string.Join(' ', cleaned.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..300].TrimEnd() + "...";
    }

    // The machine that answered, from the router's "via <node>" footer.
    internal static string? ViaNode(string? text)
    {
        Match match = ViaFooter().Match(text ?? string.Empty);
        return match.Success ? match.Groups["node"].Value : null;
    }

    [GeneratedRegex(@"\s*_?[—-]\s*via (?<node>[a-z0-9-]+)_?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ViaFooter();
}

internal static class FleetPlanStoreSteps
{
    public static PlanRecord With(PlanRecord plan, int stepId, Func<PlanStep, PlanStep> change) =>
        plan with { Steps = plan.Steps.Select(step => step.Id == stepId ? change(step) : step).ToList() };
}
