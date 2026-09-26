import { LiveRun } from "@/components/live/LiveRun";

export default async function LivePlanPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <LiveRun planId={id} />;
}
