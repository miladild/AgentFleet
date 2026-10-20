# Fleet benchmark

A repeatable answer to "can I leave the fleet running on a task and trust what comes back?". Each task is a small
project, a plan for the fleet to carry out, and **hidden acceptance tests that no model ever sees**. A run is judged by
those tests, not by the plan saying "done".

## Run it

```powershell
.\bench\Test-BenchTasks.ps1                      # no model involved: every task must be sound (see below)
.\bench\Start-BenchBackend.ps1                   # a second backend on port 8010 with a copy of your configuration
.\bench\Invoke-Bench.ps1 -Repeat 3               # every task three times; about 20 to 60 minutes per run
.\bench\Start-BenchBackend.ps1 -Stop
```

The benchmark uses the machines in your fleet configuration, so they are busy while it runs. `Invoke-Bench.ps1` writes
`results.jsonl`, `results.json` and `summary.md` under `bench\results\<time>`, and `run.json` describing the batch
(see [Importing results](#importing-results)). Useful options: `-Tasks early-close,slugify`,
`-WorkerOnly` (never use the hub), `-Review off` (no second opinion), `-AutoRetries 0`, and
`-Tier light` (send every step at that tier). The eight algorithm tasks are written as `standard`. Two small repairs to
existing code, `light-display-label` and `light-request-options`, are written as `light` and each changes one helper plus its
tests.

`-TimeoutMinutes` sets the per-task deadline (default 120). `-NoProgressMinutes` defaults to 0, which leaves the stop off;
set it to a positive number to stop a row when no round or check has finished for that many minutes.

Measure the light-tier repairs separately when calibrating that tier:

```powershell
.\bench\Invoke-Bench.ps1 -Tasks light-display-label,light-request-options -WorkerOnly -AutoRetries 0 -Repeat 3
```

To measure the workers alone, so that the hub's large model is never loaded (it fills the hub machine's graphics memory and slows it),
start the backend with `-NoHubModel` and run with `-WorkerOnly`. `-NoHubModel` points the copied configuration's hub node at a closed
local port, so a plan that tries to use the hub fails at once instead of loading it; the backend's `/health` then says `unhealthy` (503)
because of the hub node, while the workers are listed as ready. With `-WorkerOnly` the second opinion comes from the other worker, so run
one machine's batch at a time and leave the other machine alone while it runs:

```powershell
.\bench\Start-BenchBackend.ps1 -NoHubModel
.\bench\Invoke-Bench.ps1 -WorkerOnly -Tier standard -AutoRetries 0   # the standard-tier worker; -Tier light for the light-tier worker
```

`-AutoRetries 0` disables automatic retries of parked steps; the repair ladder still runs its rounds and conversations.
Use the same explicit task list and settings for before/after comparisons. A partial run does not provide a completion
rate for tasks it never reached. Forcing standard tasks to `light` measures a harder workload than these small repairs.

## Comparing a setting

Two arms of the same tasks differ in one backend setting. Start-BenchBackend.ps1 gives the setting to the side backend when it
starts, and each batch records it, so the arms can be told apart afterwards. Only FLEET_PLAN_TEST_FIRST (off or on) and
FLEET_PLAN_FAIL_FIRST (off, warn or enforce) are accepted. A value the launching shell already holds for either name is not passed on: a name that is not given is removed from the backend's environment while it starts, and the shell gets its own value back afterwards.

Test-first, the setting off against on:

```powershell
# arm 1: test-first off, on the original tasks
.\bench\Start-BenchBackend.ps1 -Setting FLEET_PLAN_TEST_FIRST=off
.\bench\Invoke-Bench.ps1 -Tasks lru-cache,slugify,semver-compare -Repeat 3 -Arm test-first-off

# arm 2: test-first on, on the test-first copies of the same tasks
.\bench\Start-BenchBackend.ps1 -Setting FLEET_PLAN_TEST_FIRST=on
.\bench\Invoke-Bench.ps1 -Tasks lru-cache-tf,slugify-tf,semver-compare-tf -Repeat 3 -Arm test-first-on
```

The test-first copies (`lru-cache-tf`, `slugify-tf`, `semver-compare-tf`) start with a tests step, which runs only when
FLEET_PLAN_TEST_FIRST is on. With the setting off that step can never pass, so run these tasks only in the "on" arm.

Fail-first, the setting off against enforce, on the same tasks:

```powershell
.\bench\Start-BenchBackend.ps1 -Setting FLEET_PLAN_FAIL_FIRST=off
.\bench\Invoke-Bench.ps1 -Tasks lru-cache,slugify,semver-compare -Repeat 3 -Arm fail-first-off

.\bench\Start-BenchBackend.ps1 -Setting FLEET_PLAN_FAIL_FIRST=enforce
.\bench\Invoke-Bench.ps1 -Tasks lru-cache,slugify,semver-compare -Repeat 3 -Arm fail-first-enforce
```

Keep these equal between the arms: the machines and models (the same fleet configuration), the tasks, `-Repeat`, and the other
options (`-Tier`, `-Review`, `-AutoRetries`, `-TimeoutMinutes`). For a worker-only comparison, start both arms with `-NoHubModel`
and run both with `-WorkerOnly`, as described above.

Where the arm shows: `run.json` in the batch folder holds `arm` (the `-Arm` label, or null) and `settings` (what the backend was started with, or null when that is unknown). The settings come from `backend.settings.json` in the work folder, which records the port the backend started on and when it started. A batch uses that file only when its port is the one in `-BaseUrl`; otherwise it warns and records `settings` as null. `-Stop` removes the file, because the backend it describes is gone. `summary.md` has a "Settings" line near the top. The rows in `results.jsonl` do not carry the arm, and the fleet's import does not store it, so keep each arm's batch folder and import them one at a time.

Each arm keeps the machines busy for about three hours. Run only one arm at a time on a machine: two arms on the same machine
share its model and distort both. Starting an arm stops the backend of the previous one and replaces its settings file.
## Trying a candidate model

`scripts\Test-CandidateModel.ps1` runs the benchmark tasks with one model on one worker, without changing your fleet:

```powershell
.\scripts\Test-CandidateModel.ps1 -Node worker1 -Model llama3.2:3b -WhatIf   # the steps and the one configuration change; starts nothing
.\scripts\Test-CandidateModel.ps1 -Node worker1 -Model llama3.2:3b           # the benchmark itself
```

It copies your configuration to a temporary folder with that worker's model replaced, starts a throwaway backend on port
8010 (`-Port` picks another) with that copy and an empty plans folder, and waits up to three minutes for `/health`. It then
runs the benchmark tasks with `-WorkerOnly`, with the hub's model kept out of the run, no second opinion (`-Review off`) and
no automatic retries (`-AutoRetries 0`). Repair steps may still go to another worker; the summary's models column lists the
machines that did the work. `-Tasks` and `-Repeat` work as they do for `Invoke-Bench.ps1`. The results go to
`bench\results\candidate-<node>-<model>-<time>` (`-OutDir` picks another folder). At the end the throwaway backend is
stopped and the temporary copy deleted, also when the run fails or is interrupted. When a run fails, the backend's log files
are copied into the results folder with addresses and this machine's folder paths taken out (best effort: a name written
without a folder path is not removed).

