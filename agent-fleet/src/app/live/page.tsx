import { LiveFollow } from "@/components/live/LiveFollow";

/** /live follows whatever the fleet is doing; /live?context=<id> one conversation; /live?pick lists the plans. */
export default function LiveIndexPage() {
  return <LiveFollow />;
}
