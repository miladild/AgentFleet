using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>Limits unattended plan attempts to local project work and read-only fleet context.</summary>
internal static partial class PlanRunnerToolPolicy
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "list_directory", "find_files", "search_files",
        "project_overview", "move_file", "delete_file", "run_command", "run_git_command",
        "get_plan", "search_context", "report_blocker"
    };

    /// <summary>The role of a model asked whether a step's check is broken.</summary>
    public const string CheckAuditRole = "check-audit";

    // The auditor reads and proposes. It cannot run a command, write a file or call anything else.
    private static readonly HashSet<string> AuditTools = new(StringComparer.Ordinal)
    {
        "read_file", "list_directory", "find_files", "search_files", "project_overview", "propose_check"
    };

    /// <summary>
    /// Whether a tool is offered on a plan runner request. report_blocker is for a model carrying out a step and
    /// propose_check for the auditor; neither is offered in chat, and the auditor gets nothing that changes anything.
    /// </summary>
    public static bool Offers(string toolName, bool fromRunner, string? role)
    {
        if (toolName is "report_blocker" or "propose_check")
        {
            return fromRunner && (role == CheckAuditRole ? toolName == "propose_check" : toolName == "report_blocker");
        }

        return !(fromRunner && role == CheckAuditRole) || AuditTools.Contains(toolName);
    }

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

    // What a plan step must never do to the machine it runs on: a model that is stuck will try anything that makes its check
    // pass, and measured on a real worker it set the limited account's PowerShell execution policy so that a blocked npm
    // script would run. These change a security setting, a service, an account or the machine's configuration, and they
    // outlive the plan, so they are refused whoever asks and whatever the scope (Process included: it is never needed).
    [GeneratedRegex(@"(?ix)
        \bSet-ExecutionPolicy\b
        |\b(?:Set|Add|Remove)-MpPreference\b|\bDisable-WindowsOptionalFeature\b|\bDisable-NetFirewall(?:Rule|Profile)\b
        |\bnetsh(?:\.exe)?\s+advfirewall\b|\b(?:New|Set|Remove|Enable)-NetFirewall(?:Rule|Profile)\b
        |\breg(?:\.exe)?\s+(?:add|delete|import|load|unload|restore)\b|\bregedit(?:\.exe)?\b
        |\b(?:New|Set|Remove)-ItemProperty\b[^\r\n]*\bHK(?:LM|CU|CR|U|CC)\b[:\\]
        |\bschtasks(?:\.exe)?\s+/(?:create|change|delete)\b|\b(?:Register|Set|Unregister)-ScheduledTask\b
        |\b(?:New|Set|Remove)-Service\b|\bsc(?:\.exe)?\s+(?:create|config|delete|failure)\b
        |\bnet(?:1)?\s+(?:user|localgroup|accounts)\b|\b(?:New|Set|Remove)-LocalUser\b|\b(?:Add|Remove)-LocalGroupMember\b
        |\bsetx(?:\.exe)?\b|\bSetEnvironmentVariable\s*\([^)]*,\s*['""]?(?:User|Machine)\b
        |\b(?:icacls|takeown|cacls)(?:\.exe)?\b|\bSet-Acl\b
        |\b(?:npm|pnpm|yarn|bun)\s+(?:\S+\s+)*(?:-g|--global|--location[= ]global)(?=\s|$)|\byarn\s+global\b|\bnpm\s+config\s+set\b")]
    private static partial Regex MachineChange();

    [GeneratedRegex(@"\b(?:git(?:\.exe)?|gh(?:\.exe)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryCli();

    public static string? Refusal(string toolName, string? command = null, string? role = null)
    {
        if (role == CheckAuditRole)
        {
            return AuditTools.Contains(toolName)
                ? null
                : $"Blocked: `{toolName}` is not available while a check is being audited. You can read files and call propose_check.";
        }

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

        if (MachineChange().IsMatch(command))
        {
            return "Blocked by unattended plan policy: this command changes a security setting or the configuration of the machine (execution policy, firewall, Defender, registry, services, scheduled tasks, accounts, permissions, environment, global packages). " +
                   "Plan commands work inside the project only. If the project cannot be built here without such a change, say so in one sentence instead of trying it.";
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