Each task has a 30-minute timeout by default (`-TimeoutMinutes`). A task is also stopped after 15 minutes without a finished
round or check (`-NoProgressMinutes`); set this option to `0` to disable that stop. A new round-classified, check-passed,
or check-failed event restarts the no-progress window. The window is wall-clock time, so a sleep of the computer longer than
the window can stop a healthy task as stalled; such a row shows the asleep minutes and should be run again.
The candidate run header and `run.json` record both settings.

The worker is one of the machines of your live fleet, so:

- The command refuses while a plan on the live backend is running or approved, and when the live backend does not answer
  its plan list, because that cannot show the fleet is idle. It checks once, when it starts, so do not start plans while it
  runs. `-Force` runs anyway.
- It never writes your configuration, never uses the live backend's data folder or port, and checks afterwards that the
  configuration file is unchanged.
- The model must already be pulled on that machine. The command does not pull it.
- A worker that answers on the same Ollama address as another node shares that machine's models. The command warns,
  because loading the candidate there can unload the other node's model while the run goes on.

Before anything starts, it refuses when the node is not in the configuration (names must match exactly); when the node is
the hub's machine, which is the node marked `"fallback": true` or a node whose address is this computer's own (localhost, this
computer's name, one of its addresses, or a name that resolves to one of those; `-AllowHub` tests it anyway: the hub's model is
then loaded, and the run is not limited to workers); when the node uses the `openai` API or is a
vision node; when the port is a live backend's, the web UI's (including the address the installer recorded), Ollama's, or
the port of any node's address in the configuration, or something already listens on it; when the output folder already
holds results; or when the configuration marks no fallback node, because the hub's model could then not be
kept out of the run.

