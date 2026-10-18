import { expect, test } from "@playwright/test";

const baseConfig = {
  version: "fixture-version-1",
  defaultContextLength: 32768,
  triageModel: "model-triage",
  nodes: [
    {
      name: "hub",
      url: "http://hub.example.test/v1",
      model: "model-x",
      purpose: "hard work",
      tier: "heavy",
      vision: false,
      fallback: true,
      caveman: "off",
      ponytail: "off",
    },
    {
      name: "worker-a",
      url: "http://worker-a.example.test/v1",
      model: "model-y",
      purpose: "worker",
      tier: "standard",
      vision: false,
      fallback: false,
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
const parkedPlanId = "cccccccccccccccccccccccccccccccc";

test.beforeEach(async ({ page }) => {
  let config = JSON.parse(JSON.stringify(baseConfig)) as typeof baseConfig;
  let machineChoice: string | null = null;
  let fitJob: Record<string, unknown> | null = null;
  let fitLatest: Record<string, unknown> | null = null;
  let fitId = 0;

  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const { pathname } = new URL(request.url());
    const json = (body: unknown, status = 200) =>
      route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

    // Let the local chat runtime advertise its default agent; only
    // fleet/backend APIs are fixture-backed in these browser tests.
    if (pathname.startsWith("/api/copilotkit/")) return route.continue();

    if (pathname === "/api/lab/fit/latest") {
      return fitLatest ? json(fitLatest) : json({ reason: "not measured" }, 404);
    }
    if (pathname === "/api/lab/fit" && request.method() === "POST") {
      const body = request.postDataJSON();
      fitId += 1;
      const id = fitId.toString(16).padStart(32, "0");
      const result = { context: 12288, status: "ok", gpuShare: 0.96, tokensPerSecond: 20, loadSeconds: 3, detail: null, completedAtUtc: "2026-10-05T00:00:00Z" };
      const recommendedResult = { ...result, context: 16384, gpuShare: 0.76, tokensPerSecond: 15.2, loadSeconds: 4 };
      const complete = body.node === "hub";
      const measuredModel = config.nodes.find((node) => node.name === body.node)?.model ?? "model-x";
      fitJob = {
        id,
        node: body.node,
        model: measuredModel,
        state: complete ? "complete" : "running",
        currentContext: complete ? null : 16384,
        results: complete ? [result, recommendedResult] : [result],
        recommendation: { context: complete ? 16384 : null, doesNotFullyFit: false, reason: "Keeps enough speed." },
        startedAtUtc: "2026-10-05T00:00:00Z",
        finishedAtUtc: complete ? "2026-10-05T00:01:00Z" : null,
        reason: null,
      };
      if (complete) {
        fitLatest = {
          node: "hub",
          model: measuredModel,
          results: [result, recommendedResult],
          recommendation: { context: 16384, doesNotFullyFit: false, reason: "Keeps enough speed." },
          measuredAtUtc: "2026-10-05T00:00:00Z",
          stale: false,
          staleReason: null,
        };
      }
      return json({ id }, 202);
    }
    if (pathname === "/api/lab/fit" && request.method() === "GET") return json(fitJob ? [fitJob] : []);
    if (/^\/api\/lab\/fit\/[0-9a-f]{32}\/cancel$/.test(pathname)) {
      if (fitJob) fitJob = { ...fitJob, state: "cancelled", reason: "Stopped." };
      return json({});
    }
    if (pathname === "/api/fleet-config/nodes/hub/context" && request.method() === "PATCH") {
      return json({ reason: "The settings changed since you opened them. Reload and try again.", version: "fixture-version-2" }, 409);
    }

    if (pathname === "/api/fleet-config") {
      if (request.method() === "PUT") {
        const { ifVersion: _ifVersion, ...changes } = request.postDataJSON();
        config = { ...config, ...changes, version: "fixture-version-2" };
      }
      return json(config);
    }
    if (pathname === "/api/fleet-config/models") {
      return json({ models: [{ name: "model-new", sizeBytes: 4096 }] });
    }
    if (pathname === "/api/fleet-status") {
      return json({
        mode: "conservative",
        planMode: false,
        nodes: [{ name: "hub", model: "model-x", ready: true, reachable: true }],
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
        nodes: [{ name: "hub", model: "model-x", ready: true }],
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
          { name: "hub", model: "model-x", ready: true, vision: false },
          // As /api/fleet-status sends them: only a worker with a workspace at the step's tier can be chosen for a retry.
          { name: "worker-a", model: "model-y", tier: "light", ready: true, vision: false, workspace: true },
          { name: "worker-b", model: "model-y", tier: "light", ready: true, vision: false, workspace: true },
          { name: "vision", model: "model-vision", ready: true, vision: true },
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
            { atUtc: at(7), stepId: 1, attempt: 1, kind: "check-healed", tier: "standard", node: "hub", rung: 2, round: 2, checkBefore: "npm run build && npm test", checkAfter: "npm run build; if ($?) { npm test }", detail: "The check was changed because the shell cannot parse the check: PowerShell 5.1 has no && (Same programs and arguments, written so the shell accepts them.)" },
            { atUtc: at(8), stepId: 1, attempt: 1, kind: "environment-repaired", tier: "standard", node: "worker-a", failureClass: "Repaired", failureSignature: "node-modules", rung: 2, round: 2, detail: "Install the project's declared dependencies. `npm ci` succeeded: added 120 packages" },
          ],
          steps: [
            { id: 1, title: "Fix the failing tests", detail: "Make the widget tests pass.", files: ["src/widget.ts"], verify: "npm run build; if ($?) { npm test }", originalVerify: "npm run build && npm test", tier: "standard", machine: "worker-a", status: "running", note: null, attempts: 1 },
          ],
        },
        events: [],
        nodes: [
          { name: "hub", model: "model-x", ready: true, vision: false },
          { name: "worker-a", model: "model-y", tier: "standard", ready: true, vision: false, workspace: true },
        ],
      });
    }
    if (pathname === `/api/live/${parkedPlanId}`) {
      const at = (second: number) => `2026-09-27T00:01:${String(second).padStart(2, "0")}.000Z`;
      const step = (id: number, title: string, status: string, dependsOn: number[], note: string | null = null) => ({
        id, title, detail: "", files: [], verify: "npm test", tier: "standard", machine: "worker-a", status, note, attempts: status === "pending" ? 0 : 3, dependsOn,
      });
      return json({
        plan: {
          id: parkedPlanId,
          title: "Two features",
          goal: "One step gets stuck; the independent one goes on.",
          status: "running",
          workingDirectory: "C:/workspace/demo",
          assumptions: [],
          openQuestions: [],
          risks: [],
          diagram: null,
          diagramNote: null,
          updatedUtc: at(30),
          events: [
            { atUtc: at(0), stepId: 2, attempt: 3, kind: "attempt-started", tier: "standard", node: "worker-a", modelNode: "worker-a", rung: 3, round: 3, detail: "" },
            { atUtc: at(10), stepId: 2, attempt: 3, kind: "step-parked", tier: "standard", node: null, failureClass: "Parked", failureSignature: "check-kept-failing", detail: "Step 2 (Feature A) did not pass its check after 6 rounds over 3 attempts: every rung of the repair ladder had its rounds." },
            { atUtc: at(20), stepId: 3, attempt: 1, kind: "attempt-started", tier: "standard", node: "worker-a", modelNode: "worker-a", rung: 1, round: 1, detail: "" },
          ],
          steps: [
            step(1, "Base", "done", []),
            step(2, "Feature A", "parked", [1], "Did not pass its check after 6 rounds over 3 attempts."),
            step(3, "Feature B", "running", [1]),
            step(4, "Integration", "pending", [1, 2, 3]),
          ],
        },
        events: [],
        nodes: [
          { name: "hub", model: "model-x", ready: true, vision: false },
          { name: "worker-a", model: "model-y", tier: "standard", ready: true, vision: false, workspace: true },
        ],
      });
    }
    if (pathname === `/api/plans/${parkedPlanId}/retry` && request.method() === "POST") {
      return json({ id: parkedPlanId });
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

test("notifies when a step parks while its plan keeps running", async ({ page }) => {
  await page.route("**/api/plans", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify([{
      id: parkedPlanId,
      title: "Two features",
      status: "running",
      stepsDone: 1,
      stepsTotal: 4,
      updatedUtc: "2026-10-06T00:00:00.000Z",
      parkedSteps: [{ stepId: 2, title: "Feature A", parkedAtUtc: "2026-10-06T00:00:00.000Z", cause: "check-kept-failing" }],
    }]),
  }));

  await page.goto("/");

  const notice = page.getByRole("complementary", { name: "Recent plan results" });
  await expect(notice).toContainText("Step parked");
  await expect(notice).toContainText("Step 2 (Feature A) needs attention · check-kept-failing. The plan continues.");
  await expect(notice.getByRole("button", { name: "Open plan" })).toBeVisible();
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
  expect((await machine.locator("option").allTextContents()).join("\n")).not.toContain("model-vision");
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

  // A check the fleet changed says what it was and what it is, and a repair of the machine is a line of its own.
  await expect(log).toContainText("the check was changed: npm run build && npm test → npm run build; if ($?) { npm test }");
  await expect(log).toContainText("`npm ci` succeeded: added 120 packages");
});

test("shows a parked step, what waits for it and what goes on, and retries just that step", async ({ page }) => {
  await page.goto(`/live/${parkedPlanId}`);

  // The plan is still running: the parked step is set aside, the independent one is working, the last waits.
  await expect(page.locator(".live-status")).toContainText("running");
  await expect(page.locator(".live-node.parked")).toHaveCount(1);
  await expect(page.locator(".live-node.parked")).toContainText("Feature A");
  await expect(page.locator(".live-node.running")).toContainText("Feature B");
  const waiting = page.locator(".live-node.pending.held");
  await expect(waiting).toHaveCount(1);
  await expect(waiting).toContainText("waiting on #2");
  await expect(page.locator(".parked-count")).toContainText("1");

  // Features A and B stand side by side: independent steps share a stage, and nothing says they run "with" each other.
  await expect(page.locator(".live-node .with")).toHaveCount(0);
  await expect(page.locator(".live-bar .live-ws i")).toHaveCount(3);
  await expect(page.locator(".live-bar .live-ws i.parked")).toHaveCount(1);

  const banner = page.locator(".live-banner.parked-list");
  await expect(banner).toContainText("PARKED");
  await expect(banner).toContainText("the rest of the plan goes on");
  await expect(banner).toContainText("waiting on it: #4");
  await expect(banner).toContainText("#2 Feature A");

  const retryRequest = page.waitForRequest((request) => request.url().includes(`/api/plans/${parkedPlanId}/retry`) && request.method() === "POST");
  await banner.getByRole("button", { name: /retry step 2/ }).click();
  expect(new URL((await retryRequest).url()).searchParams.get("step")).toBe("2");
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
  await page.getByRole("button", { name: "Edit", exact: true }).first().click();
  await page.getByRole("combobox", { name: "Caveman response style" }).first().selectOption("lite");
  await page.getByRole("combobox", { name: "Ponytail coding style" }).first().selectOption("full");
  const saveRequestPromise = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  const saveRequest = await saveRequestPromise;
  const savedNode = saveRequest.postDataJSON().nodes[0];

  await expect(page.getByText("Saved.", { exact: true })).toBeVisible();
  expect(saveRequest.postDataJSON().ifVersion).toBe("fixture-version-1");
  expect(savedNode.caveman).toBe("lite");
  expect(savedNode.ponytail).toBe("full");
  await expect(page.getByRole("combobox", { name: "Caveman response style" }).first()).toHaveValue("lite");
  await expect(page.getByRole("combobox", { name: "Ponytail coding style" }).first()).toHaveValue("full");
});

test("a stale config save shows the conflict and reloads the latest version", async ({ page }) => {
  let conflicted = false;
  await page.route("**/api/fleet-config", async (route) => {
    const request = route.request();
    if (request.method() === "PUT") {
      conflicted = true;
      return route.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ reason: "The settings changed since you opened them. Reload and try again.", version: "server-version-2" }),
      });
    }
    if (request.method() === "GET" && conflicted) {
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ...baseConfig, version: "server-version-2" }) });
    }
    return route.fallback();
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  await page.getByRole("button", { name: "Edit", exact: true }).first().click();
  await page.getByRole("combobox", { name: "Caveman response style" }).first().selectOption("lite");

  const firstSave = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  expect((await firstSave).postDataJSON().ifVersion).toBe("fixture-version-1");
  await expect(page.getByText("The settings changed since you opened them. Reload and try again.", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Reload", exact: true }).click();
  await expect(page.getByRole("combobox", { name: "Caveman response style" }).first()).toHaveValue("off");
  await page.getByRole("combobox", { name: "Caveman response style" }).first().selectOption("lite");
  const secondSave = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  expect((await secondSave).postDataJSON().ifVersion).toBe("server-version-2");
});

