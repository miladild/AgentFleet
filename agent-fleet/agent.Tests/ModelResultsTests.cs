using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ModelResultsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-results-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Fewer_than_five_steps_in_either_window_do_not_signal_a_drop()
    {
        IReadOnlyList<ModelRegression> found = ModelResults.FindRegressions(
            Results(4, 1), Results(4, 4));

        Assert.Empty(found);
        Assert.Equal(FleetSetupService.Ok, ModelResults.Check(found, [], Now).Status);
    }

    [Fact]
    public void A_drop_by_at_least_half_fails_with_both_window_counts()
    {
        IReadOnlyList<ModelRegression> found = ModelResults.FindRegressions(
            Results(6, 1), Results(5, 4));

        ModelRegression regression = Assert.Single(found);
        Assert.Equal("node-a.example.test", regression.Node);
        Assert.Equal("model-a", regression.Model);
        SetupCheck check = ModelResults.Check(found, [], Now);
        Assert.Equal(FleetSetupService.Fail, check.Status);
        Assert.Equal("Model results", check.Title);
        Assert.Contains("4 of 5 last week, 1 of 6 this week on model-a", check.Detail);
    }

    [Fact]
    public void A_smaller_drop_does_not_fail_and_stale_fit_measurements_are_listed_as_a_warning()
    {
        IReadOnlyList<ModelRegression> found = ModelResults.FindRegressions(
            Results(6, 3), Results(5, 4));

        SetupCheck check = ModelResults.Check(found, ["node-a.example.test (model-a)"], Now);
        Assert.Equal(FleetSetupService.Warn, check.Status);
        Assert.Contains("Stale fit measurements: node-a.example.test (model-a)", check.Detail);
    }

    private static ModelScorecardsResult Results(int worked, int own) => new(
        [new ModelScorecard("node-a.example.test", "model-a", worked, own, 0,
            new Dictionary<string, int>(), 0, new Dictionary<string, int>(), 0, 0, null)],
        0, new Dictionary<string, int>(), 0, new Dictionary<string, int>(),
        new Dictionary<string, int>(), 0, new Dictionary<string, int>(), 0);

    [Fact]
    public async Task A_regression_notice_is_sent_once_for_each_node_in_a_utc_week()
    {
        var sender = new RecordingSender();
        var notifier = new ModelRegressionNotifier(sender, _directory, NullLogger.Instance);
        var nodeA = new ModelRegression("node-a.example.test", "model-a", 4, 5, 1, 6);
        var nodeAOtherModel = new ModelRegression("node-a.example.test", "model-b", 5, 6, 2, 7);
        var nodeB = new ModelRegression("node-b.example.test", "model-b", 5, 6, 2, 7);
        DateTimeOffset thursday = Now;
        DateTimeOffset friday = Now.AddDays(1);

        await notifier.NotifyOnceAsync([nodeA, nodeAOtherModel, nodeB], thursday);
        var restartedNotifier = new ModelRegressionNotifier(sender, _directory, NullLogger.Instance);
        await restartedNotifier.NotifyOnceAsync([nodeA, nodeAOtherModel, nodeB], friday);

        Assert.Equal(2, sender.Notices.Count);
        Assert.Contains(sender.Notices, notice => notice.Node == nodeA.Node);
        Assert.Contains(sender.Notices, notice => notice.Node == nodeB.Node);
        Assert.All(sender.Notices, notice => Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), notice.WeekStartUtc));
    }

    private sealed class RecordingSender : IModelRegressionNoticeSender
    {
        public List<ModelRegressionNotice> Notices { get; } = [];
        public Task SendAsync(ModelRegressionNotice notice)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
