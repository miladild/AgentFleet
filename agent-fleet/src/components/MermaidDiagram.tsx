"use client";

import { useEffect, useRef, useState } from "react";

/** Renders Mermaid source as an SVG in the browser. mermaid is loaded on demand so it adds
 * nothing to the page until a diagram is actually shown. A diagram that does not parse
 * shows its source and the parser's message instead of a broken picture. */
export function MermaidDiagram({ code }: { code: string }) {
  const container = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);

    (async () => {
      try {
        const mermaid = (await import("mermaid")).default;
        mermaid.initialize({ startOnLoad: false, theme: "dark", securityLevel: "strict" });
        // parse() throws without touching the page; render() on bad input leaves an error
        // graphic behind in the document.
        await mermaid.parse(code);
        const { svg } = await mermaid.render(`mmd-${Math.random().toString(36).slice(2)}`, code);
        if (!cancelled && container.current) container.current.innerHTML = svg;
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [code]);

  if (error) {
    return (
      <div className="rounded-md border border-red-900/60 bg-red-950/20 p-2 text-xs">
        <div className="text-red-400 mb-1">This diagram could not be drawn: {error.split("\n")[0]}</div>
        <pre className="text-neutral-400 whitespace-pre-wrap break-words max-h-40 overflow-y-auto">{code}</pre>
      </div>
    );
  }

  return <div ref={container} className="overflow-x-auto [&_svg]:max-w-full [&_svg]:h-auto" />;
}