test("a worker context-fit measurement shows progress and can be cancelled", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  const workerCard = page.getByRole("textbox", { name: "Name" }).nth(1).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");

  await expect(workerCard).toContainText("Fit not measured yet.");
  await expect(workerCard).toContainText("Speed only, not coding skill.");
  console.log("Fit card before measurement DOM:", await workerCard.evaluate((element) => element.outerHTML));
  const startRequest = page.waitForRequest((request) => request.url().endsWith("/api/lab/fit") && request.method() === "POST");
  await workerCard.getByRole("button", { name: "Measure", exact: true }).click();
  expect((await startRequest).postDataJSON()).toEqual({ node: "worker-a" });

  const panel = workerCard.getByRole("region", { name: "Context fit measurement" });
  await expect(panel).toContainText("16K — ◉ Measuring");
  await expect(panel.getByRole("progressbar", { name: "Context fit measurement progress" })).toHaveAttribute("aria-valuemax", "4");
  await panel.getByRole("button", { name: "Cancel measuring" }).click();
  await expect(panel).toContainText("Measurement cancelled: Stopped.");
});

test("a machine card shows count-only work results and the shared check appears in setup", async ({ page }) => {
  await page.route("**/api/lab/scorecards**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        models: [{
          node: "worker-a", model: "model-y", stepsWorked: 6, stepsDoneOnOwn: 4,
          rescuedByNode: { hub: 2 }, parksByFailureSignature: { check: 1 },
        }],
        infrastructureRoundsByNode: { "worker-a": 2 },
        skippedButCountedDoneByNode: { "worker-a": 1 },
        truncatedPlans: 1,
      }),
    });
  });
  await page.route("**/api/setup", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        ready: true,
        firstRun: false,
        checks: [{ id: "model-results", area: "machines", status: "fail", title: "Model results", detail: "4 of 5 last week, 1 of 6 this week on model-y." }],
        suggestions: [],
        hub: { hostname: "localhost", addresses: ["127.0.0.1"] },
        hardware: { ramGb: 64, gpu: null, vramGb: 0, freeDiskGb: null },
      }),
    });
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  const workerCard = page.getByRole("textbox", { name: "Name" }).nth(1).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");
  const workLine = workerCard.getByText(/^Work last 7 days:/);
  await expect(workLine).toContainText("4 of 6 steps on its own, 1 parked");
  await expect(workLine).toContainText("Rescued by the hub 2 · infrastructure problems 2 · skipped 1");
  await expect(workLine).toContainText("Some long plans were cut.");
  await expect(workLine).not.toContainText("%");

  await page.getByRole("button", { name: /^Setup/ }).click();
  await expect(page.getByText("Model results", { exact: true })).toBeVisible();
  await expect(page.getByText("4 of 5 last week, 1 of 6 this week on model-y.", { exact: true })).toBeVisible();
});

