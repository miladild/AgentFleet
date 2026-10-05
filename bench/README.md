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
`results.json` and `summary.md` under `bench\results\<time>`. Useful options: `-Tasks early-close,slugify`,
`-WorkerOnly` (never use the hub), `-Review off` (no second opinion), `-AutoRetries 0`, `-TimeoutMinutes`, and
`-Tier light` (send every step at that tier). The eight algorithm tasks are written as `standard`. Two small repairs to
existing code, `light-display-label` and `light-request-options`, are written as `light` and each changes one helper plus its
tests. Measure these separately when calibrating the light tier:

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

## What the summary checks (the gate)

1. **No false accept**: no plan ends `done` while the hidden tests fail.
2. **Completion**: at least 80% of the runs end `done` with the hidden tests passing.
3. **Nothing silent**: every other run ends blocked with a reason; none is stuck, times out or errors.
4. **No environment parks**: no step is parked for an environment problem, which means the fleet and not the work was at fault.

It cannot promise that the models solve every task. It shows, with numbers, how often they do, and whether a failure
is honest.

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
