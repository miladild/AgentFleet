"use client";

import { useEffect, useState } from "react";
import { setProjectFolder, useProjectFolder } from "./projectFolder";

/**
 * "Working on: C:\projects\shop". Every message then carries that folder as context, so "run the tests" or "where
 * is login handled?" work without typing the path. The assistant can also set it when you name a project in chat.
 */
export function ProjectFolderBar() {
  const folder = useProjectFolder();
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState("");
  const [note, setNote] = useState<{ text: string; bad: boolean } | null>(null);

  // Say what the folder looks like, from the project overview tool (the path is on the hub, not in this browser).
  useEffect(() => {
    if (!folder) {
      setNote(null);
      return;
    }
    let cancelled = false;
    fetch("/api/tools/run", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: "project_overview", arguments: { path: folder, depth: 1 } }),
    })
      .then((res) => (res.ok ? res.json() : null))
      .then((data: { ok: boolean; output: string } | null) => {
        if (cancelled || !data) return;
        if (!data.ok) return setNote({ text: data.output.replace(/^Error:\s*/, ""), bad: true });
        const hints = data.output.split("How to build and test, from the project files:")[1]?.trim().split("\n") ?? [];
        const first = hints.find((line) => line.startsWith("- ") && !line.includes("nothing recognised"));
        setNote({ text: first ? first.slice(2) : "Found on the hub.", bad: false });
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [folder]);

  function save() {
    setProjectFolder(value);
    setEditing(false);
  }

  if (editing || !folder) {
    return (
      <div className="space-y-1">
        <h2 className="text-xs uppercase tracking-wide text-neutral-500">Project folder</h2>
        <div className="flex gap-2">
          <input
            value={editing ? value : ""}
            onFocus={() => {
              if (!editing) {
                setValue(folder);
                setEditing(true);
              }
            }}
            onChange={(e) => {
              setValue(e.target.value);
              setEditing(true);
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter") save();
              if (e.key === "Escape") setEditing(false);
            }}
            placeholder="C:\projects\my-app  (optional: then you can just say 'run the tests')"
            className="flex-1 text-sm px-3 py-1.5 rounded-md bg-neutral-900 border border-neutral-800 text-neutral-200 font-mono placeholder:font-sans placeholder:text-neutral-600"
          />
          {editing && (
            <button type="button" onClick={save} className="text-xs px-3 rounded-md bg-neutral-800 border border-neutral-700 text-neutral-200 hover:bg-neutral-700">
              {value.trim() ? "Use it" : "Clear"}
            </button>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-1">
      <h2 className="text-xs uppercase tracking-wide text-neutral-500">Project folder</h2>
      <div className="flex items-center gap-2 bg-neutral-900 border border-neutral-800 rounded-md px-3 py-2 text-sm">
        <span className="font-mono text-neutral-200 truncate flex-1" title={folder}>
          {folder}
        </span>
        <button
          type="button"
          onClick={() => {
            setValue(folder);
            setEditing(true);
          }}
          className="text-xs text-sky-400 hover:underline"
        >
          Change
        </button>
        <button type="button" onClick={() => setProjectFolder("")} className="text-xs text-neutral-500 hover:text-neutral-300" title="Forget the folder">
          ×
        </button>
      </div>
      {note && <p className={`text-[11px] ${note.bad ? "text-amber-300" : "text-neutral-500"} truncate`} title={note.text}>{note.text}</p>}
    </div>
  );
}
