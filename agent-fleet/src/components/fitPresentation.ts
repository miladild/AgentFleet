export type FitSizeResult = {
  context: number;
  status: string;
  gpuShare: number | null;
  tokensPerSecond: number | null;
  loadSeconds: number | null;
  detail: string | null;
  completedAtUtc: string;
  reused?: boolean;
};

export type FitRecommendation = {
  context: number | null;
  doesNotFullyFit: boolean;
  reason: string;
};

export type FitLatestResult = {
  node: string;
  model: string;
  results: FitSizeResult[];
  recommendation: FitRecommendation;
  measuredAtUtc: string;
  stale: boolean;
  staleReason: string | null;
};

export type SetupFitSummary = {
  line: string;
  replacesStaticReason: boolean;
};

export function formatFitSize(context: number): string {
  return `${Math.round(context / 1024)}K`;
}

function measuredDate(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "date unavailable"
    : new Intl.DateTimeFormat("en-NZ", { day: "numeric", month: "short", timeZone: "UTC" }).format(date);
}

// A size counts as sitting on the card only when this size's own share is high; the recommendation's flag looks at
// the smallest size, so a larger size can spill even when that flag is false.
export const FULL_CARD_SHARE = 0.9;

function sizeIsFullyOnCard(point: FitSizeResult | undefined): boolean {
  return point?.gpuShare == null || point.gpuShare >= FULL_CARD_SHARE;
}

export function formatFitLine(result: FitLatestResult | null): string {
  if (!result) return "Fit not measured yet.";

  const { recommendation } = result;
  if (recommendation.context === null) {
    if (/processor/i.test(recommendation.reason)) return "Fit runs on the processor, no graphics-card fit to suggest.";
    if (result.results.length < 2) return "Fit too few measurements to suggest a size.";
    return `Fit no size to suggest. ${recommendation.reason}`;
  }

  const size = formatFitSize(recommendation.context);
  const point = result.results.find((item) => item.context === recommendation.context);
  const fit = recommendation.doesNotFullyFit
    ? "does not fully fit on the graphics card"
    : sizeIsFullyOnCard(point)
      ? "stays on the graphics card"
      : "runs partly on the processor";
  const share = point?.gpuShare === null || point?.gpuShare === undefined
    ? ""
    : ` (${Math.round(point.gpuShare * 100)}% card share)`;
  const speed = point?.tokensPerSecond == null ? "" : `, about ${Math.round(point.tokensPerSecond)} tokens/s`;
  return `Fit ${size} ${fit}${share}${speed} (measured ${measuredDate(result.measuredAtUtc)}).`;
}

export function fitStaleReason(result: FitLatestResult | null): string | null {
  if (!result?.stale) return null;
  const reason = result.staleReason?.toLowerCase() ?? "";
  if (reason.includes("server") || reason.includes("version")) return "The server was updated since this was measured.";
  if (reason.includes("model")) return "The model changed since this was measured.";
  return "This measurement is out of date.";
}

export function fitResultStatus(result: FitSizeResult | undefined, current: boolean): string {
  if (result?.status === "ok") {
    const share = result.gpuShare == null ? "" : `, ${Math.round(result.gpuShare * 100)}% card share`;
    const speed = result.tokensPerSecond == null ? "" : `, ${Math.round(result.tokensPerSecond)} tokens/s`;
    return `✓ Measured${share}${speed}`;
  }
  if (result?.status === "doesNotFit") return `× Did not fit${result.detail ? `: ${result.detail}` : ""}`;
  if (result?.status === "error") return `! Could not measure${result.detail ? `: ${result.detail}` : ""}`;
  if (current) return "◉ Measuring";
  return "· Not measured yet";
}

export function formatSetupFitSummary(model: string, result: FitLatestResult | null): SetupFitSummary | null {
  if (!result || taggedModel(result.model) !== taggedModel(model)) return null;
  if (result.stale) {
    return {
      line: "Measured on this computer: the last result is out of date; measure again.",
      replacesStaticReason: true,
    };
  }

  const context = result.recommendation.context;
  if (context === null) {
    return {
      line: "Measured on this computer: no graphics-card size could be recommended.",
      replacesStaticReason: true,
    };
  }

  const size = formatFitSize(context);
  if (result.recommendation.doesNotFullyFit) {
    return {
      line: `Measured on this computer: ${size} does not fully fit on the graphics card.`,
      replacesStaticReason: true,
    };
  }

  const point = result.results.find((item) => item.context === context);
  if (!sizeIsFullyOnCard(point)) {
    return {
      line: `Measured on this computer: ${size} runs partly on the processor.`,
      replacesStaticReason: true,
    };
  }

  return {
    line: `Measured on this computer: ${size} stays on the graphics card.`,
    replacesStaticReason: false,
  };
}

function taggedModel(model: string): string {
  return model.includes(":") ? model : `${model}:latest`;
}
