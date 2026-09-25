# Tools and MCP

The assistant is more than a chat window because it can use tools: read files, search, edit, run commands, and so on.
This page lists what it has, how to switch tools off, and how to add more.

## Built-in tools

| Tool | What it does |
|---|---|
| `read_file` | Reads a text file on the hub. For a big file, a start and end line read just that part. |
| `write_file` | Creates a file or replaces one completely. |
| `edit_file` | Changes part of an existing file by replacing exact text. Far safer than rewriting the whole file, and it cannot drop the rest of the file by accident. |
| `list_directory` | Lists a folder. |
| `find_files` | Finds files by name with a pattern like `*.cs` or `src/**/*.tsx`. |
| `search_files` | Searches file contents for a regular expression, like `grep -rn`. Skips `node_modules`, `bin`, `obj`, `.git` and binary files. |
| `run_command` | Runs any shell command on the hub: builds, tests, package installs, any tool you have installed. |
| `run_git_command` | Runs git in a repository folder. |
| `run_sandboxed_code` | Runs a snippet of Python, JavaScript or bash in a throwaway Docker container. Off when no Docker is found. |
| `project_overview` | A project at a glance: its folders a few levels deep (dependencies and build output skipped), how to build and test it from its project files (package.json scripts, `.csproj`, `pyproject.toml`, `Cargo.toml`, `go.mod`, Makefile...), and the start of its README. |
| `move_file` | Moves or renames a file or folder. Will not replace an existing file unless told to. |
| `delete_file` | Deletes one file or an empty folder. It refuses a folder with contents. |
| `web_search` | Searches the web (DuckDuckGo, no key needed). |
| `web_fetch` | Fetches a page and returns its readable text. |
| `http_request` | Calls an HTTP API with any method, headers and body, and returns the status and the raw body (JSON pretty-printed). For trying a local server you are building or any REST API. |
| `propose_plan`, `get_plan` | Plan mode only. See [planning.md](planning.md). |

All file and command tools work on **the hub machine's own disk**, as the account the backend runs as. Give full paths.

Small local models do better with small precise tools than big blunt ones, which is why there are separate search, read
and edit tools instead of one "do anything with files" tool.

### Trying a tool yourself

Every tool in **Config > Tools** has a **Try** link: fill in its values and press **Run**, and you see exactly what the
model would get back. It really runs, on the hub. Handy for checking an MCP server or one of your own tools before
leaving it to the model.

### Switching tools off

In the web UI, **Config**, then untick a tool under **Built-in tools**. It is saved and applied at once: the next message
no longer offers it, and there is no restart. A disabled tool is not offered to the model at all. The same thing is `"tools": { "run_command": { "enabled": false } }` in `fleet.config.json`. Turning
off `run_command`, `run_git_command` and `write_file` leaves an assistant that can read and search but not change
anything, which is a reasonable first setting on a machine you do not fully trust the models on.

### The sandbox

`run_sandboxed_code` runs code in a Docker container that has no saved state, runs as an unprivileged user, has a
read-only file system, no Linux capabilities, a memory and CPU limit, and a short time limit (20 seconds by default). The
container does have outbound network access, so package installs work; that also means it can reach other machines on
your network.

Set it up in the web UI: **Config**, **Sandbox**. Pick where the container runs, press **Test**, then **Save**. It applies
at once, without a restart.

- **Automatic** (default): Docker on the hub if it is installed, otherwise the tool is not offered.
- **This computer**: Docker Desktop (Windows, macOS) or Docker Engine (Linux) on the hub.
- **Another machine, over SSH**: Docker on a Linux machine, reached over SSH with a key. Handy when the hub has no Docker,
  and it keeps model-written code off the hub entirely.
- **Off**: no sandbox tool.

**Test** checks each step and says what to do when one fails: it signs in, checks that Docker runs there and that the
account may use it (with the exact `usermod` line when it may not), and checks whether the two container images are
already downloaded. If not, **Download them now** fetches them (about 250 MB) and runs a real snippet, so the model's
first call does not spend its time limit waiting for a download.

#### SSH, step by step

1. On the sandbox machine: an SSH server, Docker Engine (`curl -fsSL https://get.docker.com | sh`), and the account in the
   `docker` group (`sudo usermod -aG docker <account>`, then sign out and in).
