/** The most a request to the lab routes may carry: the backend takes no larger import. */
export const MAX_LAB_BODY_BYTES = 8 * 1024 * 1024;

/**
 * The request body as text, or null when it is over the limit. Reading stops as soon as the limit is passed, so a
 * huge upload is refused without being held in memory.
 */
export async function readLabBody(request: Request, limit: number): Promise<string | null> {
  if (!request.body) return "";
  const reader = request.body.getReader();
  const decoder = new TextDecoder();
  let total = 0;
  let text = "";
  for (;;) {
    const chunk = await reader.read();
    if (chunk.done) break;
    total += chunk.value.byteLength;
    if (total > limit) {
      await reader.cancel();
      return null;
    }
    text += decoder.decode(chunk.value, { stream: true });
  }
  return text + decoder.decode();
}
