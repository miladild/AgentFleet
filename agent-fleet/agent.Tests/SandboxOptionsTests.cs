using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class SandboxOptionsTests
{
    private static IConfiguration Env(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    [Fact]
    public void With_no_configuration_and_no_docker_the_sandbox_is_off()
    {
        DockerSandboxOptions options = DockerSandboxOptions.Resolve(null, Env(), dockerOnPath: () => false);

        Assert.Equal(SandboxMode.Off, options.Mode);
        Assert.Contains("Docker was not found", options.Summary);
    }

    [Fact]
    public void With_no_configuration_but_docker_installed_the_sandbox_runs_locally()
    {
        DockerSandboxOptions options = DockerSandboxOptions.Resolve(null, Env(), dockerOnPath: () => true);

        Assert.Equal(SandboxMode.Local, options.Mode);
    }

    [Fact]
    public void An_ssh_host_in_the_config_selects_ssh_and_takes_its_own_key()
    {
        string key = Path.GetTempFileName();
        try
        {
            var config = new FleetSandboxConfig(Host: "10.0.0.9", User: "builder", KeyPath: key, Sudo: true, Port: 2222);

            DockerSandboxOptions options = DockerSandboxOptions.Resolve(config, Env(), dockerOnPath: () => true);

            Assert.Equal(SandboxMode.Ssh, options.Mode);
            Assert.Equal("10.0.0.9", options.Host);
            Assert.Equal("builder", options.User);
            Assert.Equal(2222, options.Port);
            Assert.True(options.UseSudo);
        }
        finally
        {
            File.Delete(key);
        }
    }

    [Fact]
    public void Environment_variables_fill_in_what_the_file_leaves_out()
    {
        string key = Path.GetTempFileName();
        try
        {
            DockerSandboxOptions options = DockerSandboxOptions.Resolve(
                new FleetSandboxConfig(),
                Env(("SANDBOX_SSH_HOST", "box.lan"), ("SANDBOX_SSH_KEY_PATH", key), ("SANDBOX_SSH_USER", "ci")));

            Assert.Equal(SandboxMode.Ssh, options.Mode);
            Assert.Equal("box.lan", options.Host);
            Assert.Equal("ci", options.User);
            Assert.False(options.UseSudo);
        }
        finally
        {
            File.Delete(key);
        }
    }

    [Fact]
    public void Off_wins_even_when_docker_and_a_host_are_present()
    {
        DockerSandboxOptions options = DockerSandboxOptions.Resolve(
            new FleetSandboxConfig(Mode: "off", Host: "10.0.0.9"),
            Env(),
            dockerOnPath: () => true);

        Assert.Equal(SandboxMode.Off, options.Mode);
    }

    [Fact]
    public void Ssh_mode_without_a_host_is_refused_with_a_message_that_says_what_to_set()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerSandboxOptions.Resolve(new FleetSandboxConfig(Mode: "ssh"), Env()));

        Assert.Contains("sandbox.host", exception.Message);
    }

    [Fact]
    public void A_missing_ssh_key_is_refused_naming_the_path()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-key-" + Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerSandboxOptions.Resolve(new FleetSandboxConfig(Host: "h", KeyPath: missing), Env()));

        Assert.Contains(missing, exception.Message);
    }

    [Fact]
    public void An_unknown_mode_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DockerSandboxOptions.Resolve(new FleetSandboxConfig(Mode: "kubernetes"), Env()));
    }

    [Fact]
    public void The_timeout_defaults_to_twenty_seconds_and_is_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), DockerSandboxOptions.Resolve(null, Env(), () => false).ExecutionTimeout);
        Assert.Throws<InvalidOperationException>(() =>
            DockerSandboxOptions.Resolve(new FleetSandboxConfig(TimeoutSeconds: 500), Env(), () => false));
    }

    [Fact]
    public void The_defaults_name_no_machine_or_person()
    {
        // Regression: the defaults used to be one specific LAN host, user name and key file. The file may name
        // no address and no home folder; the user and the key's folder come from the running account.
        string source = File.ReadAllText(Path.Combine(FindAgentFolder(), "DockerSandboxOptions.cs"));

        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", source);
        Assert.DoesNotMatch(@"(?i)[a-z]:\\\\users\\\\|[a-z]:\\users\\|/home/|/Users/", source);
        Assert.Contains("Environment.UserName", source);
        Assert.Contains("SpecialFolder.UserProfile", source);
    }

    private static string FindAgentFolder()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "agent", "DockerSandboxOptions.cs")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory is null
            ? throw new InvalidOperationException("Could not find the agent folder above the test binaries.")
            : Path.Combine(directory, "agent");
    }
}