2. In **Config**, **Sandbox**: choose **Another machine, over SSH**, type its address and the account.
3. **Create a key for the fleet**. The fleet makes its own key pair (`~/.ssh/agent-fleet_rsa` for the account the backend
   runs as, readable by that account only). The private half never leaves the hub, and an existing key file is never
   overwritten.
4. **Add the key to that machine**: type that account's password once and the fleet adds its public key to
   `~/.ssh/authorized_keys` there. The password is used for that one sign-in and is not saved or logged. On a machine that
   only accepts keys, or a Windows one, **Or do it by hand** shows the line to run there instead.
5. **Test**, then **Save**.

The first successful test shows the machine's identity (its SSH host key fingerprint), and **Save** remembers it. From
then on the fleet refuses to run code on a machine that answers with a different key, so something else taking over that
address cannot receive your code. If you reinstall the sandbox machine, press **Test** and **Save** again to remember its
new key.

The same settings are the `sandbox` section of `fleet.config.json`, if you would rather edit the file (a hand edit applies
at the next start):

```json
"sandbox": { "mode": "ssh", "host": "192.168.1.30", "user": "builder", "keyPath": "C:\\Users\\me\\.ssh\\agent-fleet_rsa",
             "hostKey": "SHA256:..." }
```

`mode` is `auto`, `local`, `ssh` or `off`. For `ssh`: `host`, `user`, `keyPath`, optionally `port`, `"sudo": true` when
the account may run `sudo docker` without a password instead of being in the `docker` group, and `hostKey` (without it,
any host key is accepted). `timeoutSeconds` (1 to 120, default 20) is the time limit per run in every mode.

## Your own tools, from a command

The quickest way to give the fleet a tool for your own work: **Config > Tools > Your own tools > New tool from a
command**. Write the command you would type, with `{name}` where the model fills something in:

| Field | Example |
|---|---|
| Name | `run_tests` |
| What it does (the model reads this) | Runs the .NET tests of a project and reports which failed. |
| Command | `dotnet test {project} --nologo` |
| Run it in (optional) | `C:\projects\shop`, or `{folder}` to let the model choose |

Each `{name}` becomes a value the model passes; describe it and say whether it is required. An optional value that the
model leaves out drops its whole argument, so `--filter={filter}` disappears rather than becoming `--filter=`. **Start
from** has templates for .NET tests, an npm script, pytest and `dotnet format`. **Try it** runs it with values you type
before you save, and **Add and save** makes it available to the next message, in the web UI, plan runs and `@fleet`.

- **No shell.** The command runs as a program with separate arguments, never through `cmd` or `sh`, so whatever the
  model fills in is always exactly one argument and cannot start a second command. The program itself is fixed; only its
  arguments can be filled in. On Windows, a `.cmd` or `.bat` program (such as `npm`) is run by `cmd.exe`, so for those a
  value may not contain `" % ! & | < > ^`. For pipes or several commands, write a script file and point the tool at it.
- **Read-only.** Tick **it only reads** for a tool that changes nothing, such as one that lists or checks. It is then
  allowed while a plan waits for approval.
- **Limits.** Two minutes by default (up to ten), and the output is cut to its start and its end (where test summaries
  are) if it is long.

In `fleet.config.json`:

```json
"customTools": {
  "run_tests": {
    "description": "Runs the .NET tests of a project and reports which failed.",
    "command": "dotnet test {project} --nologo",
    "parameters": [{ "name": "project", "description": "Path to the test project or solution", "required": true }],
    "workingDirectory": null,
    "timeoutSeconds": 300,
    "readOnly": false,
    "enabled": true
  }
}
```

## MCP servers: add tools without writing code

