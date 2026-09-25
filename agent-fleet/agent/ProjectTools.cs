using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// More small, precise tools for local models: see a whole project at a glance with how to build and test it,
/// move or delete one file, and call an HTTP API. Same posture as the other hub tools: no path allowlist and no
/// approval step, by the owner's choice.
/// </summary>
internal static partial class ProjectTools
{
    private const int MaxOverviewLines = 350;
    private const int MaxResponseCharacters = 20_000;

    // What tells the model how a project is built and tested, in the order worth reading.
    private static readonly (string Pattern, string How)[] ProjectFiles =
    [
        ("*.sln", "dotnet build / dotnet test on the solution"),
        ("*.slnx", "dotnet build / dotnet test on the solution"),
        ("*.csproj", "dotnet build, dotnet test (test projects), dotnet run (apps)"),
        ("pyproject.toml", "Python project: pip install -e . , then pytest if there are tests"),
        ("requirements.txt", "Python: pip install -r requirements.txt"),
        ("Cargo.toml", "cargo build, cargo test"),
        ("go.mod", "go build ./..., go test ./..."),
        ("pom.xml", "mvn package, mvn test"),
        ("build.gradle", "gradle build, gradle test"),
        ("build.gradle.kts", "gradle build, gradle test"),
        ("CMakeLists.txt", "cmake -B build, cmake --build build"),
        ("Dockerfile", "docker build ."),
        ("docker-compose.yml", "docker compose up"),
        ("compose.yaml", "docker compose up"),
    ];

    /// <summary>A folder tree (dependency and build folders skipped) and what the project files say about building and testing.</summary>
    public static string ProjectOverview(string path, int? depth)
    {
        string root = HubFileSystemTools.Resolve(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(root))
        {
            return File.Exists(root) ? $"Error: {root} is a file. Give the project's folder." : $"Error: folder not found: {root}";
        }

        int maxDepth = Math.Clamp(depth ?? 3, 1, 6);
        var lines = new List<string> { $"{root} (folders {maxDepth} deep; {string.Join(", ", HubFileSystemTools.SkippedFolderNames.Order().Take(8))} and similar are skipped)" };
        bool truncated = false;
        Walk(root, 0);

        var hints = new List<string>();
        foreach ((string pattern, string how) in ProjectFiles)
        {
            string[] found = SafeFiles(root, pattern);
            if (found.Length > 0)
            {
                hints.Add($"- {string.Join(", ", found.Take(3).Select(Path.GetFileName))}: {how}");
            }
        }

        string packageJson = Path.Combine(root, "package.json");
        if (File.Exists(packageJson))
        {
            hints.Insert(0, $"- package.json: {DescribePackageJson(packageJson)}");
        }

        string makefile = Path.Combine(root, "Makefile");
        if (File.Exists(makefile))
        {
            string[] targets = SafeReadLines(makefile)
                .Select(line => MakeTargetPattern().Match(line))
                .Where(match => match.Success && !match.Groups[1].Value.StartsWith('.'))
                .Select(match => match.Groups[1].Value).Distinct().Take(12).ToArray();
            hints.Add($"- Makefile: make {(targets.Length > 0 ? string.Join(", make ", targets) : "(no targets found)")}");
        }

        string? readme = new[] { "README.md", "README", "readme.md", "README.txt" }.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
        var result = new StringBuilder(string.Join('\n', lines));
        if (truncated)
        {
            result.Append($"\n[tree cut at {MaxOverviewLines} lines: use a smaller depth or look inside one folder]");
        }

        result.Append("\n\nHow to build and test, from the project files:\n");
        result.Append(hints.Count > 0 ? string.Join('\n', hints) : "- nothing recognised (no package.json, .csproj, pyproject.toml, Cargo.toml, go.mod, pom.xml, Makefile...)");
        if (readme is not null)
        {
            string intro = string.Join(' ', SafeReadLines(readme).Where(line => !string.IsNullOrWhiteSpace(line)).Take(6)).Trim();
            result.Append($"\n\n{Path.GetFileName(readme)} begins: {(intro.Length > 500 ? intro[..500] + "..." : intro)}");
        }

        return result.ToString();

        void Walk(string directory, int level)
        {
            if (truncated)
            {
                return;
            }

            string[] folders;
            string[] files;
            try
            {
                folders = Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
                files = Directory.GetFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            string indent = new(' ', level * 2);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                if (HubFileSystemTools.SkippedFolderNames.Contains(name))
                {
                    continue;
                }

                if (!Add($"{indent}{name}/" + (level + 1 >= maxDepth ? $" ({CountEntries(folder)})" : string.Empty)))
                {
                    return;
                }

                if (level + 1 < maxDepth)
                {
                    Walk(folder, level + 1);
                }
            }

            foreach (string file in files)
            {
                if (!Add($"{indent}{Path.GetFileName(file)}"))
                {
                    return;
                }
            }
        }

        bool Add(string line)
        {
            if (lines.Count >= MaxOverviewLines)
            {
                truncated = true;
                return false;
            }

            lines.Add(line);
            return true;
        }
    }

