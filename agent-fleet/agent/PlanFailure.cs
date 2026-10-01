using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

internal enum FailureClass
{
    Infra,
    Environment,
    CheckDefect,
    CodeProgress,
    Stall,
    NoOp,
    Unknown
}

internal sealed record FailureFingerprint(string Value, int Size, IReadOnlyList<string> Items);

internal sealed record FailureRound(
    string? Signature,
    int SignatureSize,
    int FilesChanged,
    bool EditToolCalled);

internal static partial class PlanFailure
{
    [GeneratedRegex(@"\b(?:error|fatal error)\s+(?<code>(?:CS|TS|MSB|NETSDK)\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerErrorCode();

    [GeneratedRegex(@"^\s*(?:FAILED|FAIL(?:ED)?|✖|✗|×)\s+(?<name>.+?)\s*(?:\(\s*\d+(?:\.\d+)?\s*(?:ms|s)\s*\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestLine();

    [GeneratedRegex(@"^\s*Failed\s+(?<name>[\w./\\:<>` -]+?)(?:\s+\[\s*\d+(?:\.\d+)?\s*(?:ms|s)\s*\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex XunitFailedLine();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:ms|msec|milliseconds?|seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ElapsedTime();

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|/)[^\s:]+")]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"\s+in\s+\d+(?:\.\d+)?\s*(?:ms|s|seconds?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationSuffix();

    [GeneratedRegex(@"(?:ParserError|UnexpectedToken|MissingEndCurlyBrace|SyntaxError|syntax error|unexpected token)", RegexOptions.IgnoreCase)]
    private static partial Regex CheckSyntaxError();

    [GeneratedRegex(@"(?:llama-server(?:\.exe)?[^\r\n]{0,80}not found|not found[^\r\n]{0,80}llama-server)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingLlamaServer();

    [GeneratedRegex(@"(?:Worker workspace unavailable:|access is denied|permission denied|unauthorizedaccess|no compatible \.NET SDK|no \.NET SDKs? were found|NETSDK1045|MSB4236)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentFailure();

    [GeneratedRegex(@"(?:command not found|is not recognized as an internal or external command|file not found|cannot find the file|the system cannot find the path)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingCheckInput();

    public static FailureFingerprint FailureSignature(string? checkOutput)
    {
        string output = AnsiEscape().Replace(checkOutput ?? string.Empty, string.Empty);
        var items = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (CompilerErrorCode().Match(line) is { Success: true } code)
            {
                items.Add(code.Groups["code"].Value.ToUpperInvariant());
            }

            Match failed = FailedTestLine().Match(line);
            if (!failed.Success) failed = XunitFailedLine().Match(line);
            if (failed.Success)
            {
                string name = NormalizeLine(failed.Groups["name"].Value);
                if (name.Length > 0) items.Add(name);
            }
        }

        if (items.Count > 0)
        {
            string[] names = items.Take(40).ToArray();
            return new FailureFingerprint(string.Join('\n', names), names.Length, names);
        }

        string[] normalized = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeLine)
            .Where(line => line.Length > 0 && !IsSummaryLine(line))
            .TakeLast(40)
            .ToArray();
        if (normalized.Length == 0) return new FailureFingerprint(string.Empty, 0, []);

        string stable = string.Join('\n', normalized);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant();
        return new FailureFingerprint($"sha256:{hash}", 1, []);
    }

    public static FailureClass Classify(
        Exception? exception,
        string? checkOutput,
        IReadOnlyList<FailureRound> roundHistory,
        int filesChanged = 0,
        bool editToolCalled = false)
    {
        string evidence = string.Join('\n', new[] { exception?.ToString(), checkOutput }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (EnvironmentFailure().IsMatch(evidence)) return FailureClass.Environment;
        if (IsInfrastructure(exception, evidence))
        {
            return FailureClass.Infra;
        }

        if (exception is not null)
        {
            return exception is UnauthorizedAccessException || EnvironmentFailure().IsMatch(evidence)
                ? FailureClass.Environment
                : FailureClass.Unknown;
        }

        if (CheckSyntaxError().IsMatch(evidence)) return FailureClass.CheckDefect;
        if (EnvironmentFailure().IsMatch(evidence)) return FailureClass.Environment;
        if (MissingCheckInput().IsMatch(evidence)) return FailureClass.CheckDefect;

        FailureFingerprint current = FailureSignature(checkOutput);
        if (current.Size == 0) return FailureClass.Unknown;

        FailureRound? previous = roundHistory.LastOrDefault(round => !string.IsNullOrWhiteSpace(round.Signature));
        if (previous is not null)
        {
            if (string.Equals(previous.Signature, current.Value, StringComparison.Ordinal))
            {
                return filesChanged == 0 ? FailureClass.Stall : FailureClass.CodeProgress;
            }

            string[] previousItems = previous.Signature!.StartsWith("sha256:", StringComparison.Ordinal)
                ? []
                : previous.Signature.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (current.Items.Count > 0 && previousItems.Length > current.Items.Count &&
                current.Items.All(item => previousItems.Contains(item, StringComparer.OrdinalIgnoreCase)))
            {
                return FailureClass.CodeProgress;
            }
        }

        if (!editToolCalled && filesChanged == 0) return FailureClass.NoOp;
        return FailureClass.CodeProgress;
    }

    internal static bool IsInfrastructure(Exception? exception, string? evidence = null)
    {
        if (MissingLlamaServer().IsMatch(evidence ?? string.Empty)) return true;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (MissingLlamaServer().IsMatch(current.Message)) return true;
            switch (current)
            {
                case OllamaSharp.Models.Exceptions.ModelDoesNotSupportToolsException:
                    return false;
                case OllamaSharp.Models.Exceptions.OllamaException:
                case HttpRequestException or System.Net.Sockets.SocketException or IOException or TimeoutException:
                case System.ClientModel.ClientResultException { Status: 0 or 408 or 429 or >= 500 }:
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(inner => IsInfrastructure(inner)):
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeLine(string line)
    {
        string normalized = DurationSuffix().Replace(line, string.Empty);
        normalized = ElapsedTime().Replace(normalized, "<time>");
        normalized = AbsolutePath().Replace(normalized, "<path>");
        normalized = Regex.Replace(normalized, @"(?<=\bline\s)\d+\b|(?<=\bchar\s)\d+\b", "<n>", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static bool IsSummaryLine(string line) =>
        line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("--- stdout", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("--- stderr", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Test run ", StringComparison.OrdinalIgnoreCase) && line.Contains("passed", StringComparison.OrdinalIgnoreCase);
}
