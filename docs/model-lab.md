# Model lab

The model lab helps you pick the largest context size that each machine can use without losing much speed. A context size is how much of a conversation a model can read at once, counted in tokens (short pieces of text). The lab also notices when that changes. It counts how each model did on small benchmark tasks (projects with tests the models never see) and on the steps it worked on in real plans.

Speed and fit are not coding skill. Fit means how much of a model sits in the graphics memory of its machine. A fast model can still write poor code, and the lab never ranks models. Use the numbers as a guide, not as a verdict.

## Words used here

- **Size**: one context size, such as 12K, which means 12,288 tokens.
- **Graphics memory**: the memory on a graphics card. **Ollama** is the program that runs the models on each machine. It keeps a model in graphics memory when it can. A model that does not fit there runs partly on the processor, which is slower.
- **Hub**: the machine that runs the fleet's **backend**, the server program that the web page talks to. The hub also holds the fallback node: when a worker cannot get a step to pass, the hub's model can take it over. **Workers** are the other machines.
- **Plan** and **step**: a plan is a list of steps the fleet works through. You **approve** a plan before it runs. A step the fleet cannot finish is **parked**: it stops and waits for you.
- **Vision machine**: a machine whose model reads images. It is never the fallback.
- **OpenAI-compatible API**: a machine served by a program other than Ollama, such as LM Studio. It chooses its own size, so the lab does not measure it.
- **Notice address**: a private-network address that you set as `notifyUrl`. The fleet sends it one short message when a plan needs you, or when a fit or model result gets worse.

## Measuring fit

Open **Config** on the main page, then the **Machines** tab. Each machine card has a **Measure** button. Vision machines and OpenAI-compatible machines have none.

A measurement loads that machine's model at each size from 12K to 32K (sizes above the model's own limit are skipped), asks it for a short reply, and records the speed and the share of the model in graphics memory. Then it unloads the model, and no other model is touched. One size may take up to five minutes, and the whole measurement stops after 20 minutes. A size measured before is reused when the model and the Ollama version have not changed.

The suggestion is the largest size that keeps at least 70 percent of the speed of the smallest size measured. If the model fits fully in graphics memory at the smallest size, the suggested size must also keep at least 90 percent of the model there.

**Does not fully fit** means less than 90 percent of the model is in graphics memory even at the smallest size, so part of it runs on the processor. The suggestion then needs at least 80 percent of the speed, and the graphics-memory test is dropped, because larger sizes spill further. This is not the same as **Did not fit**, which marks one size where Ollama reported that the model does not fit in memory.

Good to know:

- A measurement is refused while a plan is running or approved. Wait for the plan to finish.
- Measuring the hub asks first. A box says the model will load on this computer for about five minutes, and you choose **Measure now** or **Not now**.
- A measurement does not change your settings. The card offers **Use 16K and save** only when the suggestion differs from the current size and the measurement is up to date. Only that click saves the new size. If the settings changed meanwhile, the save is refused and you are asked to reload.
- A measurement goes out of date when the model or Ollama is updated. Then the button says **Measure again** and the **Use** button is hidden.

## The Work line and the Model results check

Each machine card shows **Work last 7 days** for that machine and model, or "no plan record" when there is none. It counts plan steps from the last seven days:

- "N of M steps on its own": steps the model finished itself, out of the steps it worked on.
- "parked": times a step stopped and waited for you after this model tried it.
- "Rescued by the hub": steps this model tried that the hub then finished.
- "infrastructure problems": attempts lost to a machine or connection failure. They are not counted against the model.
- "skipped": steps you skipped. They count as done in the plan but not as this model's steps.

Under five steps, the line adds "(too few to judge)". There is no score.

The **Model results** check on the Setup tab compares the last seven days with the seven before. It fails when a model's own steps this week are half of last week's or fewer, provided last week had at least one such step and both weeks had at least five steps worked. Its text reads "N of M last week, K of L this week on model (machine)". It warns when a fit measurement is out of date. When a notice address is set, a drop also sends one message per machine per week. The check runs each time the Setup checklist loads.

## Weekly measurement

Off by default. Turn it on in **Config**, **Setup**, under **Weekly measurement**. Choose the day, the start hour and the window length (1 to 12 hours), in the machine's local time. The window may run past midnight.

- Only worker machines are measured. Never the hub, never the fallback, never a vision machine or an OpenAI-compatible machine.
- A measurement starts only inside the window, one machine at a time, and only when no plan is running or approved, no other measurement is running, and no model is loaded on that machine. The fleet checks every ten minutes, and a busy machine is tried again at the next check. No new measurement starts after the window closes, and one already running is not stopped.
- A machine is measured when its last measurement is more than six days old or it has none. Each weekly run measures every size again. A run that was stopped or cut short is neither counted nor compared, and a run is compared only with an earlier run that measured the same sizes.
- A regression is a weekly measurement that is worse than the previous one of the same machine and model: the suggested size went down, the model stopped fully fitting in graphics memory, or the speed at one size fell by more than a quarter. A first measurement, or one after a model change, is never a regression.
- A regression shows as a warning named **Weekly fit measurement** on the Setup tab, until a later weekly run finds none. With a notice address set, one message is sent when a regression first appears, and none is sent again while it stays.
- The **Status** box under the switch shows the next window, the last run, and each worker's last measurement and any open regression.

## Benchmark results and the Models tab

A batch is one run of the bench script over the task list. Each task try in it counts as one **run**, and the batch writes a `results.jsonl` file. Open **Config**, then **Models**, and choose that file from a finished batch (required). You can also choose the batch's `run.json`. Without it, type the model each machine ran. The file is read in the browser and sent once. Files over 2 MB are not sent. A label is optional, up to 80 characters. Importing the same file again adds nothing, because each run is recognised by its plan.

