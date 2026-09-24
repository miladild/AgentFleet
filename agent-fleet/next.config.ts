import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Lets a dev server run next to the production service without both writing into
  // the same .next folder: set NEXT_DIST_DIR=.next-dev for the dev one.
  distDir: process.env.NEXT_DIST_DIR || ".next",
  typescript: { ignoreBuildErrors: false },
  serverExternalPackages: ["@copilotkit/runtime"],
};

export default nextConfig;
