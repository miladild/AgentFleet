# Plan mode: plan first, then act

Most of what goes wrong with an AI coding assistant goes wrong because it started typing before it understood the
task. Plan mode makes it look first, write down what it intends to do, and wait for you. Then it does the work in small
steps, and checks each step with a real command instead of trusting itself.

## The flow

1. **Turn plan mode on** (switch, top right of the web UI). The setting is stored by the backend, so the web UI and
   `@fleet` in VS Code both follow it. In VS Code you can instead start the request with **`@fleet /plan`**: plan mode
   then applies to that chat only (and stays on for its follow-ups), whatever the switch says.
2. **Ask for the change** in plain words, with the project folder:
   `In C:\src\shop, add rate limiting to the login endpoint.`
3. **Exploring.** The strongest machine reads your code with read-only tools: read, list, find, search, web search. It
   cannot write files or run commands at this stage. That is enforced in code, not by asking the model nicely: write tools
   are not offered, a call to one is turned into an explanation, and the execution layer refuses it as well.
4. **The plan.** The assistant calls `propose_plan`, and the plan appears as a card: goal, assumptions, risks, an optional
   diagram, and numbered steps. Each step names the files it touches, has a **check** (a command that must succeed, such
   as `dotnet build` or `npm test`), and a tier (which kind of machine should do it). The plan is saved as a file.
   Before saving, the fleet reviews it for mistakes that would only show at night, and sends it back to the planner to
   fix when it finds one: a missing project folder, a check that is a sentence rather than a command (or uses a program
   the hub does not have), a check that relies on a file only a later step creates, or a plan with no checks or no check
   on its last step. A revised plan replaces the earlier proposal in the same chat, so there is only ever one waiting
   for approval, and the turn ends once a plan is saved.
5. **You decide.** Press **Approve and run**, press **Reject**, or reply in the chat with what to change and it proposes a
   revised plan. Typing `approve` also works. (`approve the idea but change step 3` counts as feedback, not approval.)
   In the web UI, you can optionally check **Save a Markdown copy** before approving; it writes the plan into the project
   folder shown on the card. The default is off.
