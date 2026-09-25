# Agent Fleet: usage guide

## What this is

A local coding assistant backed by the machines on your network. Every message
is sent to whichever machine fits it best and answered by that machine's local
model. Nothing leaves your network, except web searches when you ask for them.
The panel at the top of the page shows each machine and whether it is up (green
or red), and the **Config** button is where machines, models, tools and MCP
servers are set up.

New here, or something is red? **Config > Setup** lists what the fleet needs,
with the fix for each item and a button for the ones it can do itself:
downloading a model that suits this computer, starting Ollama, downloading a
missing model onto another machine. **Config > Sandbox** sets up the code
sandbox, including Docker on another machine over SSH.

## Two toggles (top right)

- **Hub mode** - *Conservative* keeps the strongest machine for complex work
  and protects its GPU for your own use; ordinary tasks go to the standard
  machine and trivial ones to the light machine. *Aggressive* lets the strongest
  machine take ordinary tasks too, for when you are not using it (overnight).
  Takes effect on your next message.
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
5. After approval the plan runs **in the background**, in order. Steps run one
   at a time by default. Consecutive steps explicitly marked as an independent
   parallel group may run together when their project-local files do not
   overlap and their distinct model tiers have ready nodes. The fleet checks
   group edits after they finish; checks and retries run sequentially. Each
   attempt also gets bounded context from the text files named by that step,
   labeled as untrusted data. If a check fails, the model gets the real error
   and tries again (the last attempt goes to the strongest model). If a step
   still cannot pass, the plan stops as *Blocked* and tells you where.

You can close the chat and come back: the **Plans** button lists every plan and
how far it got, and a plan that was running when the backend restarted picks up
where it left off. When you reopen the web app, a dismissible notice calls out
plans that finished or became blocked in the last 36 hours; **Open plan** takes
you to its run log. **Stop** halts a running plan.

Planning on a large model can take a few minutes. Small tasks do not need a
diagram.

## What it can do

Built-in tools:

- **read_file**, **write_file**, **edit_file**, **list_directory**,
  **find_files** (by name), **search_files** (by content) - work on real files
  on the hub machine. `edit_file` changes part of a file by replacing exact
  text, which is far safer than rewriting the whole file.
- **run_command** - runs any shell command on the hub (build, test, install).
- **run_git_command** - git in a repository folder.
- **run_sandboxed_code** - runs a snippet in a throwaway Docker container, when
  Docker is set up.
- **web_search**, **web_fetch** - look things up online and read a page.
- **validate_diagram** - checks Mermaid source with the real parser and returns
  a corrected version when the checker can reach the web app. Fenced Mermaid
  diagrams in ordinary answers show a render control in the chat.

More tools can be added without code, and without a restart: in **Config**, pick an **MCP server** from the list, paste one
from a README, or type one in; **Test** it, then **Add and save**. Its tools appear here and in
`@fleet` in VS Code.

To work on a project, give the full path:

> In C:\Users\you\source\myapp, find where the login is handled and explain it.

> In C:\Users\you\source\myapp\app.py, fix the bug where X happens.

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
