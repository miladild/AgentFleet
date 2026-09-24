# Contributing

Thanks for looking. This is a small project with one main author, so the process is light.

## Set up a development copy

```powershell
.\scripts\Setup-Hub.ps1
cd agent-fleet
npm run dev            # backend on :8000, web UI on :3000
```

For Linux and macOS, install the .NET 9 SDK, Node.js 20+ with npm, and Ollama first, then run
`bash scripts/setup-hub.sh` from the repository root and start it with `cd agent-fleet && npm run dev`.

`npm run dev:agent` and `npm run dev:ui` start them separately. In a development run the config is
`agent-fleet\fleet.config.json` and conversations and plans are in `agent-fleet\data\`.

## Tests

```powershell
cd agent-fleet\agent.Tests
dotnet test
```

Keep them green. New backend behavior should come with a test; the existing ones show the style (small, one behavior each,
named for what should happen). The web UI is checked by hand in a browser, so say what you clicked in your pull request.

Type-check the web UI with `npm run typecheck` in `agent-fleet`. In `vscode-fleet`, `npm test` compiles the extension and
runs the tests of the parts that do not need VS Code (`test/`); the rest is checked by hand in VS Code.

## Running a second copy beside a real install

If you already run the installed fleet, test your changes on other ports so you do not disturb it:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:8010'
$env:FLEET_CONFIG_PATH = "$PWD\scratch-config.json"
dotnet run --project agent-fleet\agent

$env:NEXT_DIST_DIR = '.next-dev'; $env:AGENT_URL = 'http://localhost:8010'; $env:PORT = '3005'
node agent-fleet\scripts\serve.mjs dev
```

`NEXT_DIST_DIR` keeps the dev server's build output apart from the installed web UI's. Next.js rewrites
`agent-fleet/tsconfig.json` and `agent-fleet/next-env.d.ts` to mention that folder when you build or run with it: put those two
files back (`git checkout` them) before committing, or `npm run typecheck` will look for a folder that no longer exists.

## Guidelines

- **Nothing machine-specific in code or docs.** No addresses, host names, user names or paths from your own setup. Use
  `192.168.1.x`, `worker1` and `C:\path\to\project` in examples.
- **No secrets** anywhere: not in the config, the docs, a test or a commit message. MCP secrets go through `${env:NAME}`.
- **Your own notes stay yours.** A file that only makes sense in your copy (notes, a local `.vscode/` setup) goes in
  `.git/info/exclude`, which is never committed, not in `.gitignore`. A setting that differs per machine belongs in
  `fleet.config.json`, an environment variable or a VS Code setting, never in the code.
- **Small models are the audience.** A change that helps a 7B model (a clearer tool description, a stricter check, a
  smaller tool) is usually worth more than one that helps a 70B model.
- **Enforce in code what matters.** If a rule must hold (plan mode cannot write, a step is done only when its check passes),
  do not rely on the prompt alone.
- **Explain the why in comments** where it is not obvious, especially workarounds for Ollama or model behavior, and link the
  issue.
- Prose in the docs is plain: short sentences, no filler, no em dashes.

## Reporting problems

Include the output of `.\scripts\Test-Fleet.ps1` and the end of the backend log, with anything private removed. For a
security issue, describe it without a working exploit.
