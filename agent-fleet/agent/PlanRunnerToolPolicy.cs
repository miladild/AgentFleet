using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>Limits unattended plan attempts to local project work and read-only fleet context.</summary>
internal static partial class PlanRunnerToolPolicy
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "list_directory", "find_files", "search_files",
        "project_overview", "move_file", "delete_file", "run_command", "run_git_command",
        "get_plan", "search_context"
    };

    [GeneratedRegex(@"(?ix)
        \b(?:git(?:\.exe)?\s+(?:(?:--no-pager|-c\s+\S+|-C\s+\S+)\s+)*(?:add|commit|push|pull|fetch|reset|clean|checkout|restore|rebase|merge|cherry-pick|tag|worktree|stash|switch|revert))\b
        |\b(?:curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr|irm|ssh|scp|sftp|ftp|nc|netcat|telnet)\b
        |\b(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?(?:publish|deploy|release)\b|\b(?:twine\s+upload|dotnet\s+nuget\s+push|cargo\s+publish)\b
        |\b(?:kubectl|helm|terraform\s+(?:apply|destroy|import)|docker\s+push)\b
        |\b(?:sudo|runas)\b|\bStart-Process\b[^\r\n]*\b-Verb\s+RunAs\b
        |\b(?:rm\s+(?:-[a-z]*r[a-z]*|--recursive)|rmdir\s+/s|rd\s+/s|del\s+/s)\b
        |\bRemove-Item\b[^\r\n]*(?<![\w-])-Recurse\b
        |(?:^|[\s/\\""'=(])\.\.(?=$|[\s/\\""')])")]
    private static partial Regex UnsafeCommand();

    [GeneratedRegex(@"\b(?:git(?:\.exe)?|gh(?:\.exe)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryCli();

    public static string? Refusal(string toolName, string? command = null)
    {
        if (!AllowedTools.Contains(toolName))
        {
            return $"Blocked by unattended plan policy: `{toolName}` is outside the local project tool set. Use project file tools and local checks; external and custom tools need a separate user-approved action.";
        }

        if (toolName == "run_git_command")
        {
            if (CommandRefusal(command) is { } commandRefusal) return commandRefusal;
            string verb = FirstToken(command);
            if (verb.Length == 0 || verb is not ("status" or "diff" or "log" or "show" or "branch" or "ls-files" or "rev-parse" or "grep"))
            {
                return "Blocked by unattended plan policy: Git commands are read-only during a plan. Commits, pushes, resets, and other repository mutations require a separate user-approved action.";
            }
        }

        if (toolName == "run_command") return CommandRefusal(command);
        return null;
    }

    public static string? CommandRefusal(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (RepositoryCli().IsMatch(command))
        {
            return "Blocked by unattended plan policy: Git and GitHub commands cannot run through the shell. Use the dedicated read-only Git inspection tool when needed; repository writes and remote GitHub actions require a separate user-approved action.";
        }

        if (UnsafeCommand().IsMatch(command))
        {
            return "Blocked by unattended plan policy: this command can affect a remote service, another machine, repository history, or paths outside the project. Keep plan checks and repair commands local to the selected workspace.";
        }

        return null;
    }

    private static string FirstToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string first = value.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (first.StartsWith("--", StringComparison.Ordinal)) return string.Empty;
        return first.Trim('"', '\'', '/').ToLowerInvariant();
    }
}
