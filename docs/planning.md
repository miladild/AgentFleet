# Plan mode: plan first, then act

Most of what goes wrong with an AI coding assistant goes wrong because it started typing before it understood the
task. Plan mode makes it look first, write down what it intends to do, and wait for you. Then it does the work in small
steps, and checks each step with a real command instead of trusting itself.

## The flow

1. **Turn plan mode on** (switch, top right of the web UI). The setting is stored by the backend, so the web UI and
   `@fleet` in VS Code both follow it.
2. **Ask for the change** in plain words, with the project folder:
   `In C:\src\shop, add rate limiting to the login endpoint.`
3. **Exploring.** The strongest machine reads your code with read-only tools: read, list, find, search, web search. It
   cannot write files or run commands at this stage. That is enforced in code, not by asking the model nicely: write tools
   are not offered, a call to one is turned into an explanation, and the execution layer refuses it as well.
4. **The plan.** The assistant calls `propose_plan`, and the plan appears as a card: goal, assumptions, risks, an optional
   diagram, and numbered steps. Each step names the files it touches, has a **check** (a command that must succeed, such
   as `dotnet build` or `npm test`), and a tier (which kind of machine should do it). The plan is saved as a file.
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
   - A failed check goes back to the model with the real output, up to three attempts. The last attempt is given to the
     strongest machine.
   - If a step still fails, the plan stops as **Blocked**, says which step and why, and leaves your files as they are.
7. **Afterwards.** The **Plans** button lists every plan and how far it got. Close the browser, go to bed: a plan that
   was running when the backend restarted picks up where it left off. **Stop** halts a running plan.

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

## What to expect from small local models

They are noisy. Expect one to three attempts per step, occasionally a plan that needs a second proposal, and now and then
a step that blocks. That is what the retry loop and the checks are for. A block is a good outcome compared with a
confident wrong answer. The strongest machine plans; ordinary steps run on the cheaper machines, and the last attempt at
any step escalates to the strongest. Planning on a large model can take a few minutes, especially if the model does not
fit in graphics memory.

## Limits

- One plan runs at a time. Approving another queues it.
- Steps run sequentially by default. An approved consecutive group may run together only when the runner confirms
  disjoint project-local paths and distinct ready tiers. Checks and retries run sequentially.
- Plan mode covers work the assistant does through its tools on the hub. It does not review changes made by other means.
- There is no approval per step once a plan is approved. Approval is the gate, so read the plan.
