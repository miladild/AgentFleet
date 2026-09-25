#!/usr/bin/env bash
# Make the fleet start by itself when you log in: systemd user units on Linux, launchd agents on macOS.
# It publishes the backend to an install folder and runs the built web UI, both as you, so the tools the
# fleet gives its models act with your own permissions. No sudo, no firewall change. Run it again to update.
# Usage: bash scripts/install-autostart.sh [options]
#   --install-root DIR  Where the backend is published. Default: ~/.local/share/agent-fleet (Linux),
#                       ~/Library/Application Support/AgentFleet (macOS).
#   --backend-port N    Backend port (default 8000).
#   --frontend-port N   Web UI port (default 3000).
#   --listen-on-lan     Let other machines reach the backend (for @fleet elsewhere). Opens no firewall.
#   --skip-frontend     Install the backend only.
#   --at-boot           Linux: also start at boot, before you log in (loginctl enable-linger).
#   --uninstall         Stop and remove the autostart entries. The install folder is left alone.
#   --dry-run           Print what would be done and change nothing.
#   --help              Show this option list.

set -euo pipefail

usage() {
  sed -n '2,15p' "$0"
}

install_root=""
backend_port=8000
frontend_port=3000
listen_on_lan=0
skip_frontend=0
at_boot=0
uninstall=0
dry_run=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --install-root|--backend-port|--frontend-port)
      if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then
        printf '%s needs a value.\n' "$1" >&2; exit 2
      fi
      case "$1" in
        --install-root) install_root="$2" ;;
        --backend-port) backend_port="$2" ;;
        --frontend-port) frontend_port="$2" ;;
      esac
      shift 2 ;;
    --listen-on-lan) listen_on_lan=1; shift ;;
    --skip-frontend) skip_frontend=1; shift ;;
    --at-boot) at_boot=1; shift ;;
    --uninstall) uninstall=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown option: %s (see --help)\n' "$1" >&2; exit 2 ;;
  esac
done

say()  { printf '\n== %s\n' "$*"; }
ok()   { printf '  [ ok ] %s\n' "$*"; }
warn() { printf '  [warn] %s\n' "$*"; }
info() { printf '         %s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

# Runs a command, or with --dry-run only says what it would run.
run() {
  if [ "$dry_run" -eq 1 ]; then info "would run: $*"; else "$@"; fi
}

valid_port() {
  [[ "$1" =~ ^[0-9]+$ ]] && [ "$1" -ge 1 ] && [ "$1" -le 65535 ]
}

valid_port "$backend_port" || fail "--backend-port must be a number from 1 to 65535."
valid_port "$frontend_port" || fail "--frontend-port must be a number from 1 to 65535."
[ "$backend_port" != "$frontend_port" ] || fail "The backend and the web UI need different ports."

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
web_dir="$repo_root/agent-fleet"
backend_source="$web_dir/agent"
os="$(uname -s)"

case "$os" in
  Linux) default_root="${XDG_DATA_HOME:-$HOME/.local/share}/agent-fleet" ;;
  Darwin) default_root="$HOME/Library/Application Support/AgentFleet" ;;
  *) fail "This script supports Linux and macOS. On Windows use scripts\\Install-Autostart.ps1. Detected: $os" ;;
