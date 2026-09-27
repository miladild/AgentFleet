import type { NextConfig } from "next";

// FLEET_STANDALONE=1 (set by scripts/Build-Release.ps1) builds the self-contained server that release
// bundles ship: .next/standalone/server.js plus only the node_modules it needs. Normal builds are unchanged.
const standalone = process.env.FLEET_STANDALONE === "1";

const nextConfig: NextConfig = {
  // Dev defaults to .next-dev so it cannot replace assets from the production build.
  distDir: process.env.NEXT_DIST_DIR || ".next",
  typescript: { ignoreBuildErrors: false },
  serverExternalPackages: ["@copilotkit/runtime"],
  ...(standalone
    ? {
        output: "standalone" as const,
        // Read from disk at run time, so the tracer cannot see them on its own.
        outputFileTracingIncludes: { "/api/usage-guide": ["./src/content/usage-guide.md"] },
      }
    : {}),
};

export default nextConfig;