test("a vision machine without history says there is no plan record", async ({ page }) => {
  await page.route("**/api/fleet-config", async (route) => {
    const config = JSON.parse(JSON.stringify(baseConfig));
    config.nodes.push({
      name: "vision-node", url: "http://vision-node.example.test/v1", model: "model-vision",
      purpose: "visual tasks", tier: null, vision: true, fallback: false, caveman: "off", ponytail: "off",
    });
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(config) });
  });
  await page.route("**/api/lab/scorecards**", async (route) => {
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ models: [], truncatedPlans: 0 }) });
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  const visionCard = page.getByRole("textbox", { name: "Name" }).nth(2).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");
  await expect(visionCard.getByText("Work last 7 days: no plan record.", { exact: true })).toBeVisible();
});

test("the plan run log labels missing loop-guard history and shows recorded counts", async ({ page }) => {
  const planId = "dddddddddddddddddddddddddddddddd";
  const now = "2026-10-05T00:00:00Z";
  const plan = {
    id: planId,
    title: "Loop guard recording",
    goal: "Show round notes in the run log.",
    status: "running",
    workingDirectory: null,
    assumptions: [],
    openQuestions: [],
    risks: [],
    diagram: null,
    diagramNote: null,
    steps: [{ id: 1, title: "Record round history", detail: "", files: [], verify: null, tier: "standard", status: "running", note: null, attempts: 1 }],
    updatedUtc: now,
    events: [
      { atUtc: now, stepId: 1, attempt: 1, kind: "round-classified", tier: "standard", node: "worker-a", detail: "Old event", loopGuardNotes: null },
      { atUtc: now, stepId: 1, attempt: 1, kind: "round-classified", tier: "standard", node: "worker-a", detail: "New event", loopGuardNotes: 2 },
    ],
  };
  await page.route("**/api/plans", async (route) => {
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([{ ...plan, stepsDone: 0, stepsTotal: 1 }]) });
  });
  await page.route("**/api/plans/*", async (route) => {
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(plan) });
  });

  await page.goto("/");
  await page.getByRole("button", { name: /^Plans/ }).click();
  await page.getByRole("button", { name: /Loop guard recording/ }).click();
  await page.getByText("Run log (2 events)").click();
  const runLog = page.getByText("Run log (2 events)").locator("xpath=ancestor::details");
  await expect(runLog).toContainText("loop guard notes: not recorded");
  await expect(runLog).toContainText("loop guard notes: 2");
});

