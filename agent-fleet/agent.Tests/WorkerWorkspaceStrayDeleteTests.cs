namespace AgentFleet.Tests;

/// <summary>
/// A file the worker created and then deleted must not stay in the hub checkout: the sync carried it there while it existed, and
/// only files a step names were carried away again. A file created after the plan started is a leftover of this run.
/// </summary>
public sealed class WorkerWorkspaceStrayDeleteTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stray-delete-" + Guid.NewGuid().ToString("N"));

    public WorkerWorkspaceStrayDeleteTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string NewFile()
    {
        string path = Path.Combine(_folder, "scratch.ts");
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_file_created_after_the_plan_started_may_be_deleted()
    {
        string path = NewFile();

        Assert.True(WorkerWorkspaceSession.CreatedSincePlanStarted(path, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void A_file_that_was_there_before_the_plan_started_may_not_be_deleted()
    {
        string path = NewFile();
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        Assert.False(WorkerWorkspaceSession.CreatedSincePlanStarted(path, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void Without_a_start_time_or_a_file_nothing_may_be_deleted()
    {
        string path = NewFile();

        Assert.False(WorkerWorkspaceSession.CreatedSincePlanStarted(path, null));
        Assert.False(WorkerWorkspaceSession.CreatedSincePlanStarted(Path.Combine(_folder, "missing.ts"), DateTimeOffset.UtcNow.AddMinutes(-5)));
    }
}
