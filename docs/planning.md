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
   fix when it finds one: a missing project folder, a check that is a sentence rather than a command, a check that
   relies on a file only a later step creates, an unbounded check, or a final check that is not a whole-project build,
   typecheck or test suite. That last command is the runner-owned final validation, shown on the plan card before approval.
   Worker toolchains
   are checked only when that worker runs the step; Fleet does not install runtimes from project files. A revised plan
   replaces the earlier proposal in the same chat, so there is only ever one waiting for approval, and the turn ends
   once a plan is saved. Before approving, configure a worker workspace for every pinned machine or at least one worker
   at each step's tier in **Config > Machines**. Without a ready configured worker, the plan blocks visibly and does not
   route code work to the hub.
5. **You decide.** Review each step's restart setting and the **Recovery** choice, then press **Approve and run**, press **Reject**, or reply in the chat with what to change and it proposes a
   revised plan. Typing `approve` also works. (`approve the idea but change step 3` counts as feedback, not approval.)
   Hub rescue is on by default: when a step's worker cannot get a failing check to pass, the hub model takes over (see
   the repair ladder below). Choose **Worker only** to keep every model call on the step's workers. Existing plans keep
   their saved recovery scope. **Broken checks** decides what happens when a step's check is itself broken rather than
   failing (see below): **Fix automatically**, the default, or **Ask me first**. **Second opinion** chooses whether a model
   that did not write the code reads each step's changed files against the step's text, and answers pass or fail, before
   the step is accepted: **A second model reads each step**, the default, or **Off**. The reviewer is the hub model when
   hub rescue is allowed and the hub did not do the work, otherwise another machine; it can only read. It costs one extra
   model call per step and is skipped when the step names no files, nothing it changed could be read, or no independent
   machine is available. If the reviewer times out or cannot answer, the step is accepted on its check. On fail, the
   reviewer's problems are sent back like a failing check and the step climbs the repair ladder. Each step also says whether it
   may resume automatically after a backend restart; steps not marked restart-safe stop for review if interrupted.
   Any step can override the plan's Recovery, Parked steps, or Second opinion setting for itself only. Use this to
   keep a sensitive step on its worker only (Rescue: worker-only), to wait for you rather than retry (Retries: 0),
   or to skip a trivial step's review (Review: off). The step's own setting is shown on its row in the plan card.
   In the web UI, you can optionally check **Save a Markdown copy** before approving; it writes the plan into the project
   folder shown on the card. The default is off.