test("a newly added machine shows its unmeasured fit line and measure action", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();

  await page.getByPlaceholder("worker.example.test").fill("worker-new.example.test");
  await page.getByRole("button", { name: "Connect", exact: true }).click();
  await page.getByRole("button", { name: "model-new", exact: false }).click();
  const saveRequest = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await page.getByRole("button", { name: "Add and save", exact: true }).click();
  const saved = (await saveRequest).postDataJSON();
  expect(saved.nodes.some((node: { name: string; model: string }) => node.name === "worker2" && node.model === "model-new")).toBe(true);

  const newCard = page.getByRole("textbox", { name: "Name" }).nth(2).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");
  await expect(newCard).toContainText("Fit not measured yet.");
  await expect(newCard.getByRole("button", { name: "Measure", exact: true })).toBeVisible();
});

test("the Setup model list replaces a static fit claim when measurement says it does not fit", async ({ page }) => {
  await page.route("**/api/fleet-config", async (route) => {
    const config = JSON.parse(JSON.stringify(baseConfig));
    config.nodes[0].url = "http://localhost:11434/v1";
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(config) });
  });
  await page.route("**/api/setup", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        ready: true,
        firstRun: false,
        checks: [],
        suggestions: [{ model: "model-x", downloadGb: 6, why: "Fits in 6 GB.", recommended: true }],
        hub: { hostname: "localhost", addresses: ["127.0.0.1"] },
        hardware: { ramGb: 64, gpu: "graphics card", vramGb: 6, freeDiskGb: null },
      }),
    });
  });
  await page.route("**/api/lab/fit/latest?node=hub", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        node: "hub",
        model: "model-x",
        results: [],
        recommendation: { context: 16384, doesNotFullyFit: true, reason: "Measured result." },
        measuredAtUtc: "2026-10-05T00:00:00Z",
        stale: false,
        staleReason: null,
      }),
    });
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await expect(page.getByText(/Measured on this computer: 16K does not fully fit on the graphics card\./)).toBeVisible();
  await expect(page.getByText("Fits in 6 GB.", { exact: true })).toHaveCount(0);
});

