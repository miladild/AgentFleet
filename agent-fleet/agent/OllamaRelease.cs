using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>A release number such as 0.12.3 or 0.13.0-rc1, compared the way semantic versioning orders them.</summary>
internal readonly partial record struct OllamaVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<OllamaVersion>
{
    // [0-9], not \d: \d also matches digits of other scripts, which int.Parse then refuses.
    [GeneratedRegex(@"^v?([0-9]{1,6})\.([0-9]{1,6})\.([0-9]{1,6})(?:-([0-9A-Za-z][0-9A-Za-z.-]{0,63}))?(?:\+[0-9A-Za-z.-]{1,64})?\z")]
    private static partial Regex Pattern();

    /// <summary>Reads "0.12.3", "v0.12.3" or "0.13.0-rc1". Anything else, including trailing text, is refused.</summary>
    public static bool TryParse(string? text, out OllamaVersion version)
    {
        version = default;
        Match match = Pattern().Match((text ?? string.Empty).Trim());
        if (!match.Success)
        {
            return false;
        }

        version = new OllamaVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public bool IsPreRelease => PreRelease is not null;

    public int CompareTo(OllamaVersion other)
    {
        int numbers = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (numbers != 0)
        {
            return numbers;
        }

        // A pre-release comes before the release it leads up to.
        if (PreRelease is null || other.PreRelease is null)
        {
            return PreRelease is null ? (other.PreRelease is null ? 0 : 1) : -1;
        }

        string[] left = PreRelease.Split('.');
        string[] right = other.PreRelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = long.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out long leftNumber);
            bool rightNumeric = long.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out long rightNumber);
            int part = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.CompareOrdinal(left[index], right[index]);
            if (part != 0)
            {
                return part;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>True only when <paramref name="installed"/> is strictly older than <paramref name="latest"/>: never a downgrade, never a reinstall of the same version.</summary>
    public static bool IsOlder(string? installed, string? latest) =>
        TryParse(installed, out OllamaVersion current) && TryParse(latest, out OllamaVersion target) && current.CompareTo(target) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? string.Empty : "-" + PreRelease)}";
}

/// <summary>One file of a release: its name and the SHA-256 GitHub reports for it ("sha256:" and 64 hex digits), when it reports one.</summary>
internal sealed record OllamaReleaseAsset(string Name, string? Digest);

/// <summary>The newest stable release as GitHub describes it.</summary>
internal sealed record OllamaRelease(string Version, DateTimeOffset? PublishedAtUtc, IReadOnlyList<OllamaReleaseAsset> Assets)
{
    public OllamaReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));
}

/// <summary>Why a release answer was not used.</summary>
internal sealed class OllamaReleaseException(string message) : Exception(message);

/// <summary>
/// The files an update may download, and the one place their addresses are built. A file name must be on the fixed list
/// and a version must be three plain numbers, so nothing a request, a release or a configuration file says can change
/// where an installer comes from or what the command that runs it looks like.
/// </summary>
internal static partial class OllamaUpdateAssets
{
    public const string WindowsInstaller = "OllamaSetup.exe";
    public const string LinuxAmd64 = "ollama-linux-amd64.tar.zst";
    public const string MacApp = "Ollama-darwin.zip";

    public static readonly IReadOnlyList<string> Allowed = [WindowsInstaller, LinuxAmd64, MacApp];

    public const string LatestReleaseUrl = "https://api.github.com/repos/ollama/ollama/releases/latest";

    [GeneratedRegex(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();

    /// <summary>Three plain numbers: a stable release. Not "1.0.0;rm -rf", not "1.0.0\n", not a pre-release.</summary>
    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    public static bool IsAllowedAsset(string? name) => name is not null && Allowed.Contains(name, StringComparer.Ordinal);

    /// <summary>The digest in the one form it is used in, lower case, or null when it is anything else.</summary>
    public static string? NormalizeDigest(string? digest)
    {
        string lower = (digest ?? string.Empty).Trim().ToLowerInvariant();
        return DigestPattern().IsMatch(lower) ? lower : null;
    }

    /// <summary>The 64 hex digits of a digest that passed <see cref="NormalizeDigest"/>.</summary>
    public static string Hex(string digest) => digest["sha256:".Length..];

    public static bool TryDownloadUrl(string? version, string? assetName, out Uri url)
    {
        url = null!;
        if (!IsValidVersion(version) || !IsAllowedAsset(assetName))
        {
            return false;
        }

        url = new Uri($"https://github.com/ollama/ollama/releases/download/v{version}/{assetName}");
        return true;
    }

    /// <summary>
    /// Reads the answer of GitHub's "latest release" call. Drafts and pre-releases are refused, as is a tag that is not a
    /// plain version. Digests that are not 64 hex digits are dropped, so an asset without a usable digest cannot be installed.
    /// </summary>
    public static OllamaRelease ParseRelease(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OllamaReleaseException("GitHub's answer was not a release.");
            }

            if (Flag(root, "draft") || Flag(root, "prerelease"))
            {
                throw new OllamaReleaseException("GitHub's latest release is a draft or a pre-release, so it is not offered.");
            }

            string tag = Text(root, "tag_name") ?? string.Empty;
            string version = tag.StartsWith('v') ? tag[1..] : tag;
            if (!IsValidVersion(version))
            {
                throw new OllamaReleaseException($"GitHub's latest release has a tag that is not a plain version ({Shorten(tag)}), so it is not offered.");
            }

            DateTimeOffset? published = Text(root, "published_at") is { } stamp &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
                    ? parsed
                    : null;

            List<OllamaReleaseAsset> assets = [];
            if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray().Take(500))
                {
                    if (item.ValueKind == JsonValueKind.Object && Text(item, "name") is { Length: > 0 and <= 200 } name)
                    {
                        assets.Add(new OllamaReleaseAsset(name, NormalizeDigest(Text(item, "digest"))));
                    }
                }
            }

            return new OllamaRelease(version, published, assets);
        }
        catch (JsonException)
        {
            throw new OllamaReleaseException("GitHub's answer could not be read.");
        }
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "...";
}
