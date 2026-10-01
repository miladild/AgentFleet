# Security

Read this before you put the fleet anywhere except one PC you sit at.

## The short version

- The fleet has **no login and no approval step**. Its tools read and write your files and run commands as the account the
  backend runs under. Whoever can send a request to the backend can do all of that.
- By default the backend listens **only on the machine it runs on**. Keep it that way unless you need otherwise.
- It is designed for a network you trust. **Never expose it, the web UI or any Ollama port to the internet.** No port
  forwarding, no public tunnel.
- Run it as **your own user account**, not as an administrator or LocalSystem.
- Use **plan mode** for anything you want to look at before it happens.

## What is exposed, and to whom

| Thing | Default | Who can reach it |
|---|---|---|
| Backend, port 8000 | `localhost` only | Programs on this machine |
| Web UI, port 3000 | this machine | Programs and browsers on this machine |
| Ollama on the hub, 11434 | this machine | Programs on this machine |
| Ollama on a worker, 11434 | the network, from the address you allow | The machines you named |

Ollama has no login either. Anyone who can reach a worker's port 11434 can use its models, download models onto it and
delete them. The worker scripts therefore require an explicit private IPv4 peer address and scope the firewall rules to
that address. They reject CIDR networks, `LocalSubnet`, hostnames, and wildcards. The Linux script refuses to enable
network listening unless UFW or firewalld is already active and its existing rules do not broadly allow the Ollama port;
it does not install or enable a firewall for you. The Windows script also checks that Windows Firewall is enabled with
block-by-default inbound policy and stops if another broad port rule could bypass the peer scope. Keep other services you
need (such as SSH or RDP) allowed before changing a machine's default firewall policy.

### Letting other machines reach the hub

You only need this for `@fleet` in VS Code on a different computer. Either:

