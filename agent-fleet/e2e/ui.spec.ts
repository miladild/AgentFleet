import { expect, test } from "@playwright/test";

const baseConfig = {
  triageModel: "llama3.2:latest",
  nodes: [
    {
      name: "hub",
      url: "http://127.0.0.1:11434/v1",
      model: "qwen3-coder:30b",
      purpose: "hard work",
      tier: "heavy",
      vision: false,
      fallback: true,
      caveman: "off",
      ponytail: "off",
    },
  ],
  tools: {},
  mcpServers: {},
  mcpStatus: [],
};

const contextFixture = {
  context: {
    id: "e2e-context",
    title: "Keep errors visible",
    eventCount: 2,
    messageCount: 1,
    updatedAtUtc: "2026-09-27T00:00:00.000Z",
  },
  recentEvents: [
    {
      id: 1,
      taskId: "task-1",
      kind: "error",
      actor: "agent",
      node: "hub",
      atUtc: "2026-09-27T00:00:00.000Z",
      payload: { source: "stream", message: "Model disconnected", partialOutput: "I started the fix" },
      pinned: false,
    },
    {
      id: 2,
      taskId: "task-1",
      kind: "verification",
      actor: "agent",
      node: "worker1",
      atUtc: "2026-09-27T00:01:00.000Z",
      payload: { passed: true, command: "npm test", exitCode: 0 },
      pinned: false,
    },
  ],
  handoffs: [
    {
      id: "handoff-1",
      createdAtUtc: "2026-09-27T00:02:00.000Z",
      envelope: {
        fromAgent: "hub",
        toAgent: "worker1",
        goal: "Keep errors visible",
        currentTask: "Recover from the model disconnect",
        nextAction: "Retry with fallback using the recorded partial output.",
        completionCondition: "The change builds and tests pass.",
        completedWork: ["Started the fix"],
        verificationEvidence: ["npm test passed"],
      },
    },
  ],
  artifacts: [],
  latestCheckpoint: null,
  latestSummary: null,
  preview: {
    text: "Earlier agent error: Model disconnected. Partial output: I started the fix.",
    eventIds: [1, 2],
    artifactIds: [],
    wasTruncated: false,
  },
  pinned: [],
};

const reroutePlanId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const ladderPlanId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

