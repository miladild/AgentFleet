"use client";

import { useSyncExternalStore } from "react";

// The project the user is working on, so they do not have to type its full path into every message. Kept in this
// browser only; the chat sends it to the fleet as context with each message.
const KEY = "agentFleet.projectFolder";
const listeners = new Set<() => void>();

function read(): string {
  try {
    return window.localStorage.getItem(KEY) ?? "";
  } catch {
    return "";
  }
}

let current: string | null = null;

export function setProjectFolder(folder: string) {
  current = folder.trim();
  try {
    if (current) window.localStorage.setItem(KEY, current);
    else window.localStorage.removeItem(KEY);
  } catch {
    // Private windows can refuse storage; the folder then lasts until the page is closed.
  }
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function snapshot(): string {
  if (current === null) current = read();
  return current;
}

export function useProjectFolder(): string {
  return useSyncExternalStore(subscribe, snapshot, () => "");
}
