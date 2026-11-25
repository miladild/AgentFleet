namespace AgentFleet.Tests;

public sealed class OllamaReleaseTests
{
    [Theory]
    [InlineData("0.12.3", 0, 12, 3, null)]
    [InlineData("v0.12.3", 0, 12, 3, null)]
    [InlineData("0.13.0-rc1", 0, 13, 0, "rc1")]
    [InlineData(" 1.2.3 ", 1, 2, 3, null)]
    public void A_version_is_read_with_or_without_a_leading_v(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(OllamaVersion.TryParse(text, out OllamaVersion version));
        Assert.Equal(new OllamaVersion(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("latest")]
    [InlineData("1.0.0;rm -rf /")]
    [InlineData("1.0.0 and more")]
    [InlineData("1.0.x")]
    public void Anything_else_is_not_a_version(string text) => Assert.False(OllamaVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.12.0", "0.12.1", true)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("v0.11.9", "0.12.0", true)]
    [InlineData("0.12.0-rc1", "0.12.0", true)]
    [InlineData("0.12.0-rc1", "0.12.0-rc2", true)]
    [InlineData("0.12.1", "0.12.1", false)]
    [InlineData("0.13.0", "0.12.9", false)]
    [InlineData("0.12.0", "0.12.0-rc1", false)]
    [InlineData("1.0.0", "0.99.99", false)]
    [InlineData("not a version", "0.12.0", false)]
    [InlineData("0.12.0", null, false)]
    public void An_update_is_only_offered_for_a_strictly_older_version(string? installed, string? latest, bool older) =>
        Assert.Equal(older, OllamaVersion.IsOlder(installed, latest));

    [Theory]
    [InlineData("١.٢.٣")]
    [InlineData("0.1٣.0")]
    [InlineData("v٠.١.٢")]
    public void Digits_outside_ASCII_are_not_a_version_and_do_not_throw(string text)
    {
        Assert.False(OllamaVersion.TryParse(text, out _));
        Assert.False(OllamaUpdateAssets.IsValidVersion(text));
        Assert.False(OllamaVersion.IsOlder(text, "0.13.0"));
    }

    [Fact]
    public void A_stable_release_is_read_with_its_date_and_the_digest_of_each_file()
    {
        OllamaRelease release = OllamaUpdateAssets.ParseRelease(OllamaUpdateFixture.Release("v0.13.0"));

        Assert.Equal("0.13.0", release.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), release.PublishedAtUtc);
        Assert.Equal("sha256:" + OllamaUpdateFixture.InstallerSha, release.Asset("OllamaSetup.exe")!.Digest);
        Assert.Equal("sha256:" + OllamaUpdateFixture.LinuxSha, release.Asset("ollama-linux-amd64.tar.zst")!.Digest);
    }

    [Fact]
    public void A_missing_or_malformed_digest_is_dropped_so_the_file_cannot_be_installed()
    {
        OllamaRelease release = OllamaUpdateAssets.ParseRelease(
            OllamaUpdateFixture.Release("0.13.0", windowsDigest: "none", linuxDigest: "sha256:not-hex"));

        Assert.Null(release.Asset("OllamaSetup.exe")!.Digest);
        Assert.Null(release.Asset("ollama-linux-amd64.tar.zst")!.Digest);
    }

    [Fact]
    public void A_digest_in_capitals_is_read_as_lower_case()
    {
        OllamaRelease release = OllamaUpdateAssets.ParseRelease(
            OllamaUpdateFixture.Release("0.13.0", linuxDigest: "sha256:" + new string('A', 64)));

        Assert.Equal("sha256:" + new string('a', 64), release.Asset("ollama-linux-amd64.tar.zst")!.Digest);
    }

    [Fact]
    public void A_file_the_release_does_not_list_is_not_found()
    {
        OllamaRelease release = OllamaUpdateAssets.ParseRelease(OllamaUpdateFixture.Release("0.13.0", linuxAsset: false));

        Assert.Null(release.Asset("ollama-linux-amd64.tar.zst"));
        Assert.NotNull(release.Asset("OllamaSetup.exe"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_draft_or_a_pre_release_is_never_offered(bool draft, bool prerelease) =>
        Assert.Throws<OllamaReleaseException>(() => OllamaUpdateAssets.ParseRelease(OllamaUpdateFixture.Release("v0.13.0", draft, prerelease)));

    [Theory]
    [InlineData("v0.13.0-rc1")]
    [InlineData("latest")]
    [InlineData("v1.0.0;rm -rf /")]
    [InlineData("")]
    public void A_tag_that_is_not_a_plain_version_is_refused(string tag) =>
        Assert.Throws<OllamaReleaseException>(() => OllamaUpdateAssets.ParseRelease(OllamaUpdateFixture.Release(tag)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void An_answer_that_is_not_a_release_is_refused(string json) =>
        Assert.Throws<OllamaReleaseException>(() => OllamaUpdateAssets.ParseRelease(json));

    [Theory]
    [InlineData("0.13.0", "OllamaSetup.exe", "https://github.com/ollama/ollama/releases/download/v0.13.0/OllamaSetup.exe")]
    [InlineData("12.4.100", "ollama-linux-amd64.tar.zst", "https://github.com/ollama/ollama/releases/download/v12.4.100/ollama-linux-amd64.tar.zst")]
    [InlineData("0.13.0", "Ollama-darwin.zip", "https://github.com/ollama/ollama/releases/download/v0.13.0/Ollama-darwin.zip")]
    public void The_download_address_is_built_from_a_plain_version_and_a_listed_file(string version, string asset, string expected)
    {
        Assert.True(OllamaUpdateAssets.TryDownloadUrl(version, asset, out Uri url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData("1.0.0;rm -rf", "OllamaSetup.exe")]
    [InlineData("1.0.0\n", "OllamaSetup.exe")]
    [InlineData(" 1.0.0", "OllamaSetup.exe")]
    [InlineData("1.0", "OllamaSetup.exe")]
    [InlineData("../1.0.0", "OllamaSetup.exe")]
    [InlineData("1.0.0-rc1", "OllamaSetup.exe")]
    [InlineData("1.0.0$(id)", "ollama-linux-amd64.tar.zst")]
    [InlineData("1.0.0", "evil.exe")]
    [InlineData("1.0.0", "../OllamaSetup.exe")]
    [InlineData("1.0.0", "ollama-linux-arm64.tar.zst")]
    [InlineData("1.0.0", "OllamaSetup.exe; id")]
    [InlineData("1.0.0", "")]
    [InlineData(null, "OllamaSetup.exe")]
    public void A_version_or_file_outside_the_allow_list_gets_no_address(string? version, string asset) =>
        Assert.False(OllamaUpdateAssets.TryDownloadUrl(version, asset, out _));

    [Fact]
    public void Manual_steps_name_the_verified_download_its_sha_256_and_the_silent_install_for_each_platform()
    {
        OllamaRelease release = OllamaUpdateAssets.ParseRelease(OllamaUpdateFixture.Release("0.13.0"));

        IReadOnlyList<OllamaManualStep> windows = OllamaUpdateService.ManualSteps(release, "windows");
        OllamaManualStep step = Assert.Single(windows);
        Assert.Equal("https://github.com/ollama/ollama/releases/download/v0.13.0/OllamaSetup.exe", step.Url);
        Assert.Equal(OllamaUpdateFixture.InstallerSha, step.Sha256);
        Assert.Equal([".\\OllamaSetup.exe /SILENT /NORESTART"], step.Commands);

        OllamaManualStep linux = Assert.Single(OllamaUpdateService.ManualSteps(release, "linux"));
        Assert.Contains($"echo '{OllamaUpdateFixture.LinuxSha}  ollama-linux-amd64.tar.zst' | sha256sum -c -", linux.Commands);
        Assert.Contains("sudo tar -I zstd -xf ollama-linux-amd64.tar.zst -C /usr", linux.Commands);

        Assert.Equal(["windows", "linux", "macos"], OllamaUpdateService.ManualSteps(release, null).Select(item => item.Platform));
    }
}
