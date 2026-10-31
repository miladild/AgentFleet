/** How many dismissed notice keys the browser remembers. */
export const SEEN_NOTIFICATIONS_LIMIT = 100;

/** The current notices plus the incoming ones that are not in it yet; the same list back when nothing is new. */
export function mergeNotifications<T extends { notificationKey: string }>(current: T[], incoming: T[]): T[] {
  const known = new Set(current.map((notice) => notice.notificationKey));
  const added: T[] = [];
  for (const notice of incoming) {
    if (known.has(notice.notificationKey)) continue;
    known.add(notice.notificationKey);
    added.push(notice);
  }
  return added.length === 0 ? current : [...current, ...added];
}

/** The remembered keys after dismissing these ones: no repeats, newest last, at most SEEN_NOTIFICATIONS_LIMIT kept. */
export function rememberDismissed(seen: string[], dismissed: string[]): string[] {
  const all = [...seen];
  for (const key of dismissed) {
    if (!all.includes(key)) all.push(key);
  }
  return all.slice(-SEEN_NOTIFICATIONS_LIMIT);
}

/** The text on the notification icon: the count, "9+" above nine, nothing at zero. */
export function notificationBadge(count: number): string | null {
  if (count <= 0) return null;
  return count > 9 ? "9+" : String(count);
}