test("the hub measurement asks first, offers its result, and prints a save conflict", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  const hubCard = page.getByRole("textbox", { name: "Name" }).nth(0).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");

  await hubCard.getByRole("button", { name: "Measure", exact: true }).click();
  await expect(hubCard).toContainText(/Loads .* on this computer\. It usually takes a few minutes and stops itself after 20 minutes; graphics-heavy apps may slow down\./);
  const startRequest = page.waitForRequest((request) => request.url().endsWith("/api/lab/fit") && request.method() === "POST");
  await hubCard.getByRole("button", { name: "Measure now", exact: true }).click();
  expect((await startRequest).postDataJSON()).toEqual({ node: "hub", confirmHub: true });

  const panel = hubCard.getByRole("region", { name: "Context fit measurement" });
  await expect(panel).toContainText("16K — ✓ Measured");
  await expect(panel).toContainText("Suggest 16K (now 32K).");
  console.log("Fit card after measurement DOM:", await hubCard.evaluate((element) => element.outerHTML));
  const patchRequest = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config/nodes/hub/context") && request.method() === "PATCH");
  await panel.getByRole("button", { name: "Use 16K and save", exact: true }).click();
  expect((await patchRequest).postDataJSON()).toEqual({ contextLength: 16384, ifVersion: "fixture-version-1" });
  await expect(panel).toContainText("The settings changed since you opened them. Reload and try again.");
  await expect(panel.getByRole("button", { name: "Reload", exact: true })).toBeVisible();
});

