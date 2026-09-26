using System.Text.Json;

namespace AgentFleet.Tests;

public sealed class PlanReviewTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fleet-review-{Guid.NewGuid():N}");

    public PlanReviewTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        File.WriteAllText(Path.Combine(_project, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_project, "src", "index.js"), "");
    }

    public void Dispose() => Directory.Delete(_project, recursive: true);

    private static readonly Func<string, bool> Installed = program => program is "node" or "npm" or "dotnet" or "python";

    private static PlanStepInput Step(string title, string? verify, params string[] files) => new(title, "do it", files, verify, "standard");

    private IReadOnlyList<string> Review(params PlanStepInput[] steps) => PlanReview.Problems(_project, steps, Installed);

    [Fact]
    public void A_good_plan_has_no_problems() =>
        Assert.Empty(Review(
            Step("slug and its test", "node --test test/slug.test.js", "src/slug.js", "test/slug.test.js"),
            Step("export it", "node -e \"require('./src/index.js').slugify('a b')\"", "src/index.js"),
            Step("nothing to check", null),
            Step("all tests", "npm test")));

    [Fact]
    public void A_plan_without_any_check_or_without_a_final_check_is_refused()
    {
        Assert.Contains("No step has a check", Assert.Single(Review(Step("a", null, "src/a.js"), Step("b", null, "src/b.js"))));
        Assert.Contains("The last step", Assert.Single(Review(Step("a", "npm test", "src/a.js"), Step("b", null, "src/b.js"))));
    }

    [Fact]
    public void A_check_that_starts_a_server_or_a_watcher_is_refused()
    {
        // Measured: "Start the development server" was checked with `npm run dev` (tsx watch), which can never pass.
        string problem = Assert.Single(Review(Step("start it", "npm run dev", "src/index.js")));
        Assert.Contains("does not exit on its own", problem);
        Assert.Contains("\"dev\" script", problem);
    }

    [Theory]
    [InlineData("npm start", true)]
    [InlineData("npx vite", true)]
    [InlineData("npx vite build", false)]
    [InlineData("npx jest --watchAll", true)]
    [InlineData("npx jest --watchAll=false", false)]
    [InlineData("npx tsc -w", true)]
    [InlineData("npx tsc --noEmit", false)]
    [InlineData("cd src && dotnet watch test", true)]
    [InlineData("dotnet test", false)]
    [InlineData("python -m http.server 8000", true)]
    [InlineData("node --test test/slug.test.js", false)]
    public void Servers_and_watchers_are_told_from_checks_that_finish(string check, bool endless) =>
        Assert.Equal(endless, PlanReview.NeverFinishes(check, _project) is not null);

    [Fact]
    public void An_npm_script_is_read_from_package_json()
    {
        File.WriteAllText(Path.Combine(_project, "package.json"),
            """{ "scripts": { "test": "node --import tsx --test \"test/**/*.test.ts\"", "test:watch": "node --test --watch" } }""");

        Assert.Null(PlanReview.NeverFinishes("npm test", _project));
        Assert.Contains("--watch", PlanReview.NeverFinishes("npm run test:watch", _project));
    }

    [Fact]
    public void A_sentence_is_not_a_check()
    {
        string problem = Assert.Single(Review(Step("slug", "File src/slug.js should exist with a slugify function", "src/slug.js")));
        Assert.Contains("not a command the hub can run", problem);
    }

    [Fact]
    public void A_check_with_a_program_that_is_not_installed_is_named()
    {
        string problem = Assert.Single(Review(Step("slug", "jest test/slug.test.js", "src/slug.js", "test/slug.test.js")));
        Assert.Contains("jest is not installed", problem);
    }

    [Fact]
    public void A_check_that_needs_a_later_steps_file_is_refused()
    {
        string problem = Assert.Single(Review(
            Step("slug", "node --test test/slug.test.js", "src/slug.js"),
            Step("slug tests", "node --test test/slug.test.js", "test/slug.test.js")));
        Assert.Contains("which step 2 creates", problem);
    }

    [Fact]
    public void A_check_with_a_file_nobody_creates_is_refused()
    {
        string problem = Assert.Single(Review(Step("slug", "node --test test/slug.test.js", "src/slug.js")));
        Assert.Contains("no step names", problem);
    }

    [Fact]
    public void A_leading_cd_and_windows_flags_are_understood() =>
        Assert.Empty(Review(Step("build", "cd src && dotnet build /p:TreatWarningsAsErrors=true", "src/index.js")));

    [Fact]
    public void A_missing_or_relative_project_folder_is_refused()
    {
        Assert.Contains("missing", Assert.Single(PlanReview.Problems(null, [Step("x", "npm test")], Installed)));
        Assert.Contains("not a full path", Assert.Single(PlanReview.Problems("project", [Step("x", "npm test")], Installed)));
    }

    [Fact]
    public void A_new_project_folder_is_fine_when_its_parent_exists() =>
        Assert.Empty(PlanReview.Problems(Path.Combine(_project, "new-app"), [Step("x", "npm test")], Installed));
}