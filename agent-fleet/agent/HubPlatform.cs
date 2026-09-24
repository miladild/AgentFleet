namespace AgentFleet;

/// <summary>
/// What the model needs to know about the machine its file and shell tools act on: the
/// hub, which is the machine this backend runs on. Kept in one place so the tool
/// descriptions and the system prompt agree.
/// </summary>
internal static class HubPlatform
{
    public static string ShellDescription =>
        OperatingSystem.IsWindows() ? "a Windows shell (cmd.exe)" : "a POSIX shell (/bin/sh)";

    public static string Name =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>The paragraph of the system prompt about path style.</summary>
    public static string PathInstructions =>
        OperatingSystem.IsWindows()
            ? """
              The hub machine you run tools on is Windows. Always use Windows-style paths
              exactly as given or as they'd naturally appear on Windows (e.g. C:\Users\name\
              project, C:\integration-test) for read_file, write_file, edit_file,
              list_directory, find_files, search_files, run_git_command, and run_command's
              working directory. Never rewrite a path into POSIX/Unix style (e.g.
              /c/integration-test or /mnt/c/...) - that format does not exist on this
              machine and every tool call will fail if you do this.
              """
            : $"""
              The hub machine you run tools on is {Name}. Use absolute POSIX-style paths
              exactly as given (e.g. /home/name/project) for read_file, write_file,
              edit_file, list_directory, find_files, search_files, run_git_command, and
              run_command's working directory. Never rewrite a path into Windows style
              (e.g. C:\project) - that format does not exist on this machine.
              """;
}