test.beforeEach(async ({ page }) => {
  let config = JSON.parse(JSON.stringify(baseConfig)) as typeof baseConfig;
  let machineChoice: string | null = null;

  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const { pathname } = new URL(request.url());
    const json = (body: unknown, status = 200) =>
      route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

    // Let the local CopilotKit runtime advertise its real default agent; only
    // fleet/backend APIs are fixture-backed in these browser tests.
    if (pathname.startsWith("/api/copilotkit/")) return route.continue();

    if (pathname === "/api/fleet-config") {
      if (request.method() === "PUT") {
        config = { ...config, ...request.postDataJSON() };
      }
      return json(config);
    }
    if (pathname === "/api/fleet-status") {
      return json({
        mode: "conservative",
        planMode: false,
        nodes: [{ name: "hub", model: "qwen3-coder:30b", ready: true, reachable: true }],
        recentActivity: [],
      });
    }
    if (pathname === "/api/fleet-mode") return json({ mode: "conservative" });
    if (pathname === "/api/plan-mode") return json({ enabled: false });
    if (pathname === "/api/live/demo") {
      const after = Number(new URL(request.url()).searchParams.get("after") ?? "0");
      const failed = after > 0;
      return json({
        plan: {
          id: "demo",
          title: "Collapsible live panels",
          goal: "Keep new failures visible while panels are collapsed.",
          status: "running",
          workingDirectory: "C:/workspace/demo",
          assumptions: [],
          openQuestions: [],
          risks: [],
          diagram: null,
          diagramNote: null,
          updatedUtc: "2026-09-27T00:00:00.000Z",
          events: failed
            ? [{ atUtc: "2026-09-27T00:00:02.000Z", stepId: 1, attempt: 1, kind: "check-failed", tier: "standard", node: "hub", detail: "Expected true; received false" }]
            : [],
          steps: [
            { id: 1, title: "Simulate a new failure", detail: "Wait for the event feed to update.", files: [], verify: "npm test", tier: "standard", status: failed ? "failed" : "running", note: null, attempts: 1 },
          ],
        },
        events: after === 0
          ? [{ id: 1, at: "2026-09-27T00:00:01.000Z", step: 1, kind: "route", node: "hub", text: "model call on hub" }]
          : after === 1
            ? [{ id: 2, at: "2026-09-27T00:00:02.000Z", step: 1, kind: "verification", node: "hub", text: "check failed", ok: false }]
            : [],
        nodes: [{ name: "hub", model: "hub/qwen3-coder:30b", ready: true }],
      });
    }
    if (pathname === `/api/live/${reroutePlanId}`) {
      const steps = [{
        id: 1,
        title: "Retry on a selected machine",
        detail: "Keep the requested task tier across all attempts.",
        files: ["src/example.ts"],
        verify: "npm test",
        tier: "light",
        machine: machineChoice,
        parallelGroup: null,
        status: "failed",
        note: "The check failed.",
        attempts: 3,
      }];
      return json({
        plan: {
          id: reroutePlanId,
          title: "Choose a machine for retry",
          goal: "Reroute a failed step without changing its requested tier.",
          status: "blocked",
          workingDirectory: "C:/workspace/demo",
          assumptions: [],
          openQuestions: [],
          risks: [],
          diagram: null,
          diagramNote: null,
          updatedUtc: "2026-09-27T00:00:00.000Z",
          events: [
            { atUtc: "2026-09-27T00:00:00.000Z", stepId: 1, attempt: 1, kind: "attempt-started", tier: "light", node: "worker-b", detail: "" },
            { atUtc: "2026-09-27T00:00:01.000Z", stepId: 1, attempt: 2, kind: "attempt-started", tier: "heavy", node: "hub", detail: "" },
            { atUtc: "2026-09-27T00:00:02.000Z", stepId: 1, attempt: 3, kind: "attempt-started", tier: "heavy", node: "hub", detail: "" },
            { atUtc: "2026-09-27T00:00:03.000Z", stepId: 1, attempt: 3, kind: "plan-blocked", tier: null, node: null, detail: "The check failed." },
          ],
          steps,
        },
        events: [],
        nodes: [
          { name: "hub", model: "qwen3-coder:30b", ready: true, vision: false },
          // As /api/fleet-status sends them: only a worker with a workspace at the step's tier can be chosen for a retry.
          { name: "worker-a", model: "ornith:9b", tier: "light", ready: true, vision: false, workspace: true },
          { name: "worker-b", model: "ornith:9b", tier: "light", ready: true, vision: false, workspace: true },
          { name: "vision", model: "qwen2.5vl:7b", ready: true, vision: true },
        ],
      });
    }
    if (pathname === `/api/live/${ladderPlanId}`) {
      const at = (second: number) => `2026-09-27T00:00:${String(second).padStart(2, "0")}.000Z`;
      return json({
        plan: {
          id: ladderPlanId,
          title: "Repair ladder",
          goal: "Show where a step is on the repair ladder.",
          status: "running",
          workingDirectory: "C:/workspace/demo",
          assumptions: [],
          openQuestions: [],
          risks: [],
          diagram: null,
          diagramNote: null,
          updatedUtc: at(10),
          events: [
            { atUtc: at(0), stepId: 1, attempt: 1, kind: "attempt-started", tier: "standard", node: "worker-a", modelNode: "worker-a", rung: 1, round: 1, detail: "" },
            { atUtc: at(1), stepId: 1, attempt: 1, kind: "round-classified", tier: "standard", node: "worker-a", modelNode: "worker-a", failureClass: "NoOp", filesChanged: 0, rung: 1, round: 1, detail: "Round classified: NoOp." },
            { atUtc: at(2), stepId: 1, attempt: 1, kind: "check-failed", tier: "standard", node: "worker-a", rung: 1, round: 1, detail: "FAILED widget::renders" },
            { atUtc: at(3), stepId: 1, attempt: 1, kind: "rung-changed", tier: "standard", node: "hub", modelNode: "hub", rung: 2, detail: "Climbing from rung 1 (the requested tier) to rung 2 (the hub model, same conversation) on hub: the last round made no edit and changed no file." },
            { atUtc: at(4), stepId: 1, attempt: 1, kind: "attempt-started", tier: "standard", node: "hub", modelNode: "hub", rung: 2, round: 1, detail: "Round 1: the output of the failed check goes back into the same conversation." },
            { atUtc: at(5), stepId: 1, attempt: 1, kind: "round-classified", tier: "standard", node: "hub", modelNode: "hub", failureClass: "CodeProgress", filesChanged: 1, changedFiles: ["src/widget.ts"], rung: 2, round: 1, detail: "Round classified: CodeProgress." },
            { atUtc: at(5), stepId: 1, attempt: 1, kind: "round-rolled-back", tier: "standard", node: "hub", modelNode: "hub", failureClass: "RolledBack", rung: 2, round: 1, changedFiles: ["src/widget.ts"], detail: "Round 1 (rung 2) left 9 failing where round 2 had 6 failing, so it was undone: src/widget.ts, as they were after round 2." },
            { atUtc: at(6), stepId: 1, attempt: 1, kind: "attempt-started", tier: "standard", node: "hub", modelNode: "hub", rung: 2, round: 2, detail: "Round 2: the output of the failed check goes back into the same conversation." },
          ],
          steps: [
            { id: 1, title: "Fix the failing tests", detail: "Make the widget tests pass.", files: ["src/widget.ts"], verify: "npm test", tier: "standard", machine: "worker-a", status: "running", note: null, attempts: 1 },
          ],
        },
        events: [],
        nodes: [
          { name: "hub", model: "qwen3-coder:30b", ready: true, vision: false },
          { name: "worker-a", model: "ornith:9b", tier: "standard", ready: true, vision: false, workspace: true },
        ],
      });
    }
    if (pathname === `/api/plans/${reroutePlanId}/machine` && request.method() === "POST") {
      machineChoice = request.postDataJSON().machine ?? null;
      return json({ machine: machineChoice });
    }
    if (pathname === "/api/setup") {
      return json({
        ready: true,
        firstRun: false,
        checks: [],
        suggestions: [],
        hub: { hostname: "localhost", addresses: ["127.0.0.1"] },
        hardware: { ramGb: 64, gpu: null, vramGb: 0, freeDiskGb: null },
      });
    }
    if (pathname === "/api/feedback" || pathname === "/api/plans" || pathname === "/api/sessions" || pathname === "/api/live/sessions") {
      return json([]);
    }
    if (/^\/api\/contexts\/[^/]+\/deliveries$/.test(pathname)) {
      return json([
        {
          id: 1,
          node: "worker1",
          atUtc: "2026-09-27T00:03:00.000Z",
          payload: {
            text: "Error and partial response were provided to this agent.",
            eventIds: [1, 2],
            taskId: "task-2",
          },
        },
      ]);
    }
    if (/^\/api\/contexts\/[^/]+$/.test(pathname)) return json(contextFixture);
    return json({});
  });
});

