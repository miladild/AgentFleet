"use client";

import { useCallback, useEffect, useState } from "react";
import { CopyCommand, announceFleetChanged } from "./setupApi";

type SandboxConfig = {
  mode: string | null;
  host: string | null;
  port: number | null;
  user: string | null;
  keyPath: string | null;
  sudo: boolean | null;
  timeoutSeconds: number | null;
  hostKey: string | null;
};

type PrepareStatus = { state: "running" | "done" | "failed"; step: string; output: string | null; error: string | null } | null;

type SandboxPayload = {
  saved: SandboxConfig;
  active: { mode: "off" | "local" | "ssh"; summary: string; host: string; hostKey: string | null };
  key: { path: string; exists: boolean; publicKey: string | null; problem: string | null };
  defaultKeyPath: string;
  defaultUser: string;
  dockerOnThisMachine: boolean;
  prepare: PrepareStatus;
  message: string | null;
};

type TestStep = { name: string; ok: boolean; detail: string; hint: string | null };
type TestResult = { ok: boolean; steps: TestStep[]; hostKey: string | null; missingImages: string[] };

type Form = { mode: "auto" | "off" | "local" | "ssh"; host: string; port: string; user: string; keyPath: string; sudo: boolean; timeoutSeconds: string };

const MODES: { value: Form["mode"]; label: string; hint: string }[] = [
  { value: "auto", label: "Automatic", hint: "Docker on this computer if it has it, otherwise off." },
  { value: "local", label: "This computer", hint: "Docker Desktop (Windows, macOS) or Docker Engine (Linux) here." },
  { value: "ssh", label: "Another machine, over SSH", hint: "A Linux machine with Docker. The fleet signs in with its own key." },
  { value: "off", label: "Off", hint: "No sandbox tool. Everything else works." },
];

function toForm(payload: SandboxPayload): Form {
  const saved = payload.saved;
  const mode = (saved.mode ?? "auto") as Form["mode"];
  return {
    mode: MODES.some((m) => m.value === mode) ? mode : "auto",
    host: saved.host ?? "",
    port: String(saved.port ?? 22),
    user: saved.user ?? payload.defaultUser,
    keyPath: saved.keyPath ?? payload.defaultKeyPath,
    sudo: saved.sudo ?? false,
    timeoutSeconds: String(saved.timeoutSeconds ?? 20),
  };
}

function toConfig(form: Form, hostKey: string | null): SandboxConfig {
  const ssh = form.mode === "ssh";
  return {
    mode: form.mode,
    host: form.host.trim() || null,
    port: ssh ? Number(form.port) || 22 : null,
    user: form.user.trim() || null,
    keyPath: form.keyPath.trim() || null,
    sudo: ssh ? form.sudo : null,
    timeoutSeconds: Number(form.timeoutSeconds) || null,
    hostKey,
  };
}

const input = "text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono";
const button = "text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-50";