The table has one row per machine and model:

- **Machine**: the machine that did the work. A run that used several machines is one row, with their names joined by commas.
- **Model**: **not recorded** means the batch does not say which model a machine ran, or names no machine. **mixed** means the machines of one run ran different models.
- **Runs**: how many runs. **Too few to judge** means fewer than five.
- **Done and correct**: the plan finished and its hidden tests passed. Hidden tests are tests the models never see.
- **False accepts**: the plan said done but its hidden tests failed. These are never counted as successes.
- **Parked**, **Timed out**, **Errors**: runs in which a step was parked, the run timed out, or an error was recorded.

The counts do not rank models, show no run times, and say nothing about other tasks or settings. Imports add up, so runs with different settings add together when they share a machine and model. Import only the batches you want to compare. **Remove**, then **Yes, remove**, takes out one import and its runs.

## Trying a candidate model

A candidate is a model you want to consider for one worker. `scripts\Test-CandidateModel.ps1` runs the benchmark tasks with it on that worker, and does not change your fleet. Preview first:

    .\scripts\Test-CandidateModel.ps1 -Node worker1 -Model qwen2.5-coder:7b -WhatIf
    .\scripts\Test-CandidateModel.ps1 -Node worker1 -Model qwen2.5-coder:7b

The first command only prints the steps. The second runs them. The script copies your configuration to a temporary folder with that one machine's model changed. It starts a throwaway backend, a second temporary copy of the backend, on port 8010 (a port is the number a program listens on; `-Port` picks another), with an empty plans folder. It waits up to three minutes for that backend to answer. Then it runs the tasks on that worker alone. The hub's model is kept out, so a step that tries the hub fails at once instead of loading it. There is no reviewer model checking each step and no automatic retry. A repair step may still go to another worker, and the summary lists every machine that did work. By default every benchmark task runs once. `-Tasks` picks some, and `-Repeat` sets how many times each task runs. Results go to `bench\results\candidate-<node>-<model>-<time>`, or to `-OutDir`.

It refuses, and starts nothing, when:

- the node name is not in the configuration (names must match exactly), or two nodes share it
- the node is a vision machine or uses an OpenAI-compatible API
- the node is the hub's machine, unless you pass `-AllowHub` (then the hub's model loads and the run is not limited to workers)
- no node is marked as the fallback
- the port belongs to the live backend, the web page (3000) or Ollama (11434), or something already listens on it
- the output folder already holds results
- a plan on the live backend is running or approved (`-Force` runs anyway, and the candidate then shares the machine with the plan)

It never writes your configuration, and it checks at the end that the file is unchanged. The model must already be on that machine; the script does not download it. The throwaway backend is stopped and its copy deleted even when the run fails. When a run fails, its backend logs are copied into the results folder.

The run time depends on the model and the machine. The bench README gives 20 to 60 minutes for every task run three times across the fleet. When the run finishes, import `results.jsonl` in the Models tab, or send it to `POST /api/lab/results/import`. To see what each machine has installed, run `scripts\Get-FleetModels.ps1` on the hub.

## Settings and endpoints

An **endpoint** is an address on the backend that the web page or a script calls.

| Setting | Default | Meaning |
|---|---|---|
| `lab.minSpeedFraction` | 0.70 | Speed a suggested size must keep, as a share of the smallest size's speed |
| `lab.fullFitShare` | 0.90 | Share of the model that must sit in graphics memory |
| `lab.constrainedSpeedFraction` | 0.80 | Stricter minimum speed when the model does not fully fit at the smallest size |
| `weeklyProbe.enabled` | `false` | Measure the workers once a week |
| `weeklyProbe.dayOfWeek` | `"sunday"` | Day the window opens |
| `weeklyProbe.startHour` | `2` | Hour the window opens, 0 to 23 |
| `weeklyProbe.hours` | `4` | Window length, 1 to 12 hours |
| `notifyUrl` | not set | Private-network address for notices, including fit and model regressions |
| `nodes[].contextLength` | `32768` | A machine's context size; the save button changes it |
| `FLEET_LAB_DIR` (environment) | `lab` beside the plans folder | Where fit history, weekly state, notice state and imports are kept |

| Method and path | What it does |
|---|---|
| `POST /api/lab/fit` | Start a measurement: `{ "node", "force", "confirmHub" }`. `202` with the job id, or `409` with the reason. `force` measures every size again |
| `GET /api/lab/fit` | Recent measurement jobs, up to 20 |
| `GET /api/lab/fit/latest?node=` | The latest measurement of one machine, or `404` |
| `POST /api/lab/fit/{id}/cancel` | Stop a running measurement: `200`, or `404` |
| `GET /api/lab/weekly` | Weekly settings, next window, last run and each worker's status. Read only |
| `GET /api/lab/scorecards?days=` | Plan-step counts by machine and model. `days` 1 to 90, default 7 |
| `POST /api/lab/results/import` | Import a batch: `{ resultsJsonl, run?, models?, label? }`. Answers `{ imported, duplicates, skipped, models }`. `413` or `400` with a reason |
| `GET /api/lab/models?days=` | Benchmark counts by machine and model. `days` 1 to 3650; omitted means all |
| `DELETE /api/lab/results/{importId}` | Remove one import: `200` with `{ removed }`, or `404` |

The save button uses `PATCH /api/fleet-config/nodes/{name}/context`, which is outside `/api/lab`. [configuration.md](configuration.md) lists every setting and the rest of the HTTP interface.
