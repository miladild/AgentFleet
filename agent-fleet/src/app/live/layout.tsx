import type { Metadata } from "next";
import { JetBrains_Mono } from "next/font/google";
import "../globals.css";
import "@/components/live/live.css";

// Omarchy's own font. next/font serves it from this server, so the page makes no request to the internet.
const mono = JetBrains_Mono({ subsets: ["latin"], variable: "--live-font", display: "swap" });

export const metadata: Metadata = {
  title: "Fleet live",
  description: "A plan as it runs across your machines.",
};

// Its own root layout: the live view only reads the fleet's API, so it loads without the chat's framework.
export default function LiveLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`dark ${mono.variable}`}>
      <body className="antialiased" suppressHydrationWarning>
        {children}
      </body>
    </html>
  );
}