6. **The run.** Approval starts the plan **in the background**, in order. Each step requires a configured worker
   workspace and runs sequentially, so its changes sync back before the next step stages the project. Parallel groups
   remain visible in the plan but do not run together in worker mode.
   - Each step has two visible routes: the model that answers and the worker workspace where file tools and checks run. A
     hub model rescue changes only the model route; the workspace machine and requested task tier stay fixed. For unpinned
     steps in Aggressive mode, the hub may be the model from the first attempt while all project tools still use a worker.
   - Each step goes to a configured worker workspace, in a fresh model session that sees only the goal, the assumptions,
     the step itself, a summary of earlier steps, and bounded context from the files named by that step. The runner reads
     up to eight project-local text files (32 KiB each, 48,000 characters total) before each attempt. Missing, binary,
     linked, unreadable, and out-of-project files are skipped. The contents are labeled as untrusted data, never as
     instructions.
    - File tools, shell commands and checks run on the selected worker. Git inspection goes through the dedicated read-only tool; Git and GitHub CLI commands through the shell are blocked.
     The runner syncs changed files back to the hub checkout before and after checks; content hashes detect conflicting
     worker or hub edits and block instead of overwriting. Existing worker files that differ from the hub are preserved and
     staging stops. External web, HTTP, MCP/custom, GitHub CLI, publishing, deployment, and known remote-shell commands are blocked
     at the tool boundary. **Only a passing check and successful sync mark a step done.**
   - A model turn ends after 25 rounds of tool calls (`FLEET_PLAN_STEP_TOOL_ROUNDS`) or 20 minutes, whichever comes
     first, so a model going round in circles cannot hold a machine; the step's check then decides. A turn also ends early
     when the model makes the same call and gets the same error back: the third time the result carries a short note ("this is
     not working, try a different approach"), the fifth ends the turn. Only failing calls count, and timings in the error
     text do not make two failures different.
   - A step has a **working time** backstop of 4 hours (`FLEET_PLAN_STEP_MINUTES`): its model calls and its checks, not the
     time it waits for a machine. It is not what decides that a step is stuck: a step that is getting closer is not stopped for
     how long it has been at it (see the repair ladder below, where a round that leaves fewer failing tests than any round
     before it does not use up the rung's rounds), and one that stands still is moved up the ladder and then parked. A model
     call is cut short when it would run past the backstop or past the plan's run deadline, and a step that has used its
     working time without passing stops with the log's **step time limit** line and everything it tried; retrying the step
     gives it a fresh clock. One round of model work may run `FLEET_PLAN_ATTEMPT_MINUTES` (30 by default).
  - **A broken check is not a failing check.** Fleet tells them apart. A failing test, an assertion or a compiler error is a
    verdict: only the model changes the code, and the check is never edited. A check that cannot run or finish is a defect of
    the plan, and spending a round on it only wastes the night. The signs are a command that never exits (a dev server, a
    watcher), a command the machine's shell cannot parse (Windows PowerShell 5.1 has no `&&`), a program or file the check
    names that is not there, a check stopped at its time limit twice with the same output, and a model's `report_blocker`
    call (`check_cannot_pass`) whose evidence is really in the check's output. A sign only starts an **audit**: the hub's
    model when the plan allows hub rescue (otherwise the model of the worker that did the work) reads the project, with
    read-only tools, and proposes one replacement with `propose_check`; it may also answer that the check is fine. A fixed
    set of rules, not the model, decides whether the proposal is used. It must be one line that finishes, name only
    programs and files that exist (or that a step creates), and not pass whatever happens (`echo`, `true`, `exit 0`,
    version probes, `|| true`). It is either the same programs and arguments written so that the shell accepts them, or it
    still runs the project's tests, typecheck or build and keeps everything the original ran. The last step's check must
    still be the whole-project build and tests. A refused proposal gets one more try with the reason; after that the step
    stops with what was refused and why. A step's check changes at most twice, and a sign that two audits found nothing behind is ignored for the rest of the step. The original is kept on the step
    (`originalVerify`, shown on its card), the run log has a **check healed** line with the check before and after, and the
    report lists **Checks changed by Fleet**. A healed check runs again at once in the same workspace and costs no round.
    With **Ask me first**, the step stops with the proposal instead; approve again with **Fix automatically** to apply it.
  - **Environment doctor.** When a check fails because the machine lacks what the project itself declares (a fresh worker
    copy has no `node_modules`), the runner repairs it once per cause and step and runs the check again without costing a
    round. It runs `npm ci` (`npm install` when there is no lock file) when a package declared in `package.json` is missing,
    `dotnet restore` when the tool asks for a restore, and `pip install -r requirements.txt` only into the project's own
    virtual environment. These are commands from a fixed list, run in the project folder as the limited worker account:
    never a global install, never elevation, never a package the project does not declare (that is for the model to add).
    A yarn or pnpm project, a Python project without a virtual environment and a missing `.env` are not repaired: the
    check's output the model sees ends with a note saying exactly what is missing. Fleet does not create a `.env` from
    `.env.example` because worker copies exclude `.env` files and a created one would sync back into your project. Each
    repair is an **environment repaired** line in the run log and a section of the report.
    - In a Git project, the runner restores a tracked file that a step deletes when no plan step names it, and reports
      the path (a test once deleted the project's own config file on every run). The runner does not commit between steps;
      read-only Git inspection is available through its dedicated tool, while shell Git commands are blocked. The fleet notes the changed and new files before
     each attempt and, when the attempt has deleted one or put it back to the committed version, writes it back and
     logs **work-restored**, unless a step that has not finished yet names that file. Outside git this protection does
     not exist, so put a project under git before leaving a plan to run.
  - A failed check does not restart the model. The runner sends the check's real output back into the **same conversation**,
    with what changed since the round before (failing names fixed or new, files changed), so the model still has the task and
    everything it already did. One **round** is: the model works, then the runner runs the approved check. The step climbs a
    **repair ladder**, three rounds on each rung (`FLEET_PLAN_ROUNDS_PER_RUNG`), where a round that leaves fewer failing
    names than any round before it is progress and is not counted against the rung (a worker that goes from fourteen failing to
    nine to four is not moved up for having used three rounds; one that stands still is):
    1. the machine the step was given, at its own tier;
    2. the hub model, continuing the same conversation, while file tools and checks stay on the selected worker (only when
       the plan allows hub rescue, and only when the hub answers a small test request);
    3. a fresh conversation, on the hub when the plan allows it and otherwise on a worker, that starts from a brief of what
       the earlier rounds tried and is told to find the root cause first.

    When a worker's call times out and hub rescue is allowed, the step is handed to the hub at once; a worker-only plan
    keeps its worker.

    A round that changed nothing (no edit, and no file changed), or that leaves the same failures with no file changed, does
    not wait for its three rounds: the step climbs at once. Every climb is a **rung changed** line in the run log with its
    reason. A round that leaves the step worse than its best round so far (more failing names, or code that stopped
    building) is **undone**: the runner puts the project's changed and new files back as they were after the best round
    before the next round, tells the model what it broke, and logs **round rolled back**. Lock files and build caches that a
    round only rewrote as a side effect do not count as progress. When the last rung has had its rounds, or the working time
    is used, the plan stops as **Blocked** with the project left at the best state it reached. (The best state is kept in
    memory: a backend restart forgets it, and the step goes on from the files as they are.) The runner executes the last step's approved
    whole-project validation on the worker after edits are synced, records the source snapshot ID and bounded command output,
    and gives failures the same repair rounds. The requested task tier stays fixed. If worker staging has a connection
    failure before model tools or edits run, Fleet can move to another ready worker at the same tier; it waits for the original
    worker when no alternative is ready. Workspace conflicts, security/configuration failures and sync failures stop for review.
     A permitted hub-model rescue does not move files or checks to the hub. In **Live**, select a waiting or failed step to
    pin a configured text machine for its next attempt; that user choice takes precedence over automatic rerouting.
    A running step or one waiting for an earlier dependency cannot be moved. Clear the selection to restore automatic
    tier routing.
  - Checks run on the selected worker. Provision coding workers with their required runtimes before approval (Windows
    setup supports `Setup-Worker.ps1 -AllowFrom <hub-private-ip> -InstallToolchains`; other platforms use the operator's
    trusted package manager).
    If a check reports a missing runtime or incompatible .NET SDK, Fleet may start a fresh conversation on another ready
    worker at the same tier while attempts remain; if no candidate can run the check, it stops and identifies
    the worker so its toolchain can be installed. Plan proposal checks the syntax and declared paths but
    does not assume a worker has the hub's toolchain.
  - A plan has an eight-hour default run deadline and a cross-process lease prevents two backend instances from running
    it at once. Infrastructure waits, attempt counts and a step's place on the repair ladder survive backend restarts; the
    open conversation does not, so a step resumes in a new conversation that starts from a brief of the rounds before. A
    restart-safe step may resume after a restart if its workspace was synced; an interrupted unsynced workspace is always
    preserved and blocked for review.
   - **A stuck step is parked, not the end of the plan.** A plan is a graph, not a queue. Each step may say which earlier
     steps it needs (`dependsOn`); by default each step needs the one before it (a parallel group's steps need what came
     before the group), and the last step, the final validation, always needs every other step. When a step cannot be
     finished (its repair ladder is spent, its check is broken beyond repair, the machine cannot run it, its working time
     is used, or it keeps failing in a way nobody understands) it is **parked** with its reason, and only the steps that
     depend on it wait: the runner goes on with every step that does not. Waiting for a machine to come back is not
     parking. The harder stops still end the whole run: the run deadline, and a restart in the middle of a step that is not
     safe to repeat. When nothing more can run, the plan stops as **Blocked** with the first parked step's reason, the steps
     that were not run, and what is done; the steps that did finish stay done and your files stay as they are. In **Live**, a
     parked step is dashed and amber, the steps waiting for it say `waiting on #2`, and a banner lists each parked step with
     **retry step** (a fresh repair ladder for that step alone, nothing else restarts) and **skip step and go on** (it counts
     as done without its check, so what waited for it can start). Before a step is parked, it is retried automatically first: the
     runner gives the step a fresh repair ladder starting from the project as the best round left it, and tells the model what
     went wrong. Each plan has a default budget of 2 automatic retries per step (settable 0 to 5 when approving); the run log
     shows each one as a "step retried" line with the count. Nobody is notified while retries remain and the step is not stopped.
     Once the budget is used, the step is parked as before and you are notified once. A retry you press manually starts the
     step's budget again. Steps with environment problems (a missing runtime), broken checks, or unknown failures are not
     retried automatically, and nothing is retried after the run deadline. Approving a blocked plan again, as before, tries
     every parked step again. A plan proposed with a dependency on an unknown step, on itself or on a later step (that includes every
     cycle), or between two steps of one parallel group, is sent back to the planner. In a plan file, a step can say
     `- Depends: 1, 3`.
   - **The user is told once** when a step is parked, when the run ends with steps that need you, when the plan is done and
     when the run deadline passes: a line in the run log (**step parked**, **plan needs attention**, **plan done**,
     **run deadline exceeded**), one message in VS Code per new parked step (never repeated for the same count, not even after a
     reload), and, if `notifyUrl` is set in the fleet config ([configuration.md](configuration.md#notifyurl)), one small JSON message to
     that address. The message holds the event, the plan's and the step's titles, the counts of done and parked steps and a
     cause word (`check-kept-failing`, `check`, `environment`, `working-time`, `no-change`, `review`, `unknown-failure`): never a file's contents or a
     check's output.
7. **Afterwards.** The **Plans** button lists every plan and how far it got; in VS Code, **`@fleet /status`** shows the
   current plan's report in the chat, with **Stop it**, or **Approve and resume** for a blocked plan. Close the browser and
   VS Code, go to bed: the plan runs in the backend. **Stop** halts a running plan.

## Watching it live

`http://localhost:3000/live` follows the fleet in a page of its own: a plan that is running, else a plan being worked
out, else one waiting for approval, else the newest plan. When that changes, the page changes with it.
`/live?context=<conversation id>` follows one conversation, `/live/<plan id>` shows one plan, and `/live?pick` lists the
plans. In the web UI, the **Live** button next to **Plans**, the **Live** link on a plan and the **live** badge on a
session that carried a plan open it. In VS Code, **Watch it live** appears as soon as `@fleet /plan` starts, under a
proposed plan, under `@fleet /status` and after approving; the command **Agent Fleet: Watch the running plan live**
opens it too. It opens in VS Code's Simple Browser (`agentFleet.webUrl` sets the web UI's address when it is not on the
backend's machine at port 3000).

- **Planning.** While the strongest machine works out a plan, the page shows it at the centre of a map: every file,
  folder and search it goes through lands around it, the latest one lit, with what it last said underneath and how
  long it has been thinking. Once it proposes the plan, the page shows the plan. When it answers in the chat without
  a plan (it had a question, or the request was not for one), the page says so and shows the answer.
- **Pipeline.** The steps from left to right in stages, by how far they are from the start of the plan: steps that do not
  depend on each other stand side by side (top to bottom in a narrow pane), and a line joins a step to what it waits for.
  A running step glows and labels the model machine separately from its worker workspace, alongside its latest tool call; data flows along the edges into
  it. A step that needed repair shows where it is on the repair ladder (`↻ rung 2 · round 2`) and keeps its requested tier
  visible. The machine chip and run log show the selected and actual responder, including an automatic climb to the hub. Hover a step for its
  latest lines, click it for its instructions, its check, its next-attempt machine selector and the output of its last
  check.
- **What needs you.** A plan waiting for approval has **approve and run** and **reject** at the top. A blocked plan
  says at which step, after how many rounds and on which machines, and why in plain words (the check's failing line, a
  check that never finishes, a file that was never created), with **retry** (a fresh repair ladder for that step),
  **skip the step and go on** (it counts as done without its check) and **details**. A stopped plan offers **resume**.
- **Machines.** Each machine, what it is working on, and its tool calls over the last two minutes.
- **Log.** Everything the machines do, as a terminal: model calls, tool calls and results, what the model said, and
  the runner's own events with the attempt, rung and round. Filter it by step, or to errors only.

It asks the web server for news every two seconds while the plan runs (every fifteen once it has stopped), and not at
all while the tab is hidden; each answer carries only what is new, cut down to a line per event. Animations move only
what the graphics card can move cheaply, pause in a hidden tab, and stop for anyone whose system asks for less motion.
`/live/demo` plays a made-up plan on a loop, to see the view without a real run.

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
  give them different tiers and separate files. A light-tier step that names more than 2 files is raised to standard
  when the plan is saved.
- `Retries` (optional, 0 to 5) is how many times the fleet retries a parked step automatically before it waits for
  you; the plan's default applies if not set.
- `Rescue` (optional, `worker-only` or `allow-hub-rescue`) overrides whether the hub model may take over if the
  worker's model fails; the plan's default applies if not set.
- `Review` (optional, `off` or `auto`) overrides whether a second opinion reads the step's changes before it is
  accepted; the plan's default applies if not set.
- `Check` is the command the fleet runs to decide whether the step worked. Name the files a step creates in `Files`.
- `Depends` (optional) lists the numbers of earlier steps this one needs finished first (`- Depends: 1, 3`). Without it a
  step needs the one before it, and the last step always needs all of them. Use it for steps that really are independent,
  so that one that gets stuck holds back only what needs it.
- Other `##` sections after the steps (notes, an appendix) are ignored.

When your message names a file in this format, the fleet checks it first and tells the planner to hand it over
straight away, rather than reading around the project (which took a planner 13 file reads and 101 seconds). Whatever
the planner then proposes in answer to that message, the steps come from the file: a planner once proposed a two-step
plan of its own for the file's first step. If the planner retypes a plan file it read, or one named earlier in the
chat, the fleet notices (the same number of steps) and takes the steps from the file too.

## Leaving it overnight

What keeps a plan going when something happens in the night:

- **The backend restarts** (a crash, a Windows update, a reboot): the plan, every step's status and the whole record of
  the conversation are on disk, and finished steps are not redone. What happens to the step that was running depends on
  where it ran. A step marked restart-safe that ran on the hub carries on by itself. A step that was running in a worker
  workspace, or that is not marked restart-safe, stops the plan for review: its files on the worker are kept untouched and
  you approve the plan to go on (measured: a crash thirteen seconds into a worker step, before the model had changed
  anything, still stopped the plan). Plan files are flushed to disk before they replace the old one, so a power cut
  cannot leave a half-written plan. Run as a Windows service (`Install-Autostart.ps1 -Mode Service`), the backend is
  started again within seconds if it stops unexpectedly, and at boot; the scheduled tasks start it again within five
  minutes if it is not running (measured: four minutes after the process was killed). The web UI service starts a
  little after boot and is started again if it stops, because it runs from the source folder, which may be on a disk
  that comes up late (on a USB drive it failed at boot and stayed down).
- **A machine goes down or slows down.** Each call goes to a ready machine of the step's tier; a machine that is off, or
  fails a call, is replaced by the fallback machine. A machine that times out rests for ten minutes, so the rest of the
  step does not wait for it again. If no machine can answer at all (the network is down, the fallback is rebooting), the
  step waits and tries again, with a pause that doubles from 30 seconds to 15 minutes (plus a little jitter), and sends a
  small real test request to the machine each time it wakes. While a pause is longer than a minute and the machine is down,
  it also asks the machine every minute (a cheap request while it is down) and ends the pause as soon as the machine answers,
  so a machine that comes back after a minute does not leave the step idle for fifteen. It keeps waiting until the machine
  answers or the plan's run deadline passes. Those waits do not count as attempts or rounds; the run log shows each one.
- **The hub goes to sleep.** While a plan runs, the backend asks the operating system to stay awake (Windows, macOS and
  Linux with systemd). It cannot stop someone closing a laptop's lid or choosing Sleep, and it does nothing for the other
  machines: set those to stay awake (the worker setup's `-KeepAwake`).

## When something goes wrong

A plan that runs while you sleep meets errors. Fleet sorts them into four kinds and deals with each in its own way. All of
it stays inside the plan's run deadline (eight hours by default), and everything it does shows in the run log and the
report.

| What went wrong | What Fleet does by itself | What stops it |
|---|---|---|
| **A machine cannot answer** (off, rebooting, overloaded, the network is down) | Waits with growing pauses, asks the machine for a small real answer each time, and carries on when it can. Moves to another ready machine of the same tier, or to the hub when the plan allows it, but never to a machine that just failed without waiting first. No attempt is spent. | The run deadline. The plan stops as blocked and says so once. |
| **The model's work is wrong** (the check ran and failed) | Sends the real output back into the same conversation. Climbs the repair ladder: more rounds on the step's machine, then the hub model in the same conversation, then a fresh conversation with a brief of what was tried. Undoes a round that made things worse. Stops a model that repeats one failing call. | The ladder is spent or the step's working time backstop (4 hours) is used: the step is **parked**. |
| **The check itself is broken** (it never exits, the shell cannot run it, a program or file it names is not there, it timed out twice the same way) | Asks a model to audit it and applies a replacement only if fixed rules allow it. Keeps the original and logs both. Costs no round. | Two changes to one step, a refused replacement, or **Ask me first**: the step is parked with the reason. |
| **The machine lacks what the project declares** (no `node_modules`, no restored packages) | Installs it once with the project's own command, as the limited worker account, and runs the check again. | Anything the project does not declare, another package manager, no virtual environment: the step's model is told exactly what is missing. |

A **parked** step is set aside with its reason. Only the steps that depend on it wait; the rest of the plan goes on. When
nothing more can run, the plan is blocked and the report says what is done, what is parked, what waited for it, and what to
do next. A step that names files to change is not accepted when its approved check passes but nothing was changed: the round
counts as failed, the step climbs the repair ladder, and if it still changes nothing it is parked with cause `no-change`,
because a check that already passes cannot show that the work is done. An edit by the model's file tools, or any project file
git sees changed (lock files and build caches excepted), counts as a change, in this round or an earlier one. If a step names
test files (test/ or tests/ folders, `__tests__`, `foo.test.ts`, `foo.spec.ts`, `foo_test.go`, `FooTests.cs`, `test_foo.py`),
at least one of those must also have changed before the check's pass is accepted, because a check that only runs existing tests
says nothing about the new work. The model is told to add or extend a test that fails without its change. A step that names no
files is not held to this, nor is the last step of a plan with more than one step, which is the whole-project check. Where git
cannot say what changed the model is given the benefit of the doubt. Retry gives the model another go; Skip is for work that is
already in place. If it happens often, the plan's check is too weak: use one that fails until the work is done, such as a test
the step adds.
You retry or skip one step, or approve the plan again to retry them all. You are told once when a step is parked,
when the run ends with steps waiting, when the plan is done, and when the run deadline passes.

**What Fleet never does**, however stuck a step is:

- Edit, weaken or skip a check that ran and failed on its merits. A failing test is the model's to fix.
- Change a check to one that passes whatever the code does (`echo ok`, `exit 0`, `|| true`), or to one that runs less than the
  original did.
- Change a security setting or the configuration of a machine: the execution policy, the firewall, Defender, the registry,
  services, scheduled tasks, accounts, permissions, environment variables, or a global package. A command that tries is
  refused before it runs, and so is a check that contains one.
- Install anything outside the project, or use elevated rights.
- Go outside the approved machines, reach a remote service, change repository history, or touch a path above the project.
- Hide what it did. Every wait, climb, undone round, changed check and repair is a line in the run log and a section of the
  report.

Some stops are never healed, because a person has to look first: the run deadline, a backend restart in the middle of a step
that is not safe to repeat, a worker whose files conflict with the hub's, and a permission or security failure.

## Reading what happened overnight

Every plan keeps a **run log**: when the run started, each round with its rung, the tier and the machine that answered,
what the model said it did, whether the check passed (and, when it failed, the real output), every climb up the repair
ladder with its reason, timeouts, and how the run ended. Open a
plan from the **Plans** panel and expand **Run log**. **Open the full report** shows the same as a readable page: how the
run ended, where it stopped and why, the files git says changed (when the project is a git repository), per-step attempts,
repair rounds and timings, and the timeline in your local time. In VS Code, **Show status** opens the same report. The report is also
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
no tests at all, so a model that never wrote the test file would otherwise be waved through.) A step may name a pattern,
such as `test/*.test.ts` (`*` stays inside one folder, `**` crosses folders): it counts once any file matches it.

A check must finish by itself. A plan whose check starts a server or a watcher (`npm run dev`, `npm start`, `vite`,
`nodemon`, anything with `--watch`; an npm script is looked up in `package.json`) is not saved, and the planner is told
to check the step with its tests or the build instead. A plan saved before that rule stops at such a step before
spending any attempt on it, and says why; skip the step from the live view and start the server yourself.

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
first. When the latest turns alone are too big (a whole file written and read back), those are shortened too, except
the last call and its result. The size is estimated at 2.5 characters per token, the worst case measured on fleet traffic (tool calls full of
paths and JSON). Ollama reports the real size of each prompt: when one turns out to have filled its window, the log
says so, the request is asked again with a bigger window, and that machine's later requests are sized larger.

## What to expect from small local models

They are noisy. Expect a step to need a repair round or two, an occasional climb to the hub, occasionally a plan that
needs a second proposal, and now and then a step that blocks. That is what the repair rounds and the checks are for. A block is a good outcome compared with a
confident wrong answer. The strongest machine plans; ordinary steps start on the machine assigned to their tier, and
retries keep that task tier. From Live, choose another ready machine for an awaiting, ready, or failed step; running and
dependency-waiting steps cannot be moved. Planning on a large model can take a few minutes, especially if the model does
not fit in graphics memory. A worker model that describes what it would do ("I'll read the file...") instead of calling its tools
is a sign it is too small for tool use: give that machine a model that handles tools well, such as `ornith:9b`.

## Limits

- One plan runs at a time. Approving another queues it.
- Steps run sequentially by default. An approved consecutive group may run together only when the runner confirms
  disjoint project-local paths and distinct ready tiers. Checks and retries run sequentially.
- Plan mode covers work the assistant does through its tools on the hub. It does not review changes made by other means.
- There is no approval per step once a plan is approved. Approval is the gate, so read the plan.