test("a machine on this computer that is not the hub asks before it is measured", async ({ page }) => {
  await page.route("**/api/lab/fit", async (route) => {
    const request = route.request();
    if (request.method() !== "POST" || request.postDataJSON().confirmHub) return route.fallback();
    return route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({ reason: "Measuring this computer needs confirmation.", needsConfirmation: true }),
    });
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  const workerCard = page.getByRole("textbox", { name: "Name" }).nth(1).locator("xpath=ancestor::div[contains(@class, 'bg-neutral-800/60')][1]");

  await workerCard.getByRole("button", { name: "Measure", exact: true }).click();
  await expect(workerCard).toContainText("Loads model-y on this computer. It usually takes a few minutes and stops itself after 20 minutes; graphics-heavy apps may slow down.");
  await expect(workerCard).not.toContainText("Measuring this computer needs confirmation.");
  const startRequest = page.waitForRequest((request) => request.url().endsWith("/api/lab/fit") && request.method() === "POST" && request.postDataJSON().confirmHub === true);
  await workerCard.getByRole("button", { name: "Measure now", exact: true }).click();
  expect((await startRequest).postDataJSON()).toEqual({ node: "worker-a", confirmHub: true });
  await expect(workerCard.getByRole("region", { name: "Context fit measurement" })).toContainText("◉ Measuring");
});

const approvalId = "d".repeat(32);
const waitingApproval = {
  id: approvalId,
  contextId: "e2e-context",
  tool: "run_command",
  arguments: JSON.stringify({ command: "npm test" }),
  requestedAtUtc: "2026-10-14T10:00:00.000Z",
  expiresAtUtc: "2026-10-14T10:02:00.000Z",
  secondsLeft: 120,
};