    private static string CountEntries(string folder)
    {
        try
        {
            int files = Directory.EnumerateFiles(folder).Take(1000).Count();
            int folders = Directory.EnumerateDirectories(folder).Take(1000).Count();
            return $"{files} files, {folders} folders";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return "not readable";
        }
    }

    private static string DescribePackageJson(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string manager = File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "pnpm-lock.yaml")) ? "pnpm"
                : File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "yarn.lock")) ? "yarn" : "npm";
            if (document.RootElement.TryGetProperty("scripts", out JsonElement scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                string[] names = scripts.EnumerateObject().Select(script => script.Name).Take(15).ToArray();
                return names.Length > 0
                    ? $"{manager} install, then {manager} run <script>; scripts: {string.Join(", ", names)}"
                    : $"{manager} install (no scripts)";
            }

            return $"{manager} install (no scripts)";
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return "present but not readable as JSON";
        }
    }

    private static string[] SafeFiles(string root, string pattern)
    {
        try
        {
            return Directory.GetFiles(root, pattern);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string[] SafeReadLines(string path)
    {
        try
        {
            return File.ReadLines(path).Take(200).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+)\s*:(?!=)")]
    private static partial Regex MakeTargetPattern();

    public static string MoveFile(string source, string destination, bool? overwrite)
    {
        string from = HubFileSystemTools.Resolve(source);
        string to = HubFileSystemTools.Resolve(destination);
        bool isFile = File.Exists(from);
        if (!isFile && !Directory.Exists(from))
        {
            return $"Error: not found: {from}";
        }

        if (Directory.Exists(to) && isFile)
        {
            to = Path.Combine(to, Path.GetFileName(from));
        }

        if ((File.Exists(to) || Directory.Exists(to)) && overwrite != true)
        {
            return $"Error: {to} already exists. Pass overwrite true to replace it (files only).";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (isFile)
            {
                File.Move(from, to, overwrite == true);
            }
            else
            {
                Directory.Move(from, to);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Error: could not move {from}: {exception.Message}";
        }

        return $"Moved {from} to {to}.";
    }

    /// <summary>Deletes one file, or an empty folder. A folder with contents is refused: that is a bigger decision.</summary>
    public static string DeleteFile(string path)
    {
        string target = HubFileSystemTools.Resolve(path);
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
                return $"Deleted {target}.";
            }

            if (Directory.Exists(target))
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    return $"Error: {target} is a folder that is not empty. delete_file removes single files and empty folders only; " +
                           "ask the user before removing a whole folder.";
                }

                Directory.Delete(target);
                return $"Deleted the empty folder {target}.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Error: could not delete {target}: {exception.Message}";
        }

        return $"Error: not found: {target}";
    }

    /// <summary>
    /// Calls an HTTP API and returns the status, the main headers and the body as it is (JSON is pretty-printed), for
    /// trying out a local server the user is building or any REST API.
    /// </summary>
    public static async Task<string> HttpRequestAsync(
        string url,
        string? method,
        string? headers,
        string? body,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return "Error: url must be an absolute http or https address.";
        }

        var httpMethod = new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant());
        using var request = new HttpRequestMessage(httpMethod, uri);
        string? contentType = null;
        // One "Name: value" per line: easier for a small model to write than a nested object.
        foreach (string line in (headers ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return $"Error: headers are one \"Name: value\" per line; could not read \"{line}\".";
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
            else if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                return $"Error: the header {name} cannot be set on a request.";
            }
        }

        if (body is not null)
        {
            contentType ??= body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain";
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            HttpClient client = httpClientFactory.CreateClient(WebTools.HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(text);
                    text = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (JsonException)
                {
                    // Leave it as it came.
                }
            }

            var result = new StringBuilder($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} in {stopwatch.ElapsedMilliseconds} ms\n");
            foreach (string header in new[] { "Content-Type", "Location", "Content-Length" })
            {
                if (response.Content.Headers.TryGetValues(header, out IEnumerable<string>? contentValues) ||
                    response.Headers.TryGetValues(header, out contentValues))
                {
                    result.Append($"{header}: {string.Join(", ", contentValues)}\n");
                }
            }

            result.Append('\n');
            result.Append(text.Length > MaxResponseCharacters
                ? text[..MaxResponseCharacters] + $"\n\n[body cut at {MaxResponseCharacters} of {text.Length} characters]"
                : text);
            return result.ToString();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException)
        {
            logger.LogInformation("http_request to {Url} failed: {Message}", uri, exception.Message);
            return $"Error: {httpMethod} {uri} failed: {exception.Message}";
        }
    }
}
