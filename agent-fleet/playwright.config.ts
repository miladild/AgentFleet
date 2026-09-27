import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  reporter: "list",
  use: {
    baseURL: "http://127.0.0.1:3101",
    browserName: "chromium",
    headless: true,
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npm run dev:ui",
    url: "http://127.0.0.1:3101",
    env: {
      PORT: "3101",
      FLEET_WEB_HOST: "127.0.0.1",
      AGENT_URL: "http://127.0.0.1:9/",
    },
    timeout: 120_000,
    reuseExistingServer: false,
  },
});