The fit measurement, on a machine's card under Config > Machines, only measures speed and how much of a model fits in
graphics memory. It does not choose models: only the task results say whether a candidate works.

To import the results, import `results.jsonl` from the results folder in the Models tab, or send it through
`POST /api/lab/results/import`.

## What the summary checks (the gate)

1. **No false accept**: no plan ends `done` while the hidden tests fail.
2. **Completion**: at least 80% of the runs end `done` with the hidden tests passing.
3. **Nothing silent**: every other run ends blocked with a reason; none is stuck, times out or errors.
4. **No environment parks**: no step is parked for an environment problem, which means the fleet and not the work was at fault.

It cannot promise that the models solve every task. It shows, with numbers, how often they do, and whether a failure
is honest.

## Importing results

`run.json` is written first in each batch folder. It holds when the batch started, the options it used, and the machines
and models of the backend it ran on: names and models only, no addresses or paths. To keep a finished batch in the
fleet's own record, send its two files to the backend that should keep them (normally the fleet's backend on port 8000):

```powershell
$body = @{
    resultsJsonl = Get-Content -Raw .\bench\results\<time>\results.jsonl
    run          = Get-Content -Raw .\bench\results\<time>\run.json
    label        = 'standard tier, worker only'
} | ConvertTo-Json
Invoke-RestMethod -Uri http://localhost:8000/api/lab/results/import -Method Post -ContentType 'application/json' -Body $body
```

The answer says how many runs were imported, how many were already stored (importing the same file again adds nothing)
and how many lines were skipped because they could not be read or have no plan id, task or status. A run whose plan was
never created, for example because the backend did not answer, has no plan id, so its line is skipped. A run that has a plan
but records no machine is not skipped: it is counted under "not recorded" for its machine and model. `GET /api/lab/models` lists the
counts by machine and model, and `DELETE /api/lab/results/{importId}` removes one import.

The counts say how many runs each machine and model did and how each one ended. A run is counted as done and correct
only when its plan finished and the hidden tests passed. A plan that said done while the hidden tests failed is a false
accept and is counted on its own, never as a success. Parked, timed-out and errored runs are counted as well. A machine
and model with fewer than five runs is marked too few to judge.

The summary lists stalled rows separately and reports the median minutes per completed round. A stalled row is also marked
timed out and records how long it had gone without progress.

The counts do not rank models, do not measure speed (run times are not shown) and do not promise anything about other
tasks or settings. Every import still stored is added up, so runs with different tiers or settings add together when they
share a machine and model. Import only the batches you want to compare, or delete the others. The label helps tell them
apart.

## A task

```
tasks/<id>/
  task.json     id (the folder name), goal, steps[] (title, detail, files, verify, tier, retrySafe)
  project/      the starting project: package.json, a stub, no tests
  hidden/       *.test.js acceptance tests, run only after the plan has finished, never copied to a machine
  solution/     files that overlay project/ to make a correct solution (used only to prove the task can be won)
```

Rules that keep a task fair:

- Plain Node.js (CommonJS, no dependencies). The tests run with `node --test`, so any worker with Node can do the task.
- `package.json` has `"test": "node --test"`. The last step of the plan runs `npm test` and names no files.
- The step text states **every** rule and example the hidden tests depend on, with the exported function names. A model
  that does what the text says must be able to pass; if a hidden test needs something the text does not say, the task is
  unfair.
- The first step names the source file and the test file the model is to write (`src/<x>.js`, `test/<x>.test.js`). The
  starting project has the stub and no test file.
- The hidden tests use fixed values (fixed instants, fixed inputs), never the clock or randomness.
- `Test-BenchTasks.ps1` proves each task is sound: the hidden tests **fail** on `project/` (otherwise they prove
  nothing) and **pass** on `project/` plus `solution/` (otherwise the task cannot be won). With `-BaseUrl` it also
  asks a backend whether it would accept the plan.

## Adding a task

Copy `tasks/early-close` as the model of everything above, write the starting project, the text, the hidden tests and
the solution, then run `.\bench\Test-BenchTasks.ps1 -Tasks <id>` until it says the task is sound.
