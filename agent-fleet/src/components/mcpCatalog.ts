export type McpServerConfig = {
  type: string;
  command?: string | null;
  args?: string[] | null;
  env?: Record<string, string> | null;
  url?: string | null;
  headers?: Record<string, string> | null;
  enabled: boolean;
};

export type CatalogEntry = {
  id: string;
  title: string;
  what: string;
  needs: string;
  server: McpServerConfig;
  /** Shown in the form after picking it: what to fill in or set up first. */
  setup?: string;
};

// A short list of servers that are useful for coding and known to work with this fleet. Anything else
// can be added with the Custom form or by pasting an mcp.json entry.
export const MCP_CATALOG: CatalogEntry[] = [
  {
    id: "microsoft-learn",
    title: "Microsoft Learn",
    what: "Search and read official Microsoft and Azure documentation and code samples.",
    needs: "Nothing. No account.",
    server: { type: "http", url: "https://learn.microsoft.com/api/mcp", enabled: true },
  },
  {
    id: "context7",
    title: "Context7",
    what: "Up-to-date documentation for thousands of libraries and frameworks (React, Next.js, EF Core, ...).",
    needs: "Nothing to start. A free API key raises the limits.",
    server: { type: "http", url: "https://mcp.context7.com/mcp", enabled: true },
    setup:
      "Optional: with an API key, add a header CONTEXT7_API_KEY = ${env:CONTEXT7_API_KEY} and set that environment variable on the hub.",
  },
  {
    id: "playwright",
    title: "Playwright browser",
    what: "Drive a real browser: open pages, click, fill forms, read the page, take screenshots.",
    needs: "Node.js on the hub. Downloads a browser the first time.",
    server: { type: "stdio", command: "npx", args: ["-y", "@playwright/mcp@latest"], enabled: true },
  },
  {
    id: "sequential-thinking",
    title: "Sequential thinking",
    what: "A structured scratchpad that helps a model work through a hard problem step by step.",
    needs: "Node.js on the hub.",
    server: {
      type: "stdio",
      command: "npx",
      args: ["-y", "@modelcontextprotocol/server-sequential-thinking"],
      enabled: true,
    },
  },
  {
    id: "github",
    title: "GitHub",
    what: "Issues, pull requests, repositories and code search on GitHub.",
    needs: "A GitHub personal access token, kept in an environment variable on the hub.",
    server: {
      type: "http",
      url: "https://api.githubcopilot.com/mcp/",
      headers: { Authorization: "Bearer ${env:GITHUB_PERSONAL_ACCESS_TOKEN}" },
      enabled: true,
    },
    setup:
      "Create a token at github.com (Settings, Developer settings, Personal access tokens) and set it as the environment variable GITHUB_PERSONAL_ACCESS_TOKEN for the account the backend runs as, then restart the backend once so it sees the variable. The token itself never goes in the config.",
  },
];

/**
 * Reads what people paste: a whole VS Code mcp.json ({"servers": {...}}), a Claude/Cursor style file
 * ({"mcpServers": {...}}), or just the servers object ({"name": {...}}).
 */
export function parsePastedServers(text: string): { servers: Record<string, McpServerConfig>; error: string | null } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    return { servers: {}, error: `That is not valid JSON: ${(error as Error).message}` };
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    return { servers: {}, error: "Expected a JSON object." };
  }
  const root = parsed as Record<string, unknown>;
  const map = (root.servers ?? root.mcpServers ?? root) as Record<string, unknown>;
  const servers: Record<string, McpServerConfig> = {};
  for (const [rawName, value] of Object.entries(map)) {
    if (!value || typeof value !== "object") continue;
    const entry = value as Record<string, unknown>;
    const name = rawName.toLowerCase().replace(/[^a-z0-9-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32);
    if (!name) continue;
    const url = typeof entry.url === "string" ? entry.url : null;
    const type = typeof entry.type === "string" && entry.type !== "sse" && entry.type !== "streamableHttp"
      ? (entry.type === "http" ? "http" : "stdio")
      : url
        ? "http"
        : "stdio";
    servers[name] = {
      type,
      command: typeof entry.command === "string" ? entry.command : null,
      args: Array.isArray(entry.args) ? entry.args.filter((a): a is string => typeof a === "string") : null,
      env: isStringMap(entry.env) ? entry.env : null,
      url,
      headers: isStringMap(entry.headers) ? entry.headers : null,
      enabled: entry.disabled === true ? false : true,
    };
  }
  if (Object.keys(servers).length === 0) {
    return { servers, error: "No servers found. Paste an object with a \"servers\" or \"mcpServers\" section." };
  }
  return { servers, error: null };
}

function isStringMap(value: unknown): value is Record<string, string> {
  return !!value && typeof value === "object" && !Array.isArray(value) &&
    Object.values(value as Record<string, unknown>).every((v) => typeof v === "string");
}

// Splits "npx -y @scope/server --flag" into command and arguments, keeping "quoted text" together.
export function splitCommandLine(line: string): { command: string; args: string[] } {
  const parts = (line.match(/"[^"]*"|\S+/g) ?? []).map((part) => part.replace(/^"|"$/g, ""));
  return { command: parts[0] ?? "", args: parts.slice(1) };
}

export function joinCommandLine(server: McpServerConfig): string {
  return [server.command, ...(server.args ?? [])]
    .filter((part): part is string => !!part)
    .map((part) => (/\s/.test(part) ? `"${part}"` : part))
    .join(" ");
}

export function describeServer(server: McpServerConfig): string {
  return server.type === "http" ? (server.url ?? "") : joinCommandLine(server);
}

/** A literal-looking secret typed straight into env or a header, rather than a ${env:NAME} reference. */
export function looksLikeInlineSecret(key: string, value: string): boolean {
  if (!value || value.includes("${env:")) return false;
  return /(token|secret|key|password|authorization)/i.test(key) && value.replace(/^Bearer\s+/i, "").length >= 16;
}
