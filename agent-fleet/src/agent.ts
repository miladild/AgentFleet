import { HttpAgent } from "@ag-ui/client";

/** Points at the .NET AG-UI backend (agent/Program.cs). */
export function createDefaultAgent(): HttpAgent {
  return new HttpAgent({
    url: process.env.AGENT_URL || "http://localhost:8000/",
  });
}
