#!/usr/bin/env bash
# Starts a downloaded Agent Fleet release: the backend and the web UI, until you press Ctrl+C.
# For a release download (backend/ and web/ next to this scripts folder). Needs Node.js 20 or newer for the
# web UI, and Ollama on at least one machine. Nothing is built or installed. When the web UI answers it opens
# in your browser (on a desktop); its Setup tab takes it from there. To start the fleet at every login
# instead, run: bash scripts/install-autostart.sh. From a source checkout, use `npm run dev` in agent-fleet.
# Usage: bash scripts/start-fleet.sh [options]
#   --backend-port N    Backend port (default 8000).
#   --frontend-port N   Web UI port (default 3000).
#   --web-on-lan        Let other machines open the web UI. There is no login: read docs/security.md first.
#   --no-browser        Do not open the web UI in a browser.
#   --help              Show this option list.

set -euo pipefail

backend_port=8000
frontend_port=3000
web_host=127.0.0.1
open_browser=1
while [ "$#" -gt 0 ]; do
  case "$1" in
    --backend-port) backend_port="${2:?--backend-port needs a value}"; shift 2 ;;
    --frontend-port) frontend_port="${2:?--frontend-port needs a value}"; shift 2 ;;
    --web-on-lan) web_host=0.0.0.0; shift ;;
    --no-browser) open_browser=0; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) printf 'Unknown option: %s (see --help)\n' "$1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ ! -x "$root/backend/AgentFleet" ] || [ ! -f "$root/web/server.js" ]; then
  echo 'This is not a release download (no backend/ and web/ folders). From a source checkout, run: cd agent-fleet && npm run dev' >&2
  exit 1
fi
command -v node >/dev/null 2>&1 || { echo 'Node.js 20 or newer is needed for the web UI (https://nodejs.org).' >&2; exit 1; }
node_major="$(node --version | sed -E 's/^v([0-9]+).*/\1/')"
[ "$node_major" -ge 20 ] || { echo "Node.js $(node --version) is too old for the web UI. Install version 20 or newer (https://nodejs.org)." >&2; exit 1; }
command -v ollama >/dev/null 2>&1 || echo 'Note: Ollama is not installed here. The web UI will show how to install it (https://ollama.com), unless your models run on other machines.'

web_url="http://localhost:$frontend_port"
port_in_use() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }
answers() { command -v curl >/dev/null 2>&1 && curl -s -o /dev/null -m 3 "$1"; }
open_web_ui() {
  [ "$open_browser" = 1 ] || return 0
  if [ "$(uname -s)" = Darwin ]; then open "$web_url" >/dev/null 2>&1 || true
  elif [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && command -v xdg-open >/dev/null 2>&1; then xdg-open "$web_url" >/dev/null 2>&1 || true
  fi
}

# Already running (at login, or in another terminal)? Then there is nothing to start.
if port_in_use "$backend_port" && answers "http://localhost:$backend_port/api/fleet-mode"; then
  echo "Agent Fleet is already running on this computer. Web UI: $web_url"
  open_web_ui
  exit 0
fi
for port in "$backend_port" "$frontend_port"; do
  if port_in_use "$port"; then
    echo "Port $port is already used by another program. Close it, or pick other ports with --backend-port / --frontend-port." >&2
    exit 1
  fi
done

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
echo 'Leave this running while you use the fleet. Press Ctrl+C to stop it.'
opened=0
waited=0
while kill -0 "$backend" 2>/dev/null && kill -0 "$web" 2>/dev/null; do
  if [ "$opened" = 0 ]; then
    if answers "$web_url"; then
      opened=1
      echo "Ready: $web_url"
      open_web_ui
    elif [ "$waited" = 30 ]; then
      echo 'Still starting. The first start of a new download can take a minute or two.'
    fi
  fi
  sleep 1
  waited=$((waited + 1))
done
kill -0 "$backend" 2>/dev/null || echo 'The backend stopped. Its log is in backend/logs.' >&2
kill -0 "$web" 2>/dev/null || echo 'The web UI stopped.' >&2
