import {
  CopilotRuntime,
  createCopilotEndpoint,
  InMemoryAgentRunner,
} from "@copilotkit/runtime/v2";
import { createDefaultAgent } from "@/agent";
import { handle } from "hono/vercel";

const runtime = new CopilotRuntime({
  agents: {
    default: createDefaultAgent(),
  },
  // Fully self-hosted: no CopilotKit Intelligence/cloud account involved.
  runner: new InMemoryAgentRunner(),
});

const app = createCopilotEndpoint({
  runtime,
  basePath: "/api/copilotkit",
});

const rawHandler = handle(app);

// Workaround: the backend's AG-UI hosting library (Microsoft.Agents.AI.Hosting.AGUI,
// a preview package) only deserializes "text" message parts today - an attached
// image's {"type":"image","source":{...}} part is silently dropped before the
// backend ever sees it (confirmed via backend-side diagnostic logging). Repacking
// the image as a marked string inside the text part survives that deserializer;
// the backend then unpacks it back into real image content. Remove this once the
// hosting library supports image parts natively.
async function repackImageAttachments(request: Request): Promise<Request> {
  if (request.method !== "POST") return request;
  if (!request.headers.get("content-type")?.includes("application/json")) return request;

  const bodyText = await request.text();

  // Rebuild from primitives (url/method/headers), not by passing `request` itself as
  // the constructor's clone source - cloning an existing Request instance across the
  // fetch polyfill boundaries Next.js uses here throws "Cannot read private member
  // #state from an object whose class did not declare it".
  const rebuild = (newBody: string) =>
    new Request(request.url, { method: request.method, headers: request.headers, body: newBody });

  let body: unknown;
  try {
    body = JSON.parse(bodyText);
  } catch {
    return rebuild(bodyText);
  }

  const messages = (body as { messages?: unknown }).messages;
  if (!Array.isArray(messages)) return rebuild(bodyText);

  let changed = false;
  for (const message of messages) {
    const content = (message as { content?: unknown }).content;
    if (!Array.isArray(content)) continue;
    (message as { content: unknown[] }).content = content.map((part) => {
      const p = part as { type?: string; source?: { type?: string; value?: string; mimeType?: string } };
      if (p.type === "image" && p.source?.type === "data" && p.source.value && p.source.mimeType) {
        changed = true;
        return { type: "text", text: `[[FLEET_IMAGE:${p.source.mimeType}:${p.source.value}]]` };
      }
      return part;
    });
  }

  return rebuild(changed ? JSON.stringify(body) : bodyText);
}

export const GET = rawHandler;
export async function POST(request: Request) {
  return rawHandler(await repackImageAttachments(request));
}
export const PATCH = rawHandler;
export const DELETE = rawHandler;