test("loads the main fleet UI and machine status", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByText("Coding agents routed across hub.", { exact: true })).toBeVisible();
  await expect(page.getByText("Recent activity", { exact: true })).toBeVisible();
});

test("keeps new errors visible while the Live view panels are collapsed", async ({ page }) => {
  await page.goto("/live/demo");

  const pipeline = page.locator(".live-tile.focus");
  const rail = page.locator(".live-rail");
  const railSummary = rail.locator(":scope > summary");
  const journal = page.locator(".live-logtile");
  const journalSummary = journal.locator(":scope > summary");

  await expect(pipeline).toBeVisible();
  await expect(railSummary).toContainText("1 busy");
  const pipelineWidth = (await pipeline.boundingBox())?.width ?? 0;
  await railSummary.click();
  await expect(rail).toHaveJSProperty("open", false);
  await expect.poll(async () => (await pipeline.boundingBox())?.width ?? 0).toBeGreaterThan(pipelineWidth);

  await expect(journalSummary).toContainText("0 alerts");
  await journalSummary.click();
  await expect(journal).toHaveJSProperty("open", false);

  // The next feed update adds a failed check while both panels are closed.
  await expect(railSummary).toContainText("1 failed", { timeout: 5000 });
  await expect(journalSummary).toContainText("2 alerts", { timeout: 5000 });
  await expect(pipeline).toBeVisible();

  await journalSummary.click();
  await expect(rail).toHaveJSProperty("open", false);
  await expect(journal.getByText("check failed", { exact: true })).toBeVisible();
  await railSummary.click();
  await expect(journal).toHaveJSProperty("open", true);
});

