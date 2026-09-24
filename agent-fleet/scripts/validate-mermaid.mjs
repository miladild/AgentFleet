// Checks a Mermaid diagram with the real parser and prints one line of JSON.
//   stdin:  the diagram source
//   stdout: {"valid":true}  or  {"valid":false,"error":"..."}
//
// Mermaid's parser needs a browser DOM for some diagram types, so a minimal one is provided
// with jsdom. That is done here, in its own short-lived process, so those globals can never
// leak into the web server that spawns this.
import { JSDOM } from "jsdom";

const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const code = Buffer.concat(chunks).toString("utf8");

const dom = new JSDOM("<!doctype html><html><body></body></html>");
globalThis.window = dom.window;
globalThis.document = dom.window.document;
Object.defineProperty(globalThis, "navigator", { value: dom.window.navigator, configurable: true });

let result;
try {
  const { default: mermaid } = await import("mermaid");
  await mermaid.parse(code);
  result = { valid: true };
} catch (error) {
  const message = String((error && error.message) || error).split("\n").slice(0, 4).join(" | ").slice(0, 400);
  result = { valid: false, error: message };
}

process.stdout.write(JSON.stringify(result));
process.exit(0);
