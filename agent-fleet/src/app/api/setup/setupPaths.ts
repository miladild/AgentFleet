/**
 * The backend paths under /api/setup that the web UI may reach through its own proxy route. Anything else is refused
 * there, so the browser cannot use the proxy to reach the rest of the backend. src/components/setupProxyPaths.test.mjs reads
 * every /api/setup path the UI calls and checks it is allowed here, so a call added to the UI cannot be forgotten.
 */
const ALLOWED =
  /^(|hub|pull|pulls|pulls\/[0-9a-f]{16}\/cancel|start-ollama|ollama-versions|ollama-versions\/check|ollama-update|ollama-updates|sandbox|sandbox\/(key|test|install-key|prepare))$/;

/** Whether the proxy passes this path on: everything after /api/setup/, joined with slashes. */
export function setupPathAllowed(joined: string): boolean {
  return ALLOWED.test(joined);
}
