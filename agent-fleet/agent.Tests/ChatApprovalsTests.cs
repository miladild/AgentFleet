using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ChatApprovalsTests
{
    // A limit for a wait that should already have finished. A passing test never comes near it, and a broken wait fails
    // at once instead of hanging the run.
    internal static readonly TimeSpan SafetyLimit = TimeSpan.FromSeconds(10);

    private static ChatApprovals NewService(TimeProvider clock, int timeoutSeconds = 240) =>
        new(clock, () => timeoutSeconds, NullLogger.Instance);

    /// <summary>
    /// Waits until the service lists the expected number of calls. The waiting task has to reach its wait first, so
    /// this polls for the list; it never waits for a time limit.
    /// </summary>
    internal static async Task<IReadOnlyList<PendingApproval>> WaitForPendingAsync(ChatApprovals approvals, int expected = 1)
    {
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            IReadOnlyList<PendingApproval> pending = approvals.Pending();
            if (pending.Count >= expected)
            {
                return pending;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Expected {expected} listed approval(s).");
    }

    [Fact]
    public async Task Approving_a_listed_call_completes_its_wait_as_approved_and_unlists_it()
    {
        var approvals = NewService(new ManualClock());

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "write_file", "{\"path\":\"a.txt\"}", CancellationToken.None);
        PendingApproval listed = (await WaitForPendingAsync(approvals)).Single();

        Assert.Equal("chat-1", listed.ContextId);
        Assert.Equal("write_file", listed.Tool);
        Assert.Equal("{\"path\":\"a.txt\"}", listed.Arguments);
        Assert.True(approvals.Decide(listed.Id, approve: true));
        Assert.Equal(ApprovalDecision.Approved, await wait.WaitAsync(SafetyLimit));
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task Rejecting_a_listed_call_completes_its_wait_as_rejected_and_unlists_it()
    {
        var approvals = NewService(new ManualClock());

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "run_command", "{}", CancellationToken.None);
        PendingApproval listed = (await WaitForPendingAsync(approvals)).Single();

        Assert.True(approvals.Decide(listed.Id, approve: false));
        Assert.Equal(ApprovalDecision.Rejected, await wait.WaitAsync(SafetyLimit));
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task A_call_nobody_answers_times_out_when_its_time_runs_out_and_a_late_answer_is_refused()
    {
        var clock = new ManualClock();
        var approvals = NewService(clock, timeoutSeconds: 60);

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "run_command", "{}", CancellationToken.None);
        PendingApproval listed = (await WaitForPendingAsync(approvals)).Single();
        Assert.Equal(60, listed.SecondsLeft(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, approvals.Pending().Single().SecondsLeft(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ApprovalDecision.TimedOut, await wait.WaitAsync(SafetyLimit));
        Assert.Empty(approvals.Pending());
        Assert.False(approvals.Decide(listed.Id, approve: true));
    }

    [Fact]
    public async Task A_cancelled_wait_throws_like_any_cancelled_await_and_unlists_the_call()
    {
        var approvals = NewService(new ManualClock());
        using var cancel = new CancellationTokenSource();

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "write_file", "{}", cancel.Token);
        PendingApproval listed = (await WaitForPendingAsync(approvals)).Single();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(SafetyLimit));
        Assert.Empty(approvals.Pending());
        Assert.False(approvals.Decide(listed.Id, approve: true));
    }

    [Fact]
    public async Task Only_the_first_answer_to_a_call_counts()
    {
        var approvals = NewService(new ManualClock());

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None);
        string id = (await WaitForPendingAsync(approvals)).Single().Id;

        Assert.True(approvals.Decide(id, approve: false));
        Assert.False(approvals.Decide(id, approve: true));
        Assert.False(approvals.Decide(id, approve: false));
        Assert.Equal(ApprovalDecision.Rejected, await wait.WaitAsync(SafetyLimit));
    }

    [Fact]
    public void An_unknown_call_cannot_be_answered()
    {
        var approvals = NewService(new ManualClock());

        Assert.False(approvals.Decide("no-such-call", approve: true));
        Assert.False(approvals.Decide(string.Empty, approve: false));
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task Two_waiting_calls_are_answered_independently()
    {
        var approvals = NewService(new ManualClock());

        Task<ApprovalDecision> first = approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None);
        Task<ApprovalDecision> second = approvals.RequestAsync("chat-2", "run_command", "{}", CancellationToken.None);
        IReadOnlyList<PendingApproval> listed = await WaitForPendingAsync(approvals, expected: 2);
        PendingApproval writeCall = listed.Single(item => item.Tool == "write_file");
        PendingApproval commandCall = listed.Single(item => item.Tool == "run_command");

        Assert.True(approvals.Decide(writeCall.Id, approve: true));
        Assert.Equal(ApprovalDecision.Approved, await first.WaitAsync(SafetyLimit));
        Assert.False(second.IsCompleted);
        Assert.Equal(commandCall.Id, Assert.Single(approvals.Pending()).Id);

        Assert.True(approvals.Decide(commandCall.Id, approve: false));
        Assert.Equal(ApprovalDecision.Rejected, await second.WaitAsync(SafetyLimit));
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task A_long_argument_list_is_cut_to_the_listed_length()
    {
        var approvals = NewService(new ManualClock());

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "write_file", new string('x', 5000), CancellationToken.None);
        PendingApproval listed = (await WaitForPendingAsync(approvals)).Single();

        Assert.Equal(ChatApprovals.MaxArgumentCharacters, listed.Arguments.Length);
        Assert.EndsWith("...", listed.Arguments, StringComparison.Ordinal);
        Assert.True(approvals.Decide(listed.Id, approve: true));
        await wait.WaitAsync(SafetyLimit);
    }

    [Fact]
    public async Task The_wait_is_read_when_each_call_starts_so_a_changed_setting_applies_to_the_next_call()
    {
        var clock = new ManualClock();
        int seconds = 60;
        var approvals = new ChatApprovals(clock, () => seconds, NullLogger.Instance);

        Task<ApprovalDecision> first = approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None);
        PendingApproval firstCall = (await WaitForPendingAsync(approvals)).Single();
        Assert.Equal(60, firstCall.SecondsLeft(clock.GetUtcNow()));
        Assert.True(approvals.Decide(firstCall.Id, approve: true));
        await first.WaitAsync(SafetyLimit);

        seconds = 90;
        Task<ApprovalDecision> second = approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None);
        PendingApproval secondCall = (await WaitForPendingAsync(approvals)).Single();
        Assert.Equal(90, secondCall.SecondsLeft(clock.GetUtcNow()));
        Assert.True(approvals.Decide(secondCall.Id, approve: true));
        await second.WaitAsync(SafetyLimit);
    }

    [Fact]
    public async Task A_failing_log_write_does_not_leave_the_call_listed()
    {
        var approvals = new ChatApprovals(new ManualClock(), () => 240, new FailingLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None));

        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task A_wall_clock_set_back_does_not_stretch_the_countdown_or_move_the_refusal()
    {
        var clock = new ManualClock();
        var approvals = NewService(clock, timeoutSeconds: 240);

        Task<ApprovalDecision> wait = approvals.RequestAsync("chat-1", "write_file", "{}", CancellationToken.None);
        await WaitForPendingAsync(approvals);
        clock.Advance(TimeSpan.FromSeconds(100));
        // The machine's clock is corrected an hour back. The wait was started on the monotonic clock, so 140 s remain.
        clock.SetUtcNow(clock.GetUtcNow() - TimeSpan.FromHours(1));

        PendingApproval listed = approvals.Pending().Single();
        Assert.Equal(140, listed.SecondsLeft(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(140));
        Assert.Equal(ApprovalDecision.TimedOut, await wait.WaitAsync(SafetyLimit));
    }

    // A log sink that fails on every write, as a broken logging pipeline can.
    private sealed class FailingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("The log sink failed.");
    }
}
