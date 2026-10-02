# Agent Fleet: usage guide

## What this is

A local coding assistant backed by the machines on your network. Every message
is sent to whichever machine fits it best and answered by that machine's local
model. Nothing leaves your network, except web searches when you ask for them.
The panel at the top of the page shows each machine and whether it is up (green
or red), and the **Config** button is where machines, models, tools and MCP
servers are set up.

Set the **Project folder** on the main page (or tell the assistant which
project to work on) and every message carries it: "run the tests" or "where is
login handled?" then need no path, and the chat suggests things to do with that
project. Give answers a thumbs up or down: **Config > Machines** counts them per
machine. Ask the assistant how to set something up and it can open the right
settings tab for you.

New here, or something is red? **Config > Setup** lists what the fleet needs,
with the fix for each item and a button for the ones it can do itself:
downloading a model that suits this computer, starting Ollama, downloading a
missing model onto another machine. **Config > Sandbox** sets up the code
sandbox, including Docker on another machine over SSH.

In **Config > Machines**, set a response style for each model:

- **Caveman** makes replies shorter: Lite removes filler, Full is very concise,
  and Ultra uses the fewest clear words while keeping technical details and
  safety conditions.
- **Ponytail** guides coding work toward the smallest correct change, reusing
  existing code and avoiding speculative abstractions or dependencies.

Choose Off, Lite, Full or Ultra. These are prompt styles, not software installed
on the worker. The selected styles follow a request through fallback; match the
levels on machines that hand work to each other for consistent replies.

## Two toggles (top right)

- **Hub mode** - *Conservative* keeps the strongest machine for complex work
  and protects its GPU for your own use; ordinary tasks go to the standard
  machine and trivial ones to the light machine. *Aggressive* sends ordinary
  chat and unpinned plan steps to the hub when you are not using it (overnight).
  A step's requested tier remains visible, and an explicit machine choice takes
  priority. The setting is saved across backend restarts and takes effect on
  the next chat message or plan step.
- **Plan mode** - see below.

## Plan mode: plan first, then act

Turn it on when you would want to review the approach before anything changes.

1. Ask for the change in plain words, with the project folder.
2. The strongest model explores your code using **read-only tools only**. It
   cannot write files or run commands at this stage; that is enforced in code,
   not just requested.
3. It proposes a **plan**: goal, assumptions, risks, an optional diagram, and
   small steps. Each step names its files and has a command that checks it (a
   build, a test). The plan appears as a card in the chat.
4. **Approve and run** (or Reject). In the web UI, you can optionally check
   **Save a Markdown copy** to put the approved plan under
   `.agent-fleet/plans/` in the project folder. You can also reply `approve` in
   chat; chat and VS Code approvals do not export a copy. If you want changes,
   just say what to change and it proposes a revised plan.
5. After approval the plan runs **in the background**, in order. Each step needs
   a configured worker workspace and runs sequentially, so its changes sync back
   before the next worker stages the project. Parallel groups stay visible in
   the plan but do not run together in worker mode. Each
   attempt also gets bounded context from the text files named by that step,
   labeled as untrusted data. If a check fails, the real output goes back into
   the same conversation, with what changed since the last round, so the model
   keeps working on what it started. A round that changes nothing, or rounds
   that stop paying off, move the step up a repair ladder: the hub model
   continues the same conversation when the plan's **Recovery** choice allows
   it, then a fresh conversation starts from a brief of what was tried. File
   edits, Git commands and checks stay on the selected worker, and the requested
   tier stays the same.
   A round that makes the check worse is undone before the next one, and a step
   has 45 minutes of working time. A step that ends without passing leaves its
   files at the best state it reached. A check that is itself broken (it never
   exits, the machine's shell cannot parse it, it names a program that is not
   there) is not a failing test: the fleet asks a model to audit it, applies a
   replacement only if fixed rules allow it, keeps the original and logs both,
   and does not spend a round on it. Choose **Ask me first** under **Broken
   checks** to be asked instead. When a worker lacks the dependencies the project
   declares, the fleet installs them once with the project's own command. The run log shows the selected machine and each climb;
   a user-selected machine takes priority. During a plan, files and verification
   commands run in that machine's staged project, then changes sync back to the
   hub checkout for review. A missing-runtime or SDK error stops retries and
   identifies the worker to repair. Project edits, Git commands and checks do
   not fall back to the hub; hub-only Docker and HTTP request tools are refused
   during worker plan steps.
   In **Config > Machines**, configure a worker workspace with its dedicated
   non-admin SSH account, private key path on the hub, verified SHA256 host key,
   platform and workspace root. The setup scripts `Setup-WorkerWorkspace.ps1`
   and `setup-worker-workspace.sh` create the limited account; they do not change
   firewall rules. In **Live**, choose a configured text machine for an awaiting,
   ready or failed step; its requested tier stays the same. Running steps,
   dependency-waiting steps and parallel peers already in flight cannot move.
   If a step still cannot pass, the plan stops as *Blocked* and tells you where.

