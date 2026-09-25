#!/usr/bin/env bash
# Starts a downloaded Agent Fleet release: the backend and the web UI, until you press Ctrl+C.
# For a release download (backend/ and web/ next to this scripts folder). Needs Node.js 20 or newer for the
# web UI, and Ollama with at least one model. Nothing is built or installed. To start the fleet at every
# login instead, run: bash scripts/install-autostart.sh. From a source checkout, use `npm run dev` in agent-fleet.
# Usage: bash scripts/start-fleet.sh [options]
#   --backend-port N    Backend port (default 8000).
#   --frontend-port N   Web UI port (default 3000).
#   --web-on-lan        Let other machines open the web UI. There is no login: read docs/security.md first.
#   --help              Show this option list.

set -euo pipefail

backend_port=8000
frontend_port=3000
web_host=127.0.0.1
while [ "$#" -gt 0 ]; do
  case "$1" in
    --backend-port) backend_port="${2:?--backend-port needs a value}"; shift 2 ;;
    --frontend-port) frontend_port="${2:?--frontend-port needs a value}"; shift 2 ;;
    --web-on-lan) web_host=0.0.0.0; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) printf 'Unknown option: %s (see --help)\n' "$1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ ! -x "$root/backend/AgentFleet" ] || [ ! -f "$root/web/server.js" ]; then
  echo 'This is not a release download (no backend/ and web/ folders). From a source checkout, run: cd agent-fleet && npm run dev' >&2
  exit 1
fi
command -v node >/dev/null 2>&1 || { echo 'Node.js 20 or newer is needed for the web UI (https://nodejs.org).' >&2; exit 1; }
command -v ollama >/dev/null 2>&1 || echo 'Note: Ollama is not installed here. Install it from https://ollama.com unless your models run on other machines.'

# The backend checks plan diagrams in the web UI; the web UI talks to the backend. HOSTNAME is the address
# the web UI listens on (the shell sets it to this machine's name, which would open it to the network).
export FLEET_FRONTEND_URL="http://localhost:$frontend_port"
export COPILOTKIT_TELEMETRY_DISABLED=true
(cd "$root/backend" && exec ./AgentFleet --urls "http://localhost:$backend_port") &
backend=$!
(cd "$root/web" && PORT="$frontend_port" HOSTNAME="$web_host" AGENT_URL="http://localhost:$backend_port" exec node server.js) &
web=$!
trap 'kill "$backend" "$web" 2>/dev/null || true' EXIT INT TERM

printf '\nAgent Fleet is starting. Web UI: http://localhost:%s   Backend: http://localhost:%s\n' "$frontend_port" "$backend_port"
echo 'The first start writes backend/fleet.config.json with one machine (this one). Add machines in the web UI under Config.'
echo 'Press Ctrl+C to stop both.'
while kill -0 "$backend" 2>/dev/null && kill -0 "$web" 2>/dev/null; do sleep 1; done
kill -0 "$backend" 2>/dev/null || echo 'The backend stopped. Its log is in backend/logs.' >&2
kill -0 "$web" 2>/dev/null || echo 'The web UI stopped.' >&2