esac
install_root="${install_root:-$default_root}"
case "$install_root" in /*) ;; *) install_root="$PWD/$install_root" ;; esac
backend_target="$install_root/backend"
bind=localhost
[ "$listen_on_lan" -eq 1 ] && bind=0.0.0.0
urls="http://$bind:$backend_port"
web_host=127.0.0.1
# A release download (backend/ and web/server.js next to scripts/) is already built: it runs where it was
# unpacked, with its self-contained backend and Next.js's standalone web server.
bundle=0
if [ -f "$repo_root/web/server.js" ]; then
  bundle=1
  install_root="$repo_root"
  backend_target="$repo_root/backend"
  web_dir="$repo_root/web"
fi

units_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
backend_unit=agent-fleet-backend.service
frontend_unit=agent-fleet-frontend.service
agents_dir="$HOME/Library/LaunchAgents"
backend_label=com.agentfleet.backend
frontend_label=com.agentfleet.frontend

# --- stop and remove ----------------------------------------------------------------------------
stop_all() {
  if [ "$os" = Linux ]; then
    for unit in "$backend_unit" "$frontend_unit"; do
      if [ -e "$units_dir/$unit" ]; then run systemctl --user stop "$unit" || true; fi
    done
  else
    for label in "$backend_label" "$frontend_label"; do
      if [ -e "$agents_dir/$label.plist" ]; then run launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true; fi
    done
  fi
}

if [ "$uninstall" -eq 1 ]; then
  say 'Removing autostart'
  if [ "$os" = Linux ]; then
    for unit in "$backend_unit" "$frontend_unit"; do
      if [ -e "$units_dir/$unit" ]; then
        run systemctl --user disable --now "$unit" || true
        run rm -f "$units_dir/$unit"
        ok "Removed $unit"
      fi
    done
    run systemctl --user daemon-reload || true
    if [ "$(loginctl show-user "$USER" -p Linger --value 2>/dev/null || true)" = yes ]; then
      info "Your user services still start at boot (turned on by --at-boot). Turn that off with: loginctl disable-linger $USER"
    fi
  else
    for label in "$backend_label" "$frontend_label"; do
      if [ -e "$agents_dir/$label.plist" ]; then
        run launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
        run rm -f "$agents_dir/$label.plist"
        ok "Removed $label"
      fi
    done
  fi
  info "Files in $install_root were left alone (your config, conversations, plans and logs). Delete that folder yourself if you want it gone."
  exit 0
fi

# --- prerequisites ------------------------------------------------------------------------------
say 'Prerequisites'
dotnet_path="$(command -v dotnet || true)"
node_path="$(command -v node || true)"
[ -n "$node_path" ] || fail "Node.js 20 or newer is missing (https://nodejs.org). It runs the web UI."
if [ "$bundle" -eq 0 ]; then
  [ -n "$dotnet_path" ] || fail ".NET SDK is missing. Run bash scripts/setup-hub.sh first."
  command -v npm >/dev/null 2>&1 || fail "npm is missing. Run bash scripts/setup-hub.sh first."
fi
# Autostart runs with a bare environment, so it gets absolute paths. readlink -f resolves the symlinks
# package managers put on PATH; macOS before 12.3 has no -f, and the path as found works there too.
node_path="$(readlink -f "$node_path" 2>/dev/null || printf '%s' "$node_path")"
if [ "$bundle" -eq 1 ]; then
  dotnet_root="$backend_target"
  ok "node ($node_path); a release download needs nothing else"
else
  dotnet_path="$(readlink -f "$dotnet_path" 2>/dev/null || printf '%s' "$dotnet_path")"
  dotnet_root="$(dirname "$dotnet_path")"
  ok "dotnet ($dotnet_path), node ($node_path) and npm"
fi

# What runs: a source install runs the published DLL with dotnet and the web UI through serve.mjs; a release
# download runs its self-contained backend and Next.js's standalone server (which reads HOSTNAME).
if [ "$bundle" -eq 1 ]; then
  backend_cmd=("$backend_target/AgentFleet" --urls "$urls")
  web_cmd=("$node_path" "$web_dir/server.js")
  host_var=HOSTNAME
else
  backend_cmd=("$dotnet_path" "$backend_target/AgentFleet.dll" --urls "$urls")
  web_cmd=("$node_path" scripts/serve.mjs start)
  host_var=FLEET_WEB_HOST
fi

if [ "$os" = Linux ]; then
  command -v systemctl >/dev/null 2>&1 || fail "systemctl is missing: this needs a Linux with systemd."
  if ! systemctl --user show-environment >/dev/null 2>&1; then
    if [ "$dry_run" -eq 1 ]; then
      warn 'systemd user services are not running for this login (on WSL, enable systemd in /etc/wsl.conf).'
    else
      fail "systemd user services are not running for this login, so nothing can be registered. On WSL add [boot] systemd=true to /etc/wsl.conf and restart WSL. Over SSH, log in normally first or use --at-boot."
    fi
  else
    ok 'systemd user services'
  fi
fi

# --- build --------------------------------------------------------------------------------------
if [ "$skip_frontend" -eq 0 ] && [ "$bundle" -eq 0 ]; then
  say 'Building the web UI'
  if [ "$dry_run" -eq 1 ]; then
    info "would run: (cd '$web_dir' && npm install --no-audit --no-fund && npm run build)"
  else
    (cd "$web_dir" && npm install --no-audit --no-fund && npm run build)
    ok 'Built'
  fi
fi

if [ "$bundle" -eq 1 ]; then
  say "Running the download in place ($repo_root)"
  stop_all
  ok 'Keep this folder where it is: the autostart runs the fleet from here'
else
say "Publishing the backend to $backend_target"
stop_all
if [ "$dry_run" -eq 1 ]; then
  info "would run: (cd '$backend_source' && dotnet publish -c Release -o '$backend_target' -nologo -v q)"
else
  mkdir -p "$install_root"
  chmod 700 "$install_root"
  (cd "$backend_source" && dotnet publish -c Release -o "$backend_target" -nologo -v q)
  ok 'Published'
fi
fi

# The settings made by setup-hub.sh (or by hand) come along the first time only.
dev_config="$web_dir/fleet.config.json"
installed_config="$backend_target/fleet.config.json"
if [ "$bundle" -eq 1 ]; then
  info 'The backend keeps its settings in backend/fleet.config.json (written on its first start).'
elif [ -e "$installed_config" ]; then
  ok 'The install already has its own fleet.config.json (left as it is)'
elif [ -e "$dev_config" ]; then
  run cp "$dev_config" "$installed_config"
  run chmod 600 "$installed_config"
  ok 'Copied your fleet.config.json (from agent-fleet) into the install'
else
  info 'No fleet.config.json yet: the backend writes a one-machine config on its first start.'
fi

# --- register -----------------------------------------------------------------------------------
# systemd expands % specifiers, so a literal % in a value is written %%.
systemd_value() { printf '%s' "${1//%/%%}"; }
# One ExecStart value from a command's words, each quoted for systemd.
exec_line() { local out="" word; for word in "$@"; do out+="\"$(systemd_value "$word")\" "; done; printf '%s' "${out% }"; }
xml_escape() { local s="${1//&/&amp;}"; s="${s//</&lt;}"; printf '%s' "${s//>/&gt;}"; }

write_file() {
  # write_file PATH: content on stdin. With --dry-run the content is shown instead.
  if [ "$dry_run" -eq 1 ]; then
    info "would write $1:"
    sed 's/^/           | /'
  else
    mkdir -p "$(dirname "$1")"
    cat > "$1"
  fi
}

run_path="$(systemd_value "$PATH")"

if [ "$os" = Linux ]; then
  say 'Registering systemd user units (start when you log in, run as you)'
  write_file "$units_dir/$backend_unit" <<EOF
[Unit]
Description=Agent Fleet backend
After=network-online.target

[Service]
Type=simple
WorkingDirectory=$(systemd_value "$backend_target")
ExecStart=$(exec_line "${backend_cmd[@]}")
Environment="DOTNET_ROOT=$(systemd_value "$dotnet_root")"
Environment="PATH=$run_path"
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
  ok "$backend_unit"
  units=("$backend_unit")
  if [ "$skip_frontend" -eq 0 ]; then
    write_file "$units_dir/$frontend_unit" <<EOF
[Unit]
Description=Agent Fleet web UI
After=$backend_unit

[Service]
Type=simple
WorkingDirectory=$(systemd_value "$web_dir")
ExecStart=$(exec_line "${web_cmd[@]}")
Environment="PORT=$frontend_port" "$host_var=$web_host" "AGENT_URL=http://localhost:$backend_port"
Environment="PATH=$run_path"
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
    ok "$frontend_unit"
    units+=("$frontend_unit")
  fi
  run systemctl --user daemon-reload
  run systemctl --user enable "${units[@]}"
  say 'Starting'
  run systemctl --user restart "${units[@]}"
  if [ "$at_boot" -eq 1 ]; then
    if run loginctl enable-linger "$USER"; then
      ok 'Starts at boot too, before you log in'
    else
      warn "Could not turn on start at boot. Ask an administrator to run: sudo loginctl enable-linger $USER"
    fi
  fi
  logs_hint="journalctl --user -u $backend_unit, or $backend_target/logs"
else
  say 'Registering launchd agents (start when you log in, run as you)'
  plist() {
    # plist LABEL WORKDIR LOG ENV_XML ARG...
    local label="$1" workdir="$2" log="$3" env_xml="$4"
    shift 4
    printf '<?xml version="1.0" encoding="UTF-8"?>\n'
    printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
    printf '<plist version="1.0">\n<dict>\n'
    printf '  <key>Label</key><string>%s</string>\n' "$label"
    printf '  <key>ProgramArguments</key>\n  <array>\n'
    local arg
    for arg in "$@"; do printf '    <string>%s</string>\n' "$(xml_escape "$arg")"; done
    printf '  </array>\n'
    printf '  <key>WorkingDirectory</key><string>%s</string>\n' "$(xml_escape "$workdir")"
    printf '  <key>EnvironmentVariables</key>\n  <dict>\n%s  </dict>\n' "$env_xml"
    printf '  <key>RunAtLoad</key><true/>\n'
    printf '  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n'
    printf '  <key>StandardOutPath</key><string>%s</string>\n' "$(xml_escape "$log")"
    printf '  <key>StandardErrorPath</key><string>%s</string>\n' "$(xml_escape "$log")"
    printf '</dict>\n</plist>\n'
  }
  env_pair() { printf '    <key>%s</key><string>%s</string>\n' "$1" "$(xml_escape "$2")"; }

  plist "$backend_label" "$backend_target" "$install_root/backend.log" \
    "$(env_pair DOTNET_ROOT "$dotnet_root")$(env_pair PATH "$PATH")" \
    "${backend_cmd[@]}" | write_file "$agents_dir/$backend_label.plist"
  ok "$backend_label"
  labels=("$backend_label")
  if [ "$skip_frontend" -eq 0 ]; then
    plist "$frontend_label" "$web_dir" "$install_root/frontend.log" \
      "$(env_pair PORT "$frontend_port")$(env_pair "$host_var" "$web_host")$(env_pair AGENT_URL "http://localhost:$backend_port")$(env_pair PATH "$PATH")" \
      "${web_cmd[@]}" | write_file "$agents_dir/$frontend_label.plist"
    ok "$frontend_label"
    labels+=("$frontend_label")
  fi
  say 'Starting'
  for label in "${labels[@]}"; do
    run launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
    run launchctl bootstrap "gui/$(id -u)" "$agents_dir/$label.plist"
  done
  [ "$at_boot" -eq 1 ] && warn '--at-boot is for Linux. On macOS the agents start when you log in.'
  logs_hint="$install_root/backend.log, or $backend_target/logs"
fi

if [ "$listen_on_lan" -eq 1 ]; then
  warn "The backend listens on every network interface (port $backend_port). This script opened no firewall: allow that port from your local network only, never from the internet. Anyone who can reach it can use the fleet's file and command tools. Read docs/security.md."
fi

# --- wait ---------------------------------------------------------------------------------------
if [ "$dry_run" -eq 0 ]; then
  say 'Waiting for the backend'
  health=""
  for _ in $(seq 1 30); do
    # /health answers 503 when the fallback machine is down, which still means the backend is up.
    health="$(curl -s --max-time 5 "http://localhost:$backend_port/health" || true)"
    [ -n "$health" ] && break
    sleep 2
  done
  if [ -n "$health" ]; then
    status="$(printf '%s' "$health" | sed -n 's/.*"status":"\([a-z_]*\)".*/\1/p' | head -n 1)"
    ok "Backend answers (overall: ${status:-unknown})"
  else
    warn "The backend did not answer within 60 seconds. Look in $logs_hint."
  fi
fi

printf '\nDone. Web UI: http://localhost:%s   Backend: http://localhost:%s\n' "$frontend_port" "$backend_port"
printf 'Logs: %s\n' "${logs_hint:-}"
printf 'Update after changing the code: run this script again. Remove: bash scripts/install-autostart.sh --uninstall\n'