test("a chat tool call that waits for approval is shown and can be approved", async ({ page }) => {
  let pending = [waitingApproval];
  await page.route("**/api/approvals", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(pending) }));
  await page.route(`**/api/approvals/${approvalId}/approve`, (route) => {
    pending = [];
    return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ decided: true }) });
  });

  await page.goto("/");

  const prompts = page.getByRole("region", { name: "Approval requests" });
  await expect(prompts.getByText("npm test", { exact: true })).toBeVisible();
  await expect(prompts.getByRole("timer")).toContainText("Time left");
  const approved = page.waitForRequest((request) => request.method() === "POST" && request.url().endsWith(`/api/approvals/${approvalId}/approve`));
  await prompts.getByRole("button", { name: "Approve", exact: true }).click();
  expect((await approved).method()).toBe("POST");
  await expect(prompts).toHaveCount(0);
});

test("a waiting chat tool call is also shown on the live page", async ({ page }) => {
  await page.route("**/api/approvals", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([waitingApproval]) }));

  await page.goto("/live");

  await expect(page.getByRole("region", { name: "Approval requests" }).getByText("npm test", { exact: true })).toBeVisible();
});

test("an approval that was answered elsewhere leaves the card hidden without an error", async ({ page }) => {
  // Still listed on purpose: the page must not bring back a card the user has answered because a 404 came back.
  await page.route("**/api/approvals", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([waitingApproval]) }));
  await page.route(`**/api/approvals/${approvalId}/reject`, (route) => route.fulfill({ status: 404, contentType: "text/plain", body: "Not found" }));

  await page.goto("/");

  const prompts = page.getByRole("region", { name: "Approval requests" });
  await prompts.getByRole("button", { name: "Reject", exact: true }).click();
  await expect(prompts).toHaveCount(0);
  await expect(page.getByText("Could not send your answer")).toHaveCount(0);
});

test("a failed answer puts the card back with a message", async ({ page }) => {
  await page.route("**/api/approvals", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([waitingApproval]) }));
  await page.route(`**/api/approvals/${approvalId}/approve`, (route) => route.fulfill({ status: 500, contentType: "text/plain", body: "Internal Server Error" }));

  await page.goto("/");

  const prompts = page.getByRole("region", { name: "Approval requests" });
  await prompts.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(prompts.getByText("Could not send your answer. Try again.")).toBeVisible();
  await expect(prompts.getByText("npm test", { exact: true })).toBeVisible();
});

test("the Models tab lists the mocked counts in the order given and tags a machine with too few runs", async ({ page }) => {
  await page.route("**/api/lab/models", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        days: null,
        groups: [
          { node: "worker-a", model: "model-y", runs: 7, doneAndCorrect: 5, falseAccepts: 1, parked: 1, timedOut: 0, errors: 0, tasks: ["early-close", "slugify"], tooFew: false, lastRunUtc: "2026-10-12T09:30:00Z" },
          { node: "hub", model: "model-x", runs: 3, doneAndCorrect: 2, falseAccepts: 0, parked: 0, timedOut: 1, errors: 0, tasks: ["slugify"], tooFew: true, lastRunUtc: "2026-10-13T08:00:00Z" },
        ],
        imports: [],
      }),
    }),
  );

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: "Models", exact: true }).click();

  const table = page.getByRole("table", { name: "Counts by machine and model" });
  await expect(table.locator("tbody th")).toHaveText(["worker-a", "hub"]);
  await expect(table.locator("tbody tr").nth(0)).not.toContainText("too few to judge");
  await expect(table.locator("tbody tr").nth(0)).toContainText("12 Oct 2026, 09:30 UTC");
  await expect(table.locator("tbody tr").nth(1)).toContainText("too few to judge");
});

