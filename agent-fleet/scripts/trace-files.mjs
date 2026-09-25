// Copies a script, and every file it needs at run time, into another folder, keeping paths relative to
// the web app. The web app starts the diagram checker (validate-mermaid.mjs) as a child process, so the
// standalone build does not include it or its packages; scripts/Build-Release.ps1 adds them with this.
// Uses the file tracer Next.js itself uses for standalone builds.
//
//   node scripts/trace-files.mjs <script relative to agent-fleet> <target folder>
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const [entry, target] = process.argv.slice(2);
if (!entry || !target) {
  console.error("Usage: node scripts/trace-files.mjs <script> <target folder>");
  process.exit(2);
}

const base = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const { nodeFileTrace } = createRequire(import.meta.url)("next/dist/compiled/@vercel/nft");
const { fileList } = await nodeFileTrace([path.join(base, entry)], { base });

let copied = 0;
for (const file of fileList) {
  const from = path.join(base, file);
  const to = path.join(path.resolve(target), file);
  if (!fs.statSync(from).isFile() || fs.existsSync(to)) continue;
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to);
  copied++;
}

console.log(`${entry}: ${fileList.size} file(s) needed, ${copied} copied (the rest were already there).`);