- run VS Code on the hub (over Remote Desktop or VS Code's remote features), or
- install with `.\scripts\Install-Autostart.ps1 -ListenOnLan`, which binds the backend to the network and opens the
  port to your local network only (the Private firewall profile). Then **anyone on that network can drive the fleet**,
  including its file and command tools. Do this only on a home or office network where you trust every device, never on
  guest Wi-Fi, a hotel network or a shared flat's network.

## Protection against web pages

A page you open in a browser can make your browser send requests to `localhost`. With a trick called DNS rebinding a
hostile site can even make those requests look like they come from the same place. The backend blocks both: it only
answers requests addressed to `localhost`, an IP address, or the machine's own name, and it refuses any request that
carries a browser `Origin` from another site. If you reach the hub by another name, list it in `FLEET_ALLOWED_HOSTS`
(comma separated). This protects against browsers. It is not a login, and it does not stop another program on your
network.

## The account it runs as

Whatever account runs the backend is the account the model's tools use. Under your own user that is your files and your
tools, which is the point. Under LocalSystem (a Windows service's default) it is the whole machine. That is why
`Install-Autostart.ps1` defaults to scheduled tasks that start at your logon with a limited user token, and warns when you
choose services. Run its build/install step from a normal, non-elevated PowerShell. It restricts the install folder to the
Fleet user, SYSTEM and local Administrators so other local accounts cannot read or change the config, plans or logs. If
you do use a service, set it to run as your own user in `services.msc` (Log On tab), typing your password in that dialog
and nowhere else.

Runtime installation is a separate operator action: `scripts/Install-FleetToolchains.ps1` needs elevation only to install
three fixed, machine-wide packages. It does not execute project code. The Fleet backend and ordinary chat commands run
on the hub as a limited non-administrator account. Plan steps require a configured worker workspace; project file
tools, shell commands and verification checks run on the selected worker as its dedicated standard account. Installing
a runtime does not grant that account administrator rights. Install the project's required runtimes on each worker that
will run its checks. On Windows, run `scripts/Install-FleetToolchains.ps1 -AuditOnly` to inspect machine-wide availability,
or provision a coding worker with `scripts/Setup-Worker.ps1 -AllowFrom <hub-private-ip> -InstallToolchains`. These fixed
packages do not cover arbitrary project pins; install additional pinned SDK versions through the machine's normal
administrator setup. Restart `sshd` after worker PATH changes and restart Fleet after hub PATH changes so new processes
inherit them. On Linux, use the distribution's trusted package sources and verify the toolchains from the dedicated
workspace account before approving a plan.

## Worker plan workspaces

Worker execution uses SSH and SFTP with a pinned SHA256 host key. Configure a dedicated account that is not an
administrator/root and has no passwordless sudo; Fleet checks these conditions at connection time. Keep the private SSH
key on the hub and add only its public half to the worker. The setup scripts create the account/workspace but do not
change SSH service or firewall settings.

Fleet stages a project copy on the selected worker, excluding dependency/build caches, `.git`, environment files,
private notes, and common key/certificate/credential files. It creates local-only Git metadata without the source
repository's history or remotes. Worker changes are synced into the hub checkout before verification and after each
attempt; content hashes detect a concurrent hub edit and block the sync instead of overwriting it. A workspace is an isolated directory, not a
container or chroot: commands can access anything the dedicated account can access. Keep that account's home free of
credentials and unrelated data. Hub-only Docker sandbox and HTTP request tools are unavailable during worker plan steps;
use `run_command` on the selected worker for its checks and local requests.

Unattended plan requests have a separate tool allowlist: local project file tools, shell checks, the dedicated read-only Git
inspection tool, and read-only plan context. Git and GitHub CLI commands through the shell, MCP/custom tools, web/HTTP tools,
Git history mutations, and known publishing, deployment, remote-shell and destructive commands are refused at invocation time.
This is a guardrail, not a shell sandbox: a project script can still do anything the limited worker account can do, so keep
that account low privilege and free of secrets.

## What the model can do to you

Local models make mistakes and can be steered by text they read. A web page, a file or a tool result that contains
instructions can push the model to act on them. Because there is no approval step, treat the assistant like a junior
colleague with your keyboard: useful, fast, and worth checking.

Practical limits:

- Switch off tools you do not need (Config panel). Without `run_command`, `run_git_command` and `write_file` it cannot
  change anything.
- Use plan mode. During planning only read-only tools exist, and that is enforced in code, not by asking nicely.
- Work in a git repository so every change can be reviewed and undone.
- Do not point it at a folder that holds secrets. It can read anything your account can.
- Pinned decisions are handed to every later agent as "still in force". The assistant can pin one itself, so text it read
  (a page, a file) could steer it into pinning something you did not intend. They are labelled "noted by the assistant" versus
  "from the user", and the Context panel lists them with an **unpin** button. Look at that list before you leave a long plan
  running overnight.
- Be careful with MCP servers you did not write in ordinary chat. They run with the same lack of approval, and some expose dangerous
  tools (the reference `server-everything` has one that returns all environment variables).
- The sandbox container can reach the network, including other machines on yours.
- In ordinary chat, `run_command` and `run_git_command` run with the backend's account. In unattended plans, shell commands
  run as the limited worker account; Git inspection uses the read-only plan tool, and the execution policy blocks known
  external and destructive commands. A project script can still have side effects, so review plan steps and their restart-safe setting.
- **From your other apps** reads the MCP settings of VS Code, Claude Desktop, Claude Code, Cursor and Windsurf for the
  backend's account. Only those files, and only their MCP servers; secret values are never sent to the browser, but an
  imported entry with a secret written into it is copied into `fleet.config.json` as it is.
- `web_search` and `web_fetch` send queries to DuckDuckGo and fetch pages you or the model choose in ordinary chat; they
  are unavailable to unattended plan steps. Switch them off in the Config panel for a fully offline fleet.

## Secrets

- Never put a token or password in `fleet.config.json`, in the code, or in a plan. Give MCP servers secrets through
  environment variables and refer to them as `${env:NAME}`.
- Every conversation is kept in the durable record (`contexts\fleet-context.db`, see [context.md](context.md)) with each
  tool call, its arguments, and the start of its result, and the log records tool calls too. Anything the model read or ran
  can appear there, including file contents. It is stored unencrypted on the hub and protected only by that account's file
  permissions. Keep the `contexts`, `sessions`, `plans` and `logs` folders private, and do not copy the database elsewhere
  without reading it first. By default it keeps every chat; **Config > History** can delete chats not used for a number of
  days, now or on a schedule, and a deleted chat is overwritten in the file rather than left in free space (see
  [context.md](context.md#keeping-it-small)). The record is what a later agent is told to trust as data, not as instructions: text in it that
  came from files or the web is labelled untrusted, but a model can still be steered by it, so the advice under
  "What the model can do to you" applies.
- `fleet.config.json`, `contexts`, `sessions`, `plans`, `logs` and `.env.local` are listed in `.gitignore` so they are not committed.
- The sandbox's SSH key (made by **Config > Sandbox**, `~/.ssh/agent-fleet_rsa` by default) is readable only by the account
  the backend runs as, and only its public half is ever shown. Give it an account on the sandbox machine that can run
  Docker and nothing more. **Add the key to that machine** takes that account's password for one sign-in; the password
  goes from the browser to the backend on this computer, is used once, and is not saved or logged. After a test, **Save**
  remembers the machine's host key and the fleet then refuses a machine that answers with a different one.
- The Setup tab can download models onto any machine you name and start Ollama on this computer. Like the rest of the
  API, it is only reachable from this computer unless you opened the backend to the network.

## Reporting a problem

If you find a way to reach the backend or run a tool that you think should not be possible, please open an issue that
describes it without publishing a working exploit.
