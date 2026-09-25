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
  category: "Docs and search" | "Browser" | "Code and files" | "Thinking and memory" | "Cloud";
  what: string;
  needs: string;
  /** A program that must be on the hub for it to start. */
  requires?: "npx" | "uvx";
  server: McpServerConfig;
  /** Shown in the form after picking it: what to fill in or set up first. */
  setup?: string;
};

// Servers that are useful for coding and known to work with this fleet. {name} in an argument is filled in in the form
// (for example the folder a server may use). Anything else can be added with the Custom form, by pasting an mcp.json
// entry, or by importing it from another app.
export const MCP_CATALOG: CatalogEntry[] = [
  {
    id: "microsoft-learn",
    title: "Microsoft Learn",
    category: "Docs and search",
    what: "Search and read official Microsoft and Azure documentation and code samples.",
    needs: "Nothing. No account.",
    server: { type: "http", url: "https://learn.microsoft.com/api/mcp", enabled: true },
  },
  {
    id: "context7",
    title: "Context7",
    category: "Docs and search",
    what: "Up-to-date documentation for thousands of libraries and frameworks (React, Next.js, EF Core, ...).",
    needs: "Nothing to start. A free API key raises the limits.",
    server: { type: "http", url: "https://mcp.context7.com/mcp", enabled: true },
    setup:
      "Optional: with an API key, add a header CONTEXT7_API_KEY = ${env:CONTEXT7_API_KEY} and set that environment variable on the hub.",
  },
  {
    id: "deepwiki",
    title: "DeepWiki",
    category: "Docs and search",
    what: "Ask questions about any public GitHub repository: how it is built, where things are, how to use it.",
    needs: "Nothing. No account.",
    server: { type: "http", url: "https://mcp.deepwiki.com/mcp", enabled: true },
  },
  {
    id: "hugging-face",
    title: "Hugging Face",
    category: "Docs and search",
    what: "Search models, datasets, Spaces and papers on Hugging Face.",
    needs: "Nothing to start. A token raises the limits.",
    server: { type: "http", url: "https://huggingface.co/mcp", enabled: true },
    setup: "Optional: add a header Authorization = Bearer ${env:HF_TOKEN} and set HF_TOKEN on the hub.",
  },
  {
    id: "playwright",
    title: "Playwright browser",
    category: "Browser",
    what: "Drive a real browser: open pages, click, fill forms, read the page, take screenshots.",
    needs: "Node.js on the hub. Downloads a browser the first time.",
    requires: "npx",
    server: { type: "stdio", command: "npx", args: ["-y", "@playwright/mcp@latest"], enabled: true },
  },
  {
    id: "chrome-devtools",
    title: "Chrome DevTools",
    category: "Browser",
    what: "Inspect a live Chrome: console messages, network requests, performance traces and screenshots. Good for debugging a web app.",
    needs: "Node.js and Google Chrome on the hub.",
    requires: "npx",
    server: { type: "stdio", command: "npx", args: ["-y", "chrome-devtools-mcp@latest"], enabled: true },
  },
  {
    id: "github",
    title: "GitHub",
    category: "Code and files",
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
  {
    id: "git",
    title: "Git (one repository)",
    category: "Code and files",
    what: "Structured git tools for one repository: status, diff, log, branches, commit.",
    needs: "uv on the hub.",
    requires: "uvx",
    server: { type: "stdio", command: "uvx", args: ["mcp-server-git", "--repository", "{repository}"], enabled: true },
  },
  {
    id: "filesystem",
    title: "Files in one folder",
    category: "Code and files",
    what: "Read and write files, but only inside the folders you name. Pair it with the built-in file tools switched off for a fleet that cannot touch anything else.",
    needs: "Node.js on the hub.",
    requires: "npx",
    server: { type: "stdio", command: "npx", args: ["-y", "@modelcontextprotocol/server-filesystem", "{folder}"], enabled: true },
  },
  {
    id: "sequential-thinking",
    title: "Sequential thinking",
    category: "Thinking and memory",
    what: "A structured scratchpad that helps a model work through a hard problem step by step.",
    needs: "Node.js on the hub.",
    requires: "npx",
    server: {
      type: "stdio",
      command: "npx",
      args: ["-y", "@modelcontextprotocol/server-sequential-thinking"],
      enabled: true,
    },
  },
  {
    id: "memory",
    title: "Memory",
    category: "Thinking and memory",
    what: "A small knowledge graph the model can store facts in and look up in later chats.",
    needs: "Node.js on the hub.",
    requires: "npx",
    server: { type: "stdio", command: "npx", args: ["-y", "@modelcontextprotocol/server-memory"], enabled: true },
  },
  {
    id: "time",
    title: "Time",
    category: "Thinking and memory",
    what: "The current date and time, and conversions between time zones. Local models do not know today's date.",
    needs: "uv on the hub.",
    requires: "uvx",
    server: { type: "stdio", command: "uvx", args: ["mcp-server-time"], enabled: true },
  },
  {
    id: "azure",
    title: "Azure",
    category: "Cloud",
    what: "Work with your Azure resources: storage, databases, app services, logs and more.",
    needs: "Node.js on the hub, and the Azure CLI signed in (az login) as the backend's account.",
    requires: "npx",
    server: { type: "stdio", command: "npx", args: ["-y", "@azure/mcp@latest", "server", "start"], enabled: true },
  },
];

export const CATALOG_CATEGORIES = ["Docs and search", "Browser", "Code and files", "Thinking and memory", "Cloud"] as const;

/** The {name} placeholders in a server's command, arguments or address that still need filling in. */
export function catalogPlaceholders(text: string): string[] {
  return [...new Set([...text.matchAll(/\{([a-zA-Z_][a-zA-Z0-9_]*)\}/g)].map((match) => match[1]))];
}

export function fillPlaceholders(text: string, values: Record<string, string>): string {
  return text.replace(/\{([a-zA-Z_][a-zA-Z0-9_]*)\}/g, (whole, key: string) => values[key]?.trim() || whole);
}

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
