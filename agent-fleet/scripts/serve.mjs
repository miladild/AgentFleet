// Starts the web UI in development ("dev") or production ("start") mode.
//
// `next dev` and `next start` listen on every network interface by default, which puts a
// tool-running assistant on the whole network. This wrapper listens on this machine only, unless
// you say otherwise:
//
//   FLEET_WEB_HOST=0.0.0.0   let other machines on the network open the web UI
//   PORT=3000                the port (default 3000)
import { spawn } from "node:child_process";
import { createRequire } from "node:module";

const mode = process.argv[2];
if (mode !== "dev" && mode !== "start") {
  console.error("Usage: node scripts/serve.mjs <dev|start>");
  process.exit(2);
}

// 127.0.0.1 rather than "localhost": on Windows "localhost" can resolve to the IPv6 loopback only,
// and then http://127.0.0.1:3000 stops working. Browsers reach 127.0.0.1 through "localhost" anyway.
const host = process.env.FLEET_WEB_HOST || "127.0.0.1";
const port = process.env.PORT || "3000";
const nextBin = createRequire(import.meta.url).resolve("next/dist/bin/next");
const args = [nextBin, mode, "-H", host, "-p", port];
if (mode === "dev") args.push("--turbopack");

const child = spawn(process.execPath, args, { stdio: "inherit", env: process.env });
child.on("exit", (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => child.kill(signal));