test("the Models tab posts one import with the typed model and prints what the backend kept", async ({ page }) => {
  const resultsText = `${JSON.stringify({ planId: "plan-1", task: "slugify", status: "done", hiddenPass: true, models: "hub" })}\n${JSON.stringify({ planId: "plan-2", task: "slugify", status: "blocked", hiddenPass: false, models: "hub" })}\n`;
  await page.route("**/api/lab/results/import", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ imported: 2, duplicates: 1, skipped: 0, models: ["model-x"] }),
    }),
  );

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: "Models", exact: true }).click();

  const section = page.getByRole("region", { name: "Import a benchmark run" });
  await section.getByLabel("results.jsonl (required)").setInputFiles({
    name: "results.jsonl",
    mimeType: "application/x-ndjson",
    buffer: Buffer.from(resultsText),
  });
  await section.getByLabel("Model on hub").fill("model-x");
  await section.getByLabel("Label (optional)").fill("standard tier, worker only");

  const importRequest = page.waitForRequest((request) => request.url().endsWith("/api/lab/results/import") && request.method() === "POST");
  await section.getByRole("button", { name: "Import", exact: true }).click();
  const body = (await importRequest).postDataJSON();

  expect(body.resultsJsonl).toBe(resultsText);
  expect(body.models).toEqual({ hub: "model-x" });
  expect(body.label).toBe("standard tier, worker only");
  expect(body.run).toBeUndefined();
  await expect(section.getByText("Imported 2 runs. 1 run was already stored and not added again.", { exact: true })).toBeVisible();
});

test("the Tools tab shows the default approval level and saves another one, leaving the wait as it was", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: "Tools", exact: true }).click();

  const commandsAndHttp = page.getByRole("radio", { name: /Commands and HTTP/ });
  const allChanges = page.getByRole("radio", { name: /All changes/ });
  await expect(commandsAndHttp).toBeChecked();
  await expect(allChanges).not.toBeChecked();
  const saveRequest = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await allChanges.check();
  const body = (await saveRequest).postDataJSON();

  expect(body.chatApprovals).toEqual({ mode: "all", enabled: true, timeoutSeconds: 240 });
  expect(body.nodes).toBeUndefined();
  await expect(page.getByText("Saved. Ask before every tool that is not read-only or fleet bookkeeping, including connected tools.")).toBeVisible();
  await expect(allChanges).toBeChecked();
});

test("the Setup tab turns the weekly measurement on and shows an open regression", async ({ page }) => {
  await page.route("**/api/lab/weekly", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({
      enabled: false,
      dayOfWeek: "sunday",
      startHour: 2,
      hours: 4,
      nextWindowStartUtc: null,
      lastRunUtc: "2026-10-11T12:00:00Z",
      nodes: [{
        name: "worker-a",
        model: "model-y",
        lastMeasuredUtc: "2026-10-11T12:00:00Z",
        regression: { since: "2026-10-11T12:00:00Z", summary: "the recommended context went from 16384 to 12288" },
      }],
    }),
  }));

  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();

  await expect(page.getByText(/^Regression since /)).toContainText("the recommended context went from 16384 to 12288.");
  const checkbox = page.getByRole("checkbox", { name: /Measure the workers once a week/ });
  await expect(checkbox).not.toBeChecked();
  const saveRequest = page.waitForRequest((request) => request.url().endsWith("/api/fleet-config") && request.method() === "PUT");
  await checkbox.check();
  const body = (await saveRequest).postDataJSON();

  expect(body).toEqual({ weeklyProbe: { enabled: true, dayOfWeek: "sunday", startHour: 2, hours: 4 } });
  await expect(page.getByText("Saved. The workers are measured once a week, inside the window.")).toBeVisible();
  await expect(checkbox).toBeChecked();
  await expect(page.getByRole("combobox", { name: "Window length" })).toHaveValue("4");
});

test("turning chat approvals on does not hide unsaved machine edits", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Config", exact: true }).click();
  await page.getByRole("button", { name: /^Machines/ }).click();
  await page.getByRole("button", { name: "Edit", exact: true }).first().click();
  await page.getByRole("combobox", { name: "Caveman response style" }).first().selectOption("lite");

  await page.getByRole("button", { name: "Tools", exact: true }).click();
  await expect(page.getByText("Unsaved machine changes: see the Machines tab.")).toBeVisible();
  await page.getByRole("radio", { name: /All changes/ }).check();
  await expect(page.getByText("Saved. Ask before every tool that is not read-only or fleet bookkeeping, including connected tools.")).toBeVisible();

  await expect(page.getByText("Unsaved machine changes: see the Machines tab.")).toBeVisible();
});