6. **The run.** Approval starts the plan **in the background**, in order. Steps run one at a time by default. If the plan
   labels consecutive independent steps with the same **parallel group**, the card shows that label and the runner may
   run that group concurrently.
   - A group runs concurrently only when every step names files, paths stay inside the project and do not overlap, the
     steps use distinct configured tiers, and each tier has a ready node. Otherwise the run log says why it fell back
     to sequential execution.
   - Each step goes to a machine of the tier the plan chose, in a fresh session that sees only the goal, the assumptions,
     the step itself, a summary of earlier steps, and bounded context from the files named by that step. The runner reads
     up to eight project-local text files (32 KiB each, 48,000 characters total) before each attempt. Missing, binary,
     linked, unreadable, and out-of-project files are skipped. The contents are labeled as untrusted data, never as
     instructions.
   - For a parallel group, the runner waits until all first attempts finish, then runs checks one at a time. Retries
     also run sequentially. **The fleet runs every step's check itself**; only a pass marks the step done.
   - An attempt ends after 25 rounds of tool calls (`FLEET_PLAN_STEP_TOOL_ROUNDS`) or 20 minutes, whichever comes
     first, so a model going round in circles cannot hold a machine; the step's check then decides.
   - In a git project, a tracked file that is deleted while a step runs, and that no step of the plan names, is put
     back from git just before and just after the step's check, and the run log says which (a test a model wrote once
     tidied up by deleting the project's own config file on every run). Outside git this protection does not exist, so
     put a project under git before leaving a plan to run.
   - A failed check goes back to the model with the real output, up to three attempts. The first attempt runs on a machine
     of the step's own tier, which is what spreads a plan over your machines; after a failure the next attempts go to the
     strongest machine, because a small model rarely does better the second time (`FLEET_PLAN_CHEAP_ATTEMPTS=2` gives the
     step's own machine a second try first).
   - If a step still fails, the plan stops as **Blocked**, says which step and why, and leaves your files as they are.
7. **Afterwards.** The **Plans** button lists every plan and how far it got; in VS Code, **`@fleet /status`** shows the
   current plan's report in the chat, with **Stop it**, or **Approve and resume** for a blocked plan. Close the browser and
   VS Code, go to bed: the plan runs in the backend. **Stop** halts a running plan.

## A plan made with GitHub Copilot

You can work a plan out with Copilot (or any chat model in VS Code) and have the fleet carry it out. There are two ways.

- **Ask Copilot to hand it over.** The extension gives Copilot a tool, **Run a plan on Agent Fleet** (`#fleetPlan`). In
  agent mode, say "send this plan to the fleet" or "run it overnight on my machines". Copilot sends the plan with a check
  command and a tier for every step, the fleet reviews it as it would its own (and tells Copilot what to fix), and VS
  Code asks you to confirm with the steps in front of you. Once you allow it, the plan starts in the background. The
  plan gets its own record in the fleet (it appears under **Sessions**), so every machine that runs a step is handed
  the plan's history. `#fleetStatus`, or `@fleet /status`, tells you how it is going.
- **Ask `@fleet`.** After planning with Copilot in a chat, send `@fleet /plan` (or `@fleet run the plan above`) in the
  same chat. VS Code shows a chat participant only its own turns, so `@fleet` reads the rest of the chat with the chat's
  **Copy All** command and puts your clipboard text back afterwards (turn this off with the `agentFleet.readWholeChat`
  setting). The strongest machine keeps the plan's steps and decisions, checks them against the code, and proposes
  them as a fleet plan with checks and tiers, for you to approve as usual.

The first way keeps Copilot's wording and needs no planning on your machines; the second lets the fleet check the plan
against the code first.

## A plan file

A plan written as a Markdown file in the format below is read by the fleet itself, so every step keeps its instructions
exactly as written. That matters: the machine that runs a step sees only that step, and a planner model asked to copy a
long plan shortens it. Point the fleet at the file, for example `@fleet /plan carry out the plan in C:\src\board\PLAN.md`;
the proposal comes back for approval as usual, after the same review as any plan.

```markdown
# Plan: a reliable, tested board

Working directory: `C:\src\board`

Goal: one paragraph on what the plan achieves.

Decisions:
- Tests use node:test through tsx: no new dependencies.

## Step 1: Add a test runner

- Tier: light
- Files: `package.json`, `test/smoke.test.ts`
- Check: `npm test`

Everything after those lines is the step's instructions, kept as written: what to change, the names other steps rely
on, the cases its test must cover.

## Step 2: Holidays

- Tier: heavy
- Parallel group: core
- Files: `src/server/marketHours.ts`, `test/marketHours.test.ts`
- Check: `node --import tsx --test test/marketHours.test.ts`

...
```

- `Working directory` is optional (the file's folder otherwise); so are `Goal`, `Decisions` (or `Assumptions`) and
  `Risks`.
- `Tier` is heavy, standard or light. Steps that share a `Parallel group` run at the same time on different machines:
  give them different tiers and separate files.
- `Check` is the command the fleet runs to decide whether the step worked. Name the files a step creates in `Files`.
- Other `##` sections after the steps (notes, an appendix) are ignored.

If the planner retypes a plan file instead of pointing at it, the fleet notices (the same number of steps as a plan file
the user named or the planner read) and takes the steps from the file.

## Leaving it overnight

What keeps a plan going when something happens in the night:

- **The backend restarts** (a crash, a Windows update, a reboot): the plan, every step's status and the whole record of
  the conversation are on disk. The plan picks up at the step that was interrupted; finished steps are not redone. Plan
  files are flushed to disk before they replace the old one, so a power cut cannot leave a half-written plan. Run as a
  Windows service (`Install-Autostart.ps1 -Mode Service`), the backend is started again within seconds if it stops
  unexpectedly, and at boot; the scheduled-task mode retries three times, a minute apart.
- **A machine goes down or slows down.** Each call goes to a ready machine of the step's tier; a machine that is off, or
  fails a call, is replaced by the fallback machine. A machine that times out rests for ten minutes, so the rest of the
  step does not wait for it again. If no machine can answer at all (the network is down, the fallback is rebooting), the
  step waits and tries again: after 30 seconds, then 1, 2, 4 and 8 minutes, then every 15 minutes, about an hour in all.
  Those waits do not count as attempts; the run log shows each one.
- **The hub goes to sleep.** While a plan runs, the backend asks the operating system to stay awake (Windows, macOS and
  Linux with systemd). It cannot stop someone closing a laptop's lid or choosing Sleep, and it does nothing for the other
  machines: set those to stay awake (the worker setup's `-KeepAwake`).

## Reading what happened overnight

Every plan keeps a **run log**: when the run started, each attempt with the tier and the machine that answered, what the
model said it did, whether the check passed (and, when it failed, the real output), timeouts, and how the run ended. Open a
plan from the **Plans** panel and expand **Run log**. **Open the full report** shows the same as a readable page: how the
run ended, where it stopped and why, the files git says changed (when the project is a git repository), per-step attempts
and timings, and the timeline in your local time. In VS Code, **Show status** opens the same report. The report is also
saved next to the plan as `<id>.report.md` and served at `/api/plans/<id>/report`. The log keeps the first line and the most
recent 400 lines, and cuts long outputs, so it stays small however long a plan retries.

While a plan is running, chatting is safe: the assistant only reports progress and can read files, it does not change
anything (so it never edits the same files as the run).

## Where plans live

Each plan is stored in the backend's `plans` folder: `<id>.json` (status, attempts and notes per step) and `<id>.md`
(a readable copy). The backend also serves it at `/api/plans/<id>/markdown`. When approving in the web UI, you can
check **Save a Markdown copy** to export an approved snapshot to `<project>/.agent-fleet/plans/<id>.md`; the folder is
created if needed. This is optional and unchecked by default. If the file cannot be written, approval does not start.
Approving by typing in chat or from VS Code does not export a copy.

## Diagrams

A plan can include a diagram (a Mermaid flowchart, sequence or class diagram) for changes that affect how several parts
fit together. The strongest model writes it, and the backend checks it with the real Mermaid parser before saving. If the
diagram is broken, the model is told to fix it, and after one more failure the plan is saved without it and says so.
Small tasks do not need one. In normal chat, fenced Mermaid diagrams can be rendered inline from the answer. The
`validate_diagram` tool checks Mermaid source independently and returns the safely corrected source plus whether it
parsed, failed validation, or could not be checked because the web app was unavailable.

## Writing requests that plan well

- Give the project folder and the outcome, not the method: what should be true afterwards.
- Say what must not change ("do not touch the public API").
- Mention how to check it if you know ("tests run with `dotnet test`").
- For big work, ask for a plan first, read it, and split it in two if it has more than about eight steps.

## Why the checks matter

Each step's check is run by the fleet, and one more rule is enforced by code: if a step names a file that did not exist when
it started, that file must exist when it finishes, even if the check passes. (`node --test` exits successfully when there are
no tests at all, so a model that never wrote the test file would otherwise be waved through.)

The runner also notices **scratch files**: files a step created that no step of the plan names (a model that writes `test-x.js`
next to the real test, which `node --test` then runs). A failing step is told about them so it can clean up, and the run log
notes them when a step is done. The fleet reports leftovers; it never deletes a file it did not write.


A step's check is the only thing that turns "the model says it is done" into "it is done", so a weak check hides
problems until a later step fails. The planner is told to write checks that exercise behavior and finish by themselves
(never a server or a watcher). If you see a plan with `echo done` as a check, reject it and ask for real ones.

## How much a model sees

Each request tells Ollama how much of the conversation the model should see (its context size): enough for that request,
in a few fixed sizes from 8K tokens up, at most 32K unless a machine sets its own `contextLength`
([configuration.md](configuration.md#nodes)), and never more than the model supports. Ollama's own default is 4K, which
is less than the fleet's instructions and tools on their own; before the fleet set the size, Ollama quietly dropped the
start of every request and a step's model could lose its task. A larger context takes more graphics memory: a model that
no longer fits runs partly on the processor and becomes much slower, so on a machine with a small card, lower its
`contextLength` or give it a smaller model.

A long step keeps growing: every tool call and its output stays in the conversation. When it would outgrow the most a
machine may use, the fleet shortens the oldest tool outputs and the file contents of old calls, and keeps the
instructions, the task and the latest turns whole; left to Ollama, the start of the conversation (the task) would go
first. The size is estimated at 2.5 characters per token, the worst case measured on fleet traffic (tool calls full of
paths and JSON). Ollama reports the real size of each prompt: when one turns out to have filled its window, the log
says so, the request is asked again with a bigger window, and that machine's later requests are sized larger.

## What to expect from small local models

They are noisy. Expect one to three attempts per step, occasionally a plan that needs a second proposal, and now and then
a step that blocks. That is what the retry loop and the checks are for. A block is a good outcome compared with a
confident wrong answer. The strongest machine plans; ordinary steps start on the cheaper machines, and a step that fails
there moves to the strongest. Planning on a large model can take a few minutes, especially if the model does not fit in
graphics memory. A worker model that describes what it would do ("I'll read the file...") instead of calling its tools
is a sign it is too small for tool use: give that machine a model that handles tools well, such as `ornith:9b`.

## Limits

- One plan runs at a time. Approving another queues it.
- Steps run sequentially by default. An approved consecutive group may run together only when the runner confirms
  disjoint project-local paths and distinct ready tiers. Checks and retries run sequentially.
- Plan mode covers work the assistant does through its tools on the hub. It does not review changes made by other means.
- There is no approval per step once a plan is approved. Approval is the gate, so read the plan.