test("keeps open Live panels inside their bounds at zoomed desktop width", async ({ page }) => {
  await page.setViewportSize({ width: 1000, height: 791 });
  await page.goto(`/live/${reroutePlanId}`);

  const rail = page.locator(".live-rail");
  const side = page.locator(".live-side");
  const inspector = page.locator(".live-inspector");
  const journal = page.locator(".live-logtile");
  const logBody = page.locator(".live-log-body");
  const log = page.locator(".live-log");
  const [railBox, sideBox, inspectorBox, journalBox, logBodyBox, logBox] = await Promise.all([
    rail.boundingBox(),
    side.boundingBox(),
    inspector.boundingBox(),
    journal.boundingBox(),
    logBody.boundingBox(),
    log.boundingBox(),
  ]);

  expect(railBox && sideBox && inspectorBox && journalBox && logBodyBox && logBox).toBeTruthy();
  expect(sideBox!.y + sideBox!.height).toBeLessThanOrEqual(railBox!.y + railBox!.height + 1);
  expect(inspectorBox!.y + inspectorBox!.height).toBeLessThanOrEqual(journalBox!.y + 1);
  expect(logBodyBox!.height).toBeGreaterThan(120);
  expect(logBox!.height).toBeGreaterThan(100);
});

test("reroutes a failed step without changing its requested tier", async ({ page }) => {
  await page.goto(`/live/${reroutePlanId}`);
  await page.getByRole("button", { name: /#1 Retry on a selected machine/ }).click();

  const machine = page.getByRole("combobox", { name: "Machine for step #1" });
  await expect(machine).toHaveValue("");
  expect((await machine.locator("option").allTextContents()).join("\n")).not.toContain("qwen2.5vl");
  await expect(page.locator(".live-node.failed .tier")).toHaveText("light");
  await expect(page.locator(".live-inspector .tier")).toHaveText("light");
  await expect(page.locator(".live-inspector .tier")).not.toHaveClass(/raised/);

  const routeRequest = page.waitForRequest((request) => request.url().endsWith(`/api/plans/${reroutePlanId}/machine`) && request.method() === "POST");
  await machine.selectOption("worker-a");
  const request = await routeRequest;
  expect(request.postDataJSON()).toEqual({ stepId: 1, machine: "worker-a" });
  await expect(machine).toHaveValue("worker-a");
  await expect(page.locator(".live-inspector")).toContainText("task stays light");
});

test("shows where a step is on the repair ladder and says each climb in plain words", async ({ page }) => {
  await page.goto(`/live/${ladderPlanId}`);

  await expect(page.locator(".live-node .tries")).toContainText("rung 2 · round 2");
  await page.getByRole("button", { name: /Fix the failing tests/ }).click();
  await expect(page.locator(".live-inspector")).toContainText("rung 2 (hub model, same conversation), round 2");

  const log = page.locator(".live-log");
  await expect(log).toContainText("round 1 (rung 1): nothing changed · changed no file");
  await expect(log).toContainText("round 1 (rung 2): failed, but the failures changed · changed src/widget.ts");
  await expect(log).toContainText("Climbing from rung 1 (the requested tier) to rung 2 (the hub model, same conversation) on hub");
  await expect(log).toContainText("so it was undone: src/widget.ts, as they were after round 2");
});

test("shows an error and its successful cross-agent handoff in Context", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Context", exact: true }).click();

  await expect(page.getByRole("heading", { name: "Latest handoff" })).toBeVisible();
  await expect(page.getByText(/stream: Model disconnected/)).toBeVisible();
  await expect(page.getByText(/verification.*passed npm test/i)).toBeVisible();
  await expect(page.getByText("Next: Retry with fallback using the recorded partial output.")).toBeVisible();

  await expect(page.getByRole("heading", { name: "What agents actually received" })).toBeVisible();
  await page.getByText(/worker1 - 2 events/).click();
  await expect(page.getByText("Error and partial response were provided to this agent.")).toBeVisible();
});

test("saves per-machine Caveman and Ponytail style settings", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  await page.getByRole("button", { name: "Edit", exact: true }).click();
  await page.getByRole("combobox", { name: "Caveman response style" }).selectOption("lite");
  await page.getByRole("combobox", { name: "Ponytail coding style" }).selectOption("full");
  const saveRequestPromise = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  const saveRequest = await saveRequestPromise;
  const savedNode = saveRequest.postDataJSON().nodes[0];

  await expect(page.getByText("Saved.", { exact: true })).toBeVisible();
  expect(savedNode.caveman).toBe("lite");
  expect(savedNode.ponytail).toBe("full");
  await expect(page.getByRole("combobox", { name: "Caveman response style" })).toHaveValue("lite");
  await expect(page.getByRole("combobox", { name: "Ponytail coding style" })).toHaveValue("full");
});
