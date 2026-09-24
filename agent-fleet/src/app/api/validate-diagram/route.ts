import { spawn } from "node:child_process";
import path from "node:path";

export const runtime = "nodejs";

const MAX_CODE_LENGTH = 20000;
const TIMEOUT_MS = 25000;

// Called by the backend when the model proposes a plan with a diagram, so a syntax error
// is caught inside the tool loop and the model can fix it, instead of the user finding a
// broken picture later. Runs the check in a child process (see scripts/validate-mermaid.mjs).
export async function POST(request: Request) {
  const body = await request.json().catch(() => null);
  const code = body?.code;
  if (typeof code !== "string" || code.length === 0 || code.length > MAX_CODE_LENGTH) {
    return Response.json({ valid: false, error: "code must be a non-empty string" }, { status: 400 });
  }

  const script = path.join(process.cwd(), "scripts", "validate-mermaid.mjs");

  return new Promise<Response>((resolve) => {
    const child = spawn(process.execPath, [script], { stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let settled = false;
    const finish = (response: Response) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(response);
    };
    const timer = setTimeout(() => {
      child.kill();
      finish(Response.json({ valid: false, error: "the diagram checker timed out" }, { status: 504 }));
    }, TIMEOUT_MS);

    child.stdout.on("data", (data) => (stdout += data));
    child.on("error", (error) => finish(Response.json({ valid: false, error: error.message }, { status: 500 })));
    child.on("close", () => {
      try {
        finish(Response.json(JSON.parse(stdout)));
      } catch {
        finish(Response.json({ valid: false, error: "the diagram checker returned nothing" }, { status: 500 }));
      }
    });
    child.stdin.end(code);
  });
}