/** The Sandbox tab: where run_sandboxed_code runs, with help for the SSH key and a real test. Applies on Save. */
export function SandboxSettings() {
  const [payload, setPayload] = useState<SandboxPayload | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [test, setTest] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [password, setPassword] = useState("");

  const load = useCallback(async (resetForm: boolean, keyPath?: string) => {
    try {
      const res = await fetch(`/api/setup/sandbox${keyPath ? `?keyPath=${encodeURIComponent(keyPath)}` : ""}`, { cache: "no-store" });
      const data: SandboxPayload = await res.json();
      setPayload(data);
      if (resetForm) setForm(toForm(data));
    } catch {
      setMessage({ text: "Could not reach the backend.", error: true });
    }
  }, []);

  useEffect(() => {
    void load(true);
  }, [load]);

  // Follow the image download while it runs.
  useEffect(() => {
    if (payload?.prepare?.state !== "running") return;
    const timer = setTimeout(() => void load(false, form?.keyPath.trim()), 2000);
    return () => clearTimeout(timer);
  }, [payload, load, form?.keyPath]);

  // Describe the key at the path being typed (does it exist, what is its public half).
  const typedKeyPath = form?.keyPath.trim();
  useEffect(() => {
    if (!typedKeyPath || payload?.key.path === typedKeyPath) return;
    const timer = setTimeout(() => void load(false, typedKeyPath), 400);
    return () => clearTimeout(timer);
  }, [typedKeyPath, payload?.key.path, load]);

  if (!payload || !form) return <p className="text-sm text-neutral-500">{message?.text ?? "Loading..."}</p>;

  const saved = payload.saved;
  const sameMachine = (form.host.trim() || null) === (saved.host ?? null) && (Number(form.port) || 22) === (saved.port ?? 22);
  // The identity to remember: what the last test saw, or what is already remembered for this same machine.
  const hostKey = test?.hostKey ?? (sameMachine ? saved.hostKey : null);
  const ssh = form.mode === "ssh";
  const dirty = JSON.stringify(toConfig(form, hostKey)) !== JSON.stringify(toConfig(toForm(payload), saved.hostKey));

  function change(patch: Partial<Form>) {
    setForm({ ...form!, ...patch });
    if (patch.host !== undefined || patch.port !== undefined || patch.mode !== undefined || patch.user !== undefined) setTest(null);
    setMessage(null);
  }

  async function call(label: string, url: string, method: string, body: unknown) {
    setBusy(label);
    setMessage(null);
    try {
      const res = await fetch(url, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setMessage({ text: data.error ?? `HTTP ${res.status}`, error: true });
        return null;
      }
      return data;
    } catch {
      setMessage({ text: "Could not reach the backend.", error: true });
      return null;
    } finally {
      setBusy(null);
    }
  }

  async function runTest() {
    const result: TestResult | null = await call("test", "/api/setup/sandbox/test", "POST", toConfig(form!, hostKey));
    if (result) setTest(result);
  }

  async function save() {
    const data: SandboxPayload | null = await call("save", "/api/setup/sandbox", "PUT", toConfig(form!, hostKey));
    if (data) {
      setPayload(data);
      setForm(toForm(data));
      setMessage({ text: data.message ?? "Saved.", error: false });
      announceFleetChanged();
    }
  }

  async function createKey() {
    const key = await call("key", "/api/setup/sandbox/key", "POST", { path: form!.keyPath });
    if (key) await load(false, form!.keyPath.trim());
  }

  async function installKey() {
    const result = await call("install", "/api/setup/sandbox/install-key", "POST", {
      host: form!.host,
      port: Number(form!.port) || 22,
      user: form!.user,
      password,
      keyPath: form!.keyPath,
      hostKey: sameMachine ? saved.hostKey : null,
    });
    setPassword("");
    if (result) {
      if (result.ok) await runTest();
      setMessage({ text: result.message, error: !result.ok });
    }
  }

  async function prepare() {
    const status = await call("prepare", "/api/setup/sandbox/prepare", "POST", toConfig(form!, hostKey));
    if (status) await load(false, form!.keyPath.trim());
  }

  const key = payload.key.path === form.keyPath.trim() ? payload.key : null;
  const prep = payload.prepare;

  return (
    <div className="space-y-4">
      <div className="text-xs text-neutral-400 space-y-1">
        <p>
          <code>run_sandboxed_code</code> lets the model run a Python, JavaScript or shell snippet and see its output. Each run gets a
          fresh Docker container with no access to your files, no root, 512 MB of memory and a time limit. It can reach the internet, so
          it can install packages.
        </p>
        <p className="text-neutral-500">In use now: {payload.active.summary}.</p>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
        {MODES.map((mode) => (
          <label
            key={mode.value}
            className={`flex gap-2 items-start rounded-md border p-2 cursor-pointer ${
              form.mode === mode.value ? "border-sky-500/60 bg-sky-500/10" : "border-neutral-700 hover:border-neutral-600"
            }`}
          >
            <input type="radio" className="mt-0.5" checked={form.mode === mode.value} onChange={() => change({ mode: mode.value })} />
            <span>
              <span className="block text-xs text-neutral-100">{mode.label}</span>
              <span className="block text-[11px] text-neutral-500">{mode.hint}</span>
            </span>
          </label>
        ))}
      </div>

      {(form.mode === "local" || form.mode === "auto") && !payload.dockerOnThisMachine && (
        <p className="text-xs text-amber-200 bg-amber-500/10 border border-amber-500/30 rounded p-2">
          Docker is not installed on this computer, so {form.mode === "auto" ? "Automatic means off" : "this will not work yet"}. Install
          Docker Desktop from docker.com (Windows, macOS) or Docker Engine (Linux), start it, then press Test. Or use another machine over SSH.
        </p>
      )}

      {ssh && (
        <div className="space-y-3 border border-neutral-700 rounded-md p-3">
          <div className="flex flex-wrap gap-3">
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Machine</span>
              <input value={form.host} onChange={(e) => change({ host: e.target.value })} placeholder="192.168.1.30" className={`${input} w-44`} />
            </label>
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Port</span>
              <input value={form.port} onChange={(e) => change({ port: e.target.value })} className={`${input} w-16`} />
            </label>
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Account</span>
              <input value={form.user} onChange={(e) => change({ user: e.target.value })} placeholder="builder" className={`${input} w-32`} />
            </label>
            <label className="flex items-center gap-1.5 text-xs text-neutral-300 self-end pb-1" title="Only if the account may run sudo docker without a password">
              <input type="checkbox" checked={form.sudo} onChange={(e) => change({ sudo: e.target.checked })} />
              use sudo
            </label>
          </div>

          <div className="space-y-2">
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">The fleet&apos;s key</span>
              <input value={form.keyPath} onChange={(e) => change({ keyPath: e.target.value })} className={`${input} w-full`} />
            </label>
            {key && !key.exists && (
              <div className="flex items-center gap-2">
                <button type="button" onClick={createKey} disabled={busy !== null} className={button}>
                  {busy === "key" ? "Creating..." : "Create a key for the fleet"}
                </button>
                <span className="text-[11px] text-neutral-500">A new key pair only the fleet uses. The private half never leaves this computer.</span>
              </div>
            )}
            {key?.problem && <p className="text-[11px] text-amber-300">{key.problem}</p>}
            {!key && <p className="text-[11px] text-neutral-500">Looking at that path...</p>}
            {key?.publicKey && (
              <div className="space-y-2">
                <p className="text-[11px] text-neutral-400">
                  The machine must trust this key once. Easiest: type that account&apos;s password here and the fleet adds the key for you
                  (Linux and macOS machines; the password is used for this one sign-in and is not saved).
                </p>
                <div className="flex flex-wrap gap-2 items-center">
                  <input
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    placeholder={`password for ${form.user || "the account"}`}
                    autoComplete="off"
                    className={`${input} w-56`}
                  />
                  <button type="button" onClick={installKey} disabled={busy !== null || !password || !form.host.trim()} className={button}>
                    {busy === "install" ? "Adding the key..." : "Add the key to that machine"}
                  </button>
                </div>
                <details className="text-[11px] text-neutral-500">
                  <summary className="cursor-pointer text-neutral-400">Or do it by hand</summary>
                  <div className="mt-1 space-y-1">
                    <p>On the sandbox machine, signed in as {form.user || "that account"}, run:</p>
                    <CopyCommand
                      command={`mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '${key.publicKey}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys`}
                    />
                    <p>The public key alone:</p>
                    <CopyCommand command={key.publicKey} />
                  </div>
                </details>
              </div>
            )}
          </div>

          <details className="text-[11px] text-neutral-500">
            <summary className="cursor-pointer text-neutral-400">What the sandbox machine needs</summary>
            <ul className="mt-1 list-disc pl-4 space-y-0.5">
              <li>An SSH server (Ubuntu: sudo apt install openssh-server).</li>
              <li>Docker Engine: curl -fsSL https://get.docker.com | sh</li>
              <li>The account in the docker group, so it needs no password for Docker: sudo usermod -aG docker {form.user || "ACCOUNT"}, then sign out and in.</li>
              <li>Its firewall letting this computer in on port {form.port || 22}.</li>
            </ul>
          </details>
        </div>
      )}

      {form.mode !== "off" && (
        <label className="flex items-center gap-2 text-xs text-neutral-400">
          Time limit per run
          <input value={form.timeoutSeconds} onChange={(e) => change({ timeoutSeconds: e.target.value })} className={`${input} w-14`} />
          seconds (1 to 120)
        </label>
      )}

      <div className="flex flex-wrap items-center gap-2">
        {form.mode !== "off" && (
          <button type="button" onClick={runTest} disabled={busy !== null || (ssh && !form.host.trim())} className={button}>
            {busy === "test" ? "Testing..." : "Test"}
          </button>
        )}
        <button
          type="button"
          onClick={save}
          disabled={busy !== null || !dirty}
          className="text-xs px-3 py-1.5 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-40"
        >
          {busy === "save" ? "Saving..." : dirty ? "Save" : "Saved"}
        </button>
        {message && <span className={`text-xs ${message.error ? "text-red-300" : "text-neutral-400"}`}>{message.text}</span>}
      </div>

      {test && (
        <div className={`text-xs rounded p-2 border space-y-1.5 ${test.ok ? "bg-emerald-500/10 border-emerald-500/30" : "bg-red-500/10 border-red-500/30"}`}>
          {test.steps.map((step) => (
            <div key={step.name}>
              <p className={step.ok ? "text-emerald-300" : "text-red-300"}>
                {step.ok ? "✓" : "✕"} {step.name}: <span className="text-neutral-300">{step.detail}</span>
              </p>
              {step.hint && <p className="text-neutral-400 pl-4">{step.hint}</p>}
            </div>
          ))}
          {test.hostKey && (
            <p className="text-neutral-400">
              That machine&apos;s identity: <span className="font-mono text-neutral-300">{test.hostKey}</span>.{" "}
              {test.hostKey === saved.hostKey && sameMachine ? "Already remembered." : "Save to remember it: the fleet will then refuse any other machine at that address."}
            </p>
          )}
          {test.ok && test.missingImages.length > 0 && prep?.state !== "running" && (
            <div className="flex flex-wrap items-center gap-2 pt-1">
              <span className="text-neutral-300">Not downloaded yet: {test.missingImages.join(", ")}. The first run would spend its time limit downloading.</span>
              <button type="button" onClick={prepare} disabled={busy !== null} className={button}>
                Download them now (about 250 MB)
              </button>
            </div>
          )}
          {test.ok && test.missingImages.length === 0 && <p className="text-emerald-300">The container images are downloaded. The sandbox is ready.</p>}
        </div>
      )}

      {prep && (
        <p className={`text-xs ${prep.state === "failed" ? "text-red-300" : prep.state === "done" ? "text-emerald-300" : "text-neutral-400"}`}>
          {prep.state === "running"
            ? `Preparing the sandbox: ${prep.step}...`
            : prep.state === "done"
              ? `Sandbox ready. A test run printed: ${prep.output}`
              : `Preparing the sandbox failed while ${prep.step}: ${prep.error}`}
        </p>
      )}
    </div>
  );
}
