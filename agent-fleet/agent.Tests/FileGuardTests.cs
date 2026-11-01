namespace AgentFleet.Tests;

/// <summary>
/// A plan step may not change the project's dependencies or settings unless it names the file: measured, a worker bumped a
/// dependency in package.json to try to fix a type error, in two runs of three, and nothing in the step asked for it.
/// </summary>
public sealed class FileGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "fileguard-project");

    private static string? Denial(string tool, string path, params string[] declared) =>
        FileGuard.FileToolDenial(tool, new Dictionary<string, object?> { ["path"] = path }, Root, declared.Select(file => Path.Combine(Root, file)));

    [Theory]
    [InlineData("write_file", "package.json")]
    [InlineData("edit_file", "package.json")]
    [InlineData("delete_file", "package.json")]
    [InlineData("edit_file", "web/package.json")]
    [InlineData("edit_file", "package-lock.json")]
    [InlineData("write_file", "yarn.lock")]
    [InlineData("write_file", "pnpm-lock.yaml")]
    [InlineData("edit_file", "tsconfig.json")]
    [InlineData("edit_file", "tsconfig.build.json")]
    [InlineData("write_file", "node_modules/left-pad/index.js")]
    [InlineData("edit_file", "web/node_modules/x/package.json")]
    public void A_manifest_a_lock_file_a_tsconfig_and_node_modules_are_not_writable_unless_the_step_names_them(string tool, string path)
    {
        string? denial = Denial(tool, path);

        Assert.NotNull(denial);
        Assert.StartsWith("DENIED: ", denial, StringComparison.Ordinal);
        Assert.Contains(path, denial, StringComparison.Ordinal);
        Assert.Contains("report_blocker", denial, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("write_file", "src/server/alerts.ts")]
    [InlineData("edit_file", "test/alerts.test.ts")]
    [InlineData("write_file", "docs/tsconfig-notes.md")]
    [InlineData("write_file", "src/package.json.ts")]
    [InlineData("read_file", "package.json")]
    [InlineData("list_directory", "node_modules")]
    public void Ordinary_files_and_every_read_are_allowed(string tool, string path) => Assert.Null(Denial(tool, path));

    [Fact]
    public void A_manifest_the_step_lists_is_writable_with_a_relative_or_an_absolute_path()
    {
        Assert.Null(Denial("edit_file", "package.json", "package.json"));
        Assert.Null(Denial("edit_file", Path.Combine(Root, "package.json"), "package.json"));
        Assert.NotNull(Denial("edit_file", "package-lock.json", "package.json"));
    }

    [Fact]
    public void Moving_a_file_onto_or_away_from_a_protected_name_is_denied()
    {
        var onto = new Dictionary<string, object?> { ["source"] = "src/a.json", ["destination"] = "package.json" };
        var away = new Dictionary<string, object?> { ["source"] = "tsconfig.json", ["destination"] = "old.json" };

        Assert.NotNull(FileGuard.FileToolDenial("move_file", onto, Root, []));
        Assert.NotNull(FileGuard.FileToolDenial("move_file", away, Root, []));
        Assert.Null(FileGuard.FileToolDenial("move_file", new Dictionary<string, object?> { ["source"] = "a.ts", ["destination"] = "b.ts" }, Root, []));
    }

    [Theory]
    [InlineData("npm install tsx@latest")]
    [InlineData("npm i -D vitest")]
    [InlineData("npm install --save-dev tsx")]
    [InlineData("pnpm add zod")]
    [InlineData("yarn add left-pad")]
    [InlineData("bun add x")]
    [InlineData("npm uninstall left-pad")]
    [InlineData("npm update")]
    [InlineData("npm remove x")]
    [InlineData("npx -y create-thing")]
    [InlineData("npx --yes tsx")]
    [InlineData("npx @scope/tool run")]
    [InlineData("npx typescript@5 --noEmit")]
    [InlineData("pip install requests")]
    [InlineData("python -m pip install requests")]
    [InlineData("pip3 install -r requirements.txt")]
    [InlineData("cd web && npm install lodash")]
    [InlineData("npm.cmd install tsx@latest")]
    public void A_command_that_installs_or_changes_packages_is_denied(string command)
    {
        string? denial = FileGuard.CommandDenial(command);

        Assert.NotNull(denial);
        Assert.StartsWith("DENIED: ", denial, StringComparison.Ordinal);
        Assert.Contains("report_blocker", denial, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("npm install")]
    [InlineData("npm install --no-audit --no-fund")]
    [InlineData("npm ci")]
    [InlineData("pnpm install")]
    [InlineData("yarn")]
    [InlineData("yarn install")]
    [InlineData("npm test")]
    [InlineData("npm run typecheck && npm test")]
    [InlineData("npx tsc --noEmit")]
    [InlineData("npx tsx test/a.test.ts")]
    [InlineData("node --test test/a.test.ts")]
    [InlineData("dotnet test")]
    [InlineData("cat package.json")]
    [InlineData("pip list")]
    public void Running_the_project_and_restoring_what_the_lock_file_names_are_allowed(string command) =>
        Assert.Null(FileGuard.CommandDenial(command));

    [Fact]
    public void No_command_is_no_denial() => Assert.Null(FileGuard.CommandDenial(null));
}