You can close the chat and come back: the **Plans** button lists every plan and
how far it got, and a plan that was running when the backend restarted keeps its
place: it carries on if its step was safe to replay, and otherwise waits for you
to approve it. When you reopen the web app, a dismissible notice calls out
plans that finished or became blocked in the last 36 hours; **Open plan** takes
you to its run log. **Stop** halts a running plan.

To follow the work as it happens, use **Live** beside Plans or **Watch it live**
in VS Code. The live view shows planning activity, the step pipeline, assigned
tier and machine, current tool activity, retries, and check output. A blocked
step explains why and offers retry or skip. Select a step to change its next
machine while it is awaiting approval, ready to run, or failed; its requested
task tier stays the same. Running steps and steps waiting on earlier work cannot
be moved. The final report includes attempts, repair rounds,
checks, errors, and the paths Git detected as changed in a Git project.
On a busy screen, collapse the machine/step rail and event journal independently
from their headings. The closed headings keep the busy and failed step counts,
and the journal's alert count, in view; reopen either panel to inspect details.

In Copilot Chat, `@fleet /status` shows a plan report and its current progress.
The extension reconnects to a running plan after VS Code restarts, and reports
backend or stream errors in the chat. For an incomplete response or a failed
handoff, open the web UI's **Context** panel to see the error, partial output,
what the next agent will receive, and what earlier agents actually received.

Planning on a large model can take a few minutes. Small tasks do not need a
diagram.

## What it can do

Built-in tools:

- **read_file**, **write_file**, **edit_file**, **list_directory**,
  **find_files** (by name), **search_files** (by content), **move_file**,
  **delete_file** - work on real files on the hub during chat, and on the selected
  worker's staged project during an approved plan. `edit_file` changes
  part of a file by replacing exact text, which is far safer than rewriting the
  whole file. `delete_file` removes single files and empty folders only.
- **project_overview** - a project at a glance: its layout, how to build and
  test it, and the start of its README. Ask "give me an overview of
  C:\path\to\project" to see it.
- **run_command** - runs a shell command on the hub during chat and on the
  selected worker during a plan (build, test, install).
- **run_git_command** - runs git in the hub repository during chat and the staged
  worker project during a plan.
- **run_sandboxed_code** - runs a snippet in a throwaway Docker container on the
  hub during chat. It is unavailable in a worker plan; use `run_command` there.
- **web_search**, **web_fetch** - look things up online and read a page.
- **http_request** - call an API from the hub and see the raw response. In a
  worker plan, use `run_command` (for example `curl`) to make requests from the
  selected worker.
- **validate_diagram** - checks Mermaid source with the real parser and returns
  a corrected version when the checker can reach the web app. Fenced Mermaid
  diagrams in ordinary answers show a render control in the chat.

More tools can be added without code, and without a restart, in **Config > Tools**:

- **Your own tools**: turn a command into a tool, for example `dotnet test {project}` becomes `run_tests`. Templates
  for .NET tests, npm scripts and pytest get you started; **Try it** before you save.
- **MCP servers**: pick one from the list (documentation, browsers, GitHub, git, memory, Azure...), bring over the ones
  you already use in VS Code, Claude or Cursor with **From your other apps**, paste one from a README, or type one in.
  **Test** it, then **Add and save**.

Every tool has a **Try** link that runs it with values you type. New tools appear here and in `@fleet` in VS Code.

To work on a project, give the full path:

> In C:\fleet-test\project, find where the login is handled and explain it.

> In C:\fleet-test\project\app.py, fix the bug where X happens.

You can attach a screenshot: it goes to the vision machine, if one is set up.

There is **no approval step and no folder restriction** outside plan mode:
tools run immediately with the permissions of the account the backend runs as.
Use plan mode for anything you would want to look at first.

## What the fleet remembers

Each chat has a durable record: which machine answered, every tool call, decisions, plans and the files they produced. When
work moves to another machine, or a plan runs step by step, the next agent is handed a short summary of that record, so it
does not start from nothing.

- **Pin a decision** every agent must respect: tell the assistant ("remember: we use CommonJS"), or open the **Context**
  button and type it under "Pin something every agent must respect". Pinned decisions are always sent, on every machine.
- The **Context** button also shows what the next agent would receive, what earlier agents actually received, and whether the
  files a plan produced still match.
- **Summarise older history** in that panel shortens a very long chat without deleting anything.
- **Config > History** shows how big the record is, can delete chats you have not used for a while (it lists them and asks
  first), and can do that automatically after 30, 90 or 180 days. A chat a plan still runs in is kept.

## Good things to ask

- "Fix this bug: ..." or "Refactor this function to ..." - ordinary coding.
- "What does the % operator do in Python?" - a quick question.
- "Design a service architecture for ..." - complex work.
- "Find every place that calls X" - it will search rather than guess.
- With plan mode on: "Add rate limiting to the API in C:\path\to\project."

## Known rough edges

- The router is a small model guessing at difficulty. If something lands on the
  wrong machine, say so and re-ask.
- A machine that is off shows red; requests for it go to the fallback machine.
- Small local models sometimes need a second try or a more specific step. The
  plan runner's retries and checks exist for exactly that.