[MCP](https://modelcontextprotocol.io) (Model Context Protocol) is a standard way for a program to offer tools to an AI.
There are servers for documentation search, GitHub, databases, browsers and much more. The fleet can connect to them.

In the web UI, open **Config > Tools** and scroll to **MCP servers**. There are four ways to add one:

1. **Pick from a list.** A catalog of servers that work well for coding, by category: documentation (Microsoft Learn,
   Context7, DeepWiki, Hugging Face), browsers (Playwright, Chrome DevTools), code and files (GitHub, git for one
   repository, files in one folder), thinking and memory (sequential thinking, memory, time) and Azure. Each card says
   what it needs, and warns when that is missing on the hub (Node.js for `npx` servers, [uv](https://docs.astral.sh/uv/)
   for `uvx` ones). **Set up** fills in the form; a value it needs from you, such as the folder or repository, gets a field
   of its own, and anything to prepare (a token) is explained there.
2. **From your other apps.** The servers you already set up in VS Code, Claude Desktop, Claude Code, Cursor or Windsurf on
   this computer. Tick the ones the fleet should have and press **Add the ticked ones**. The list never shows secret
   values; an entry with a secret written into it is flagged, because it would be copied into `fleet.config.json` as it
   is. An entry that uses VS Code's `${input:...}` is flagged too: the fleet cannot ask for a value, so edit it to use
   `${env:NAME}`.
3. **Custom server.** A name, then either a command to run on the hub (`npx -y ...`, `uvx ...`, a path to a program) or a
   web address, plus environment variables or headers if it needs a key.
4. **Paste mcp.json.** Paste the JSON from a server's README, from VS Code's `mcp.json`, or from a Claude or Cursor
   config. Both `"servers"` and `"mcpServers"` work, and names are tidied up.

Press **Test** first: the backend starts the server once and shows the tools it offers, or, if it will not start, what it
printed (a missing `npx`, an npm 404, a missing key). **Add and save** connects it straight away. There is no restart:
the next message can use it. Each server then has **Turn off**, **Edit** and **Remove**, and a list of its tools with a
switch for each.

The same entries can be written by hand in `fleet.config.json`:

```json
"mcpServers": {
  "microsoft-learn": {
    "type": "http",
    "url": "https://learn.microsoft.com/api/mcp"
  },
  "files": {
    "type": "stdio",
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\you\\notes"]
  }
}
```

The field names are the same as VS Code's `mcp.json`. A change made by hand in the file is picked up at the next backend
start, or as soon as anything is saved from the Config panel.

- **Names.** Tools are renamed `<server>_<tool>` (for example `microsoft-learn_microsoft_docs_search`), because servers
  often reuse names the built-in tools already have.
- **Secrets.** Never write a token into the file. Set it as an environment variable on the machine and refer to it as
  `${env:NAME}` in `args`, `env`, `url` or `headers`. The Config panel warns when a value looks like a real secret. The
  backend reads its environment when it starts, so after setting a new variable, restart the backend once.
- **Failures.** A server that will not start is skipped, with the reason (including what it printed) shown in the panel and
  in the log. It never stops the backend from starting.
- **Windows.** `npx`, `npm`, `pnpm` and `yarn` are run through `cmd.exe` for you.
- **No approval step.** MCP tools run as soon as the model calls them, like the built-in ones. Look at what a server exposes
  before enabling it. For example, the reference server `server-everything` has a `get-env` tool that returns every
  environment variable, secrets included; untick it, or do not use that server.
- A server that says a tool is read-only (a `readOnlyHint`) stays available in plan mode before a plan is approved.

## Tools in VS Code

`@fleet` gets its tools from four places. Only the last is VS Code's own configuration.

| | Built-in tools | Your own tools | Fleet MCP servers | VS Code tools |
|---|---|---|---|---|
| Defined in | the backend | `customTools` in `fleet.config.json` | `mcpServers` in `fleet.config.json` | `.vscode/mcp.json`, your user `mcp.json`, other extensions |
| Runs | on the hub, in the backend | on the hub, started by the backend | on the hub, started by the backend | through VS Code |
| In the web UI | yes | yes | yes | no |
| In `@fleet` | yes | yes | yes | yes |

`.vscode/mcp.json` configures VS Code, not the fleet: the backend never reads it. Configure a given server in one place
only. Otherwise `@fleet` sees it twice under different names.

## Adding a tool in code

Most tools do not need code: a command tool or an MCP server covers them. For something that has to live inside the
backend:

1. Write a static method that returns a string (see `HubFileSystemTools.cs`).
2. Add its description to the `toolDescriptions` dictionary in `Program.cs`. That string is what the model reads and what
   the Config panel shows.
3. Wrap it with `AIFunctionFactory.Create(...)` and add it to `builtInTools`.
4. Say when to use it in the agent's instructions, and add it to the plan-mode rules if it changes anything.
5. Optionally add a renderer in `src/components/ToolRenderers.tsx` and tests in `agent.Tests`.

Two things trip people up with local models. A parameter that is optional needs a default (`= null`), or the model is
told it is required. And a raw JSON parameter needs a `[Description]`, or Ollama rejects the tool.
