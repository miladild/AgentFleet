using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

/// <summary>The accepted bytes of frozen test files are kept in the plan's storage, under their hash.</summary>
public sealed class FrozenContentStoreTests : PlanTestBase
{
    private static readonly byte[] Accepted = Encoding.UTF8.GetBytes("public class LimitTests { }\n");

    private string FrozenFolder(string planId) => Path.Combine(PlansDirectory, planId + ".frozen");

    [Fact]
    public void Saved_frozen_content_is_read_back_by_its_hash()
    {
        PlanRecord plan = NewPlan(Step("one"));

        string hash = Store.SaveFrozenContent(plan.Id, Accepted);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Accepted)), hash);
        Assert.Equal(Accepted, Store.ReadFrozenContent(plan.Id, hash));
    }

    [Fact]
    public void Stored_content_whose_bytes_no_longer_hash_to_its_name_is_not_trusted()
    {
        PlanRecord plan = NewPlan(Step("one"));
        string hash = Store.SaveFrozenContent(plan.Id, Accepted);
        File.WriteAllText(Path.Combine(FrozenFolder(plan.Id), hash), "changed on disk");

        Assert.Null(Store.ReadFrozenContent(plan.Id, hash));
    }

    [Fact]
    public void A_hash_that_was_never_saved_finds_nothing()
    {
        PlanRecord plan = NewPlan(Step("one"));
        Store.SaveFrozenContent(plan.Id, Accepted);

        Assert.Null(Store.ReadFrozenContent(plan.Id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other")))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("..\\..\\plans\\secret")]
    public void A_name_that_is_not_a_sha256_hash_finds_nothing(string name)
    {
        PlanRecord plan = NewPlan(Step("one"));
        Store.SaveFrozenContent(plan.Id, Accepted);

        Assert.Null(Store.ReadFrozenContent(plan.Id, name));
    }
}

/// <summary>
/// The hub's own file tools refuse to change a frozen test file while a plan's model attempt runs on the hub. The scope is
/// ambient: it is set only inside that attempt and never reaches another request.
/// </summary>
public sealed class HubFrozenTestRefusalTests : IDisposable
{
    private static readonly string Accepted = "public class LimitTests { }\n";
    private readonly string _project = Path.Combine(Path.GetTempPath(), "fleet-hub-frozen-" + Guid.NewGuid().ToString("N"));

    public HubFrozenTestRefusalTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "tests"));
        File.WriteAllText(Frozen, Accepted);
    }

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Frozen => Path.Combine(_project, "tests", "LimitTests.cs");

    private IDisposable Scope(params string[] frozenFiles) =>
        HubFrozenTestScope.Push(_project, frozenFiles.Select(file => new PlanFrozenTestFile(file, "00")).ToArray());

    [Fact]
    public async Task Write_on_a_frozen_path_is_refused_while_the_scope_is_set()
    {
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.WriteFileAsync(Frozen, "public class LimitTests { /* changed */ }\n", default);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Fact]
    public async Task Edit_on_a_frozen_path_is_refused_while_the_scope_is_set()
    {
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.EditFileAsync(Frozen, "public", "private", null, default);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Fact]
    public void Move_away_from_a_frozen_path_is_refused_while_the_scope_is_set()
    {
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.MoveFile(Frozen, Path.Combine(_project, "tests", "Moved.cs"), null);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
        Assert.False(File.Exists(Path.Combine(_project, "tests", "Moved.cs")));
    }

    [Fact]
    public void Move_onto_a_frozen_path_is_refused_even_with_overwrite_while_the_scope_is_set()
    {
        string source = Path.Combine(_project, "tests", "Other.cs");
        File.WriteAllText(source, "other\n");
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.MoveFile(source, Frozen, true);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Delete_of_a_frozen_path_is_refused_while_the_scope_is_set()
    {
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.DeleteFile(Frozen);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Theory]
    [InlineData("{root}/tests/LimitTests.cs")]
    [InlineData("{root}/tests/./LimitTests.cs")]
    [InlineData("{root}/src/../tests/LimitTests.cs")]
    [InlineData("{root}\\tests\\LimitTests.cs")]
    [InlineData("{ROOT}/TESTS/limittests.cs")]
    public async Task Every_spelling_of_a_frozen_path_is_refused(string spelling)
    {
        if (!OperatingSystem.IsWindows() && (spelling.Contains('\\') || spelling.Contains("{ROOT}", StringComparison.Ordinal))) return;
        string path = spelling.Replace("{ROOT}", _project.ToUpperInvariant(), StringComparison.Ordinal).Replace("{root}", _project, StringComparison.Ordinal);
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.WriteFileAsync(path, "changed\n", default);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Fact]
    public void Moving_a_folder_that_holds_a_frozen_file_is_refused_while_the_scope_is_set()
    {
        string folder = Path.Combine(_project, "tests");
        string moved = Path.Combine(_project, "old_tests");
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.MoveFile(folder, moved, null);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(moved));
    }

    [Fact]
    public void A_file_moved_into_a_folder_that_holds_a_frozen_file_cannot_replace_that_file()
    {
        string source = Path.Combine(_project, "src", "LimitTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "other\n");
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.MoveFile(source, Path.Combine(_project, "tests"), true);

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Deleting_a_folder_that_holds_a_frozen_file_is_refused_while_the_scope_is_set()
    {
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = WorkspaceTools.DeleteFile(Path.Combine(_project, "tests"));

        Assert.Equal(FrozenTestFiles.RefusalMessage, result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Theory]
    [InlineData("{root}/tests/LimitTests.cs.")]
    [InlineData("{root}/tests/LimitTests.cs ")]
    [InlineData("{root}/tests/LimitTests.cs::$DATA")]
    [InlineData("{root}/tests/LimitTests.cs:hidden")]
    [InlineData("{root}/TESTS~1/LIMITT~1.CS")]
    public async Task On_windows_a_frozen_file_named_another_way_is_refused(string spelling)
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = spelling.Replace("{root}", _project, StringComparison.Ordinal);
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.WriteFileAsync(path, "changed\n", default);

        Assert.True(result == FrozenTestFiles.RefusalMessage || result.Contains("short (8.3)", StringComparison.Ordinal), result);
        Assert.Equal(Accepted, File.ReadAllText(Frozen));
    }

    [Fact]
    public async Task Writes_are_allowed_without_the_scope()
    {
        string result = await WorkspaceTools.WriteFileAsync(Frozen, "changed\n", default);

        Assert.StartsWith("Wrote", result, StringComparison.Ordinal);
        Assert.Equal("changed\n", File.ReadAllText(Frozen));
    }

    [Fact]
    public async Task Other_files_are_allowed_while_the_scope_is_set()
    {
        string other = Path.Combine(_project, "tests", "Other.cs");
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.WriteFileAsync(other, "other\n", default);

        Assert.StartsWith("Wrote", result, StringComparison.Ordinal);
        Assert.Equal("other\n", File.ReadAllText(other));
    }

    [Fact]
    public async Task A_test_file_that_is_not_frozen_yet_can_be_written_while_the_scope_is_set()
    {
        string created = Path.Combine(_project, "tests", "NewTests.cs");
        using IDisposable _ = Scope("tests/LimitTests.cs");

        string result = await WorkspaceTools.WriteFileAsync(created, "new\n", default);

        Assert.StartsWith("Wrote", result, StringComparison.Ordinal);
        Assert.Equal("new\n", File.ReadAllText(created));
    }

    [Fact]
    public async Task The_scope_does_not_outlive_its_attempt()
    {
        using (Scope("tests/LimitTests.cs")) { }

        Assert.Null(HubFrozenTestScope.Current);
        string result = await WorkspaceTools.WriteFileAsync(Frozen, "changed\n", default);
        Assert.StartsWith("Wrote", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scope_does_not_reach_a_request_that_started_before_it()
    {
        var scopeSet = new TaskCompletionSource();
        Task<string> earlier = Task.Run(async () =>
        {
            await scopeSet.Task;
            return await WorkspaceTools.WriteFileAsync(Frozen, "changed by another request\n", default);
        });

        using (Scope("tests/LimitTests.cs"))
        {
            Assert.NotNull(HubFrozenTestScope.Current);
            scopeSet.SetResult();
            string result = await earlier;
            Assert.StartsWith("Wrote", result, StringComparison.Ordinal);
        }

        Assert.Equal("changed by another request\n", File.ReadAllText(Frozen));
    }
}
