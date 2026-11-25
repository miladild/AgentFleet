/** What GET /api/setup/ollama-versions returns, and the jobs GET /api/setup/ollama-updates returns. */
export type OllamaManualStep = {
  platform: "windows" | "linux" | "macos";
  asset: string;
  url: string;
  sha256: string | null;
  commands: string[];
  note: string;
};

export type OllamaMachine = {
  url: string;
  nodes: string[];
  answers: boolean;
  version: string | null;
  platform: string | null;
  updateMethod: "none" | "local" | "ssh-linux";
  upToDate: boolean;
  canUpdate: boolean;
  blockedReason: string | null;
  manualSteps: OllamaManualStep[];
};

export type OllamaLatest = { version: string; publishedAtUtc: string | null; checkedAtUtc: string };

export type OllamaVersionsReport = {
  machines: OllamaMachine[];
  latest: OllamaLatest | null;
  planRunning: boolean;
  updateRunning: boolean;
};

export type OllamaUpdateJob = {
  id: string;
  url: string;
  nodes: string[];
  state: "queued" | "running" | "done" | "failed";
  status: string;
  fromVersion: string | null;
  targetVersion: string;
  finalVersion: string | null;
  error: string | null;
};

const PLATFORM_NAMES: Record<string, string> = { windows: "Windows", linux: "Linux", macos: "macOS" };

export function platformName(platform: string | null): string {
  return platform ? (PLATFORM_NAMES[platform] ?? platform) : "unknown system";
}

/** "http://192.0.2.10:11434/" shown as "192.0.2.10:11434". */
export function addressLabel(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

function ago(from: string, now: Date): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(from).getTime()) / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} ${minutes === 1 ? "minute" : "minutes"} ago`;
  const hours = Math.round(minutes / 60);
  return `${hours} ${hours === 1 ? "hour" : "hours"} ago`;
}

/** The line above the machines: what the last check found, or that nothing was asked yet. */
export function latestLine(latest: OllamaLatest | null, now: Date): string {
  if (!latest) return "Nothing has been checked yet. Press Check for updates to ask GitHub for the newest stable release.";
  const published = latest.publishedAtUtc
    ? `, published ${new Date(latest.publishedAtUtc).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" })}`
    : "";
  return `Newest stable release: ${latest.version}${published}. Checked ${ago(latest.checkedAtUtc, now)}.`;
}

/** What a machine's row says about its version. */
export function versionLine(machine: OllamaMachine, latest: OllamaLatest | null): string {
  if (!machine.answers) return "Does not answer";
  if (!machine.version) return "Version unknown";
  if (!latest) return `Version ${machine.version}`;
  return machine.upToDate ? `Version ${machine.version} (up to date)` : `Version ${machine.version}`;
}

/** The machines the Update all button would update: the backend's own judgement, so the button and the call agree. */
export function updatableMachines(report: OllamaVersionsReport): OllamaMachine[] {
  return report.machines.filter((machine) => machine.canUpdate);
}

/** Why Update all is off, or null when it is on. */
export function updateAllBlockedReason(report: OllamaVersionsReport): string | null {
  if (!report.latest) return "Check for updates first.";
  if (report.planRunning) return "A plan is running or approved.";
  if (report.updateRunning) return "An update is already running.";
  if (updatableMachines(report).length === 0) return "No machine needs an update that can be run from here.";
  return null;
}

/** The question the confirmation dialog asks. */
export function confirmQuestion(machines: OllamaMachine[], target: string): string {
  const where = machines.map((machine) => `${machine.nodes.join(", ")} (${addressLabel(machine.url)}, now ${machine.version ?? "unknown"})`).join("\n");
  return (
    `Update Ollama to ${target} on:\n${where}\n\n` +
    "This stops and restarts Ollama on each of these machines, one at a time, and interrupts any model use there. " +
    "Nothing else is changed. Continue?"
  );
}

/** What a job's row says: progress while it runs, the new version when it is done, the reason when it failed. */
export function jobLine(job: OllamaUpdateJob): string {
  switch (job.state) {
    case "queued":
      return "Waiting for its turn.";
    case "running":
      return `${job.status.charAt(0).toUpperCase()}${job.status.slice(1)}...`;
    case "done":
      return `Updated: now ${job.finalVersion ?? job.targetVersion}.`;
    default:
      return job.error ?? "The update failed.";
  }
}

/** The most recent job for a machine, if any. */
export function jobFor(jobs: OllamaUpdateJob[], url: string): OllamaUpdateJob | undefined {
  return [...jobs].reverse().find((job) => job.url === url);
}

export function anyJobActive(jobs: OllamaUpdateJob[]): boolean {
  return jobs.some((job) => job.state === "queued" || job.state === "running");
}
