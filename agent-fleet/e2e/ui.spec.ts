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

test.beforeEach(async ({ page }) => {
  let config = JSON.parse(JSON.stringify(baseConfig)) as typeof baseConfig;

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
