import { readFile } from "node:fs/promises";
import path from "node:path";

export async function GET() {
  const filePath = path.join(process.cwd(), "src", "content", "usage-guide.md");
  const content = await readFile(filePath, "utf-8");
  return new Response(content, {
    headers: { "content-type": "text/markdown; charset=utf-8" },
  });
}
