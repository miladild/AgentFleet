using System.Text;

namespace AgentFleet.Tests;

// The preload that names what a node test run still has open once its tests are done: how it travels in a command, which
// commands get it, and how its marker line is read back.
public sealed class OpenHandlePreloadTests
{
    [Fact]
    public void The_data_url_decodes_back_to_the_preload_source()
    {
        string url = OpenHandlePreload.DataUrl();

        Assert.StartsWith("data:text/javascript;base64,", url, StringComparison.Ordinal);
        string source = Encoding.UTF8.GetString(Convert.FromBase64String(url["data:text/javascript;base64,".Length..]));
        Assert.Equal(OpenHandlePreload.Source, source);
        Assert.Contains("process.getActiveResourcesInfo()", source, StringComparison.Ordinal);
        Assert.Contains("[open-after-tests]", source, StringComparison.Ordinal);
        Assert.Contains(".unref()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_argument_puts_the_data_url_in_double_quotes_after_import()
    {
        Assert.Equal($"--import \"{OpenHandlePreload.DataUrl()}\"", OpenHandlePreload.Argument());
    }

    [Theory]
    [InlineData("node --test a.test.ts")]
    [InlineData("node --import tsx --test \"test/**/*.test.ts\"")]
    [InlineData("node --test a.test.ts 2>&1 | tail -20")]
    [InlineData("cd app && node --test x.test.js")]
    [InlineData("tsx --test x.test.ts")]
    [InlineData("npx tsx --test x.test.ts")]
    [InlineData("npm run build; node --test a.test.js")]
    public void Commands_that_run_nodes_test_runner_are_recognised(string command) =>
        Assert.True(OpenHandlePreload.IsNodeTestRun(command));

    [Theory]
    [InlineData("npm run build")]
    [InlineData("ls")]
    [InlineData("node app.js")]
    [InlineData("node --test-name-pattern=x app.js")]
    [InlineData("npm test | tail -20")]
    [InlineData("echo \"node --test\"")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_commands_are_not_node_test_runs(string? command) =>
        Assert.False(OpenHandlePreload.IsNodeTestRun(command));

    [Fact]
    public void A_plain_node_test_command_gets_the_preload_right_after_node() =>
        Assert.Equal($"node {OpenHandlePreload.Argument()} --test a.test.ts", OpenHandlePreload.WithPreload("node --test a.test.ts"));

    [Fact]
    public void A_command_that_already_imports_a_loader_keeps_it_and_gets_the_preload_before_it() =>
        Assert.Equal($"node {OpenHandlePreload.Argument()} --import tsx --test \"test/**/*.test.ts\"",
            OpenHandlePreload.WithPreload("node --import tsx --test \"test/**/*.test.ts\""));

    [Fact]
    public void A_piped_command_gets_the_preload_in_its_test_command_and_the_pipe_is_kept() =>
        Assert.Equal($"node {OpenHandlePreload.Argument()} --test a.test.ts 2>&1 | tail -20",
            OpenHandlePreload.WithPreload("node --test a.test.ts 2>&1 | tail -20"));

    [Fact]
    public void A_chained_command_gets_the_preload_in_its_test_command_after_the_first_one() =>
        Assert.Equal($"cd app && node {OpenHandlePreload.Argument()} --test x.test.js",
            OpenHandlePreload.WithPreload("cd app && node --test x.test.js"));

    [Fact]
    public void Only_the_first_test_command_of_a_chain_gets_the_preload()
    {
        string result = OpenHandlePreload.WithPreload("node --test a.test.js; node --test b.test.js")!;

        Assert.Equal($"node {OpenHandlePreload.Argument()} --test a.test.js; node --test b.test.js", result);
    }

    [Fact]
    public void A_command_run_through_npx_and_tsx_gets_the_preload_after_tsx() =>
        Assert.Equal($"npx tsx {OpenHandlePreload.Argument()} --test x.test.ts", OpenHandlePreload.WithPreload("npx tsx --test x.test.ts"));

    [Fact]
    public void A_tsx_test_command_gets_the_preload_after_tsx() =>
        Assert.Equal($"tsx {OpenHandlePreload.Argument()} --test x.test.ts", OpenHandlePreload.WithPreload("tsx --test x.test.ts"));

    [Theory]
    [InlineData("node --test --test-force-exit a.test.ts")]
    [InlineData("node --test a.test.ts --test-force-exit | tail -3")]
    [InlineData("npm run build")]
    [InlineData("ls")]
    [InlineData("node --import \"data:text/javascript;base64,AAAA\" --test a.test.ts")]
    [InlineData("node --test a.test.ts # open-after-tests")]
    [InlineData("   ")]
    [InlineData("")]
    public void Commands_that_end_by_themselves_or_run_no_node_test_or_have_the_preload_are_unchanged(string command) =>
        Assert.Equal(command, OpenHandlePreload.WithPreload(command));

    [Fact]
    public void A_command_that_already_has_this_preload_is_unchanged()
    {
        string once = OpenHandlePreload.WithPreload("node --test a.test.ts")!;

        Assert.Equal(once, OpenHandlePreload.WithPreload(once));
    }

    [Fact]
    public void No_command_stays_no_command() =>
        Assert.Null(OpenHandlePreload.WithPreload(null));

    [Fact]
    public void A_command_with_the_preload_is_recognised_as_carrying_it()
    {
        Assert.True(OpenHandlePreload.IsLoadedBy(OpenHandlePreload.WithPreload("node --test a.test.ts")));
        Assert.False(OpenHandlePreload.IsLoadedBy("node --test a.test.ts"));
        Assert.False(OpenHandlePreload.IsLoadedBy(null));
    }

    [Fact]
    public void The_resource_is_the_text_after_the_marker_on_its_line()
    {
        string output = "ok 1 - first\n\n[open-after-tests] TCPServerWrap, FSReqCallback\n";

        Assert.Equal("TCPServerWrap, FSReqCallback", OpenHandlePreload.ResourceFrom(output));
    }

    [Fact]
    public void The_resource_of_the_first_marker_line_is_the_one_read()
    {
        Assert.Equal("TCPServerWrap", OpenHandlePreload.ResourceFrom("[open-after-tests] TCPServerWrap\r\n[open-after-tests] Other\r\n"));
    }

    [Theory]
    [InlineData("ok 1 - first\n# pass 1\n")]
    [InlineData("a line that mentions [open-after-tests] in the middle")]
    [InlineData("")]
    [InlineData(null)]
    public void An_output_without_the_marker_line_has_no_resource(string? output) =>
        Assert.Null(OpenHandlePreload.ResourceFrom(output));

    [Fact]
    public void A_marker_line_is_recognised_by_its_start()
    {
        Assert.True(OpenHandlePreload.IsMarkerLine("[open-after-tests] TCPServerWrap"));
        Assert.False(OpenHandlePreload.IsMarkerLine("  [open-after-tests] TCPServerWrap"));
        Assert.False(OpenHandlePreload.IsMarkerLine(null));
    }

    [Fact]
    public void The_explanation_names_the_resource_says_the_tests_ran_to_the_end_and_not_to_run_it_again()
    {
        string text = OpenHandlePreload.Explanation("TCPServerWrap");

        Assert.Contains("TCPServerWrap", text, StringComparison.Ordinal);
        Assert.Contains("tests ran to the end", text, StringComparison.Ordinal);
        Assert.Contains("ended for that reason", text, StringComparison.Ordinal);
        Assert.Contains("server.close()", text, StringComparison.Ordinal);
        Assert.Contains("rather than adding flags", text, StringComparison.Ordinal);
        Assert.Contains("Do not run the same command again unchanged", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("")]
    [InlineData(null)]
    public void The_explanation_still_reads_well_when_no_resource_is_named(string? resource)
    {
        string text = OpenHandlePreload.Explanation(resource);

        Assert.Contains("something is still open", text, StringComparison.Ordinal);
        Assert.DoesNotContain("still open ()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(nothing)", text, StringComparison.Ordinal);
    }
}
