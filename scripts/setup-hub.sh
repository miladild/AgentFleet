#!/usr/bin/env bash
# Prepare a Linux or macOS checkout as a local Agent Fleet hub.
# This script never opens a firewall, changes a bind address, or overwrites user config.
# Usage: bash scripts/setup-hub.sh [options]
#   --model NAME        Pick the coding model (otherwise sized from local memory).
#   --triage-model NAME Set the small routing model (default: llama3.2:latest).
#   --skip-model        Do not check or download the coding model.
#   --dry-run           Print build and setup actions without changing anything.
#   --yes, -y           Approve the coding-model download without prompting.
#   --help              Show this option list.

set -euo pipefail

usage() {
  sed -n '2,10p' "$0"
}

model=""
triage_model="llama3.2:latest"
skip_model=0
dry_run=0
assume_yes=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --model|--triage-model)
      if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then
        printf '%s requires a model name.\n' "$1" >&2; exit 2
      fi
      if [ "$1" = --model ]; then model="$2"; else triage_model="$2"; fi
      shift 2 ;;
    --skip-model) skip_model=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    --yes|-y) assume_yes=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown option: %s (see --help)\n' "$1" >&2; exit 2 ;;
  esac
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
web_dir="$repo_root/agent-fleet"
backend_dir="$web_dir/agent"
os="$(uname -s)"

say()  { printf '\n== %s\n' "$*"; }
ok()   { printf '  [ ok ] %s\n' "$*"; }
warn() { printf '  [warn] %s\n' "$*"; }
info() { printf '         %s\n' "$*"; }

confirm() {
  if [ "$assume_yes" -eq 1 ]; then return 0; fi
  if [ ! -t 0 ]; then
    warn "No interactive terminal; pass --yes to approve this download."
    return 1
  fi
  local answer
  read -r -p "$1 [y/N] " answer
  case "$answer" in [Yy]|[Yy][Ee][Ss]) return 0 ;; *) return 1 ;; esac
}

valid_model_name() {
  [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*(:[A-Za-z0-9._-]+)?$ ]]
}

if [ "$os" != Linux ] && [ "$os" != Darwin ]; then
  printf 'This script supports Linux and macOS. Detected: %s\n' "$os" >&2
  exit 1
fi
if ! valid_model_name "$triage_model"; then
  echo 'Invalid triage model name. Use letters, numbers, dot, underscore, slash, hyphen, and an optional tag after colon.' >&2
  exit 2
fi
if [ -n "$model" ] && ! valid_model_name "$model"; then
  echo 'Invalid model name. Use letters, numbers, dot, underscore, slash, hyphen, and an optional tag after colon.' >&2
  exit 2
fi

# --- 1. prerequisites ---------------------------------------------------------------------------
say 'Prerequisites'
missing=()
if ! command -v dotnet >/dev/null 2>&1; then
  missing+=(".NET SDK 9 or newer (https://dotnet.microsoft.com/download)")
elif ! dotnet --list-sdks 2>/dev/null | awk -F. '$1 >= 9 { found=1 } END { exit !found }'; then
  missing+=(".NET SDK 9 or newer (https://dotnet.microsoft.com/download)")
else
  ok '.NET SDK 9 or newer'
fi

if ! command -v node >/dev/null 2>&1; then
  missing+=("Node.js 20 or newer (https://nodejs.org/en/download)")
else
  node_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || printf 0)"
  if [[ "$node_major" =~ ^[0-9]+$ ]] && [ "$node_major" -ge 20 ] && command -v npm >/dev/null 2>&1; then
    ok "Node.js $(node --version) and npm"
  else
    missing+=("Node.js 20 or newer with npm (https://nodejs.org/en/download)")
  fi
fi

if command -v ollama >/dev/null 2>&1; then
  ok 'Ollama CLI'
else
  warn 'Ollama is not installed (https://ollama.com/download). The app and builds can still be prepared; model setup will be skipped.'
fi

if [ "${#missing[@]}" -gt 0 ]; then
  printf '\nInstall the following prerequisites, then run this script again:\n' >&2
  for item in "${missing[@]}"; do printf '  - %s\n' "$item" >&2; done
  exit 1
fi

# --- 2. choose model -----------------------------------------------------------------------------
if [ -z "$model" ]; then
  ram_gb=0
  vram_gb=0
  if [ "$os" = Darwin ]; then
    if command -v sysctl >/dev/null 2>&1; then
      mem_bytes="$(sysctl -n hw.memsize 2>/dev/null || printf 0)"
      [[ "$mem_bytes" =~ ^[0-9]+$ ]] && ram_gb=$((mem_bytes / 1024 / 1024 / 1024))
    fi
  elif [ -r /proc/meminfo ]; then
    ram_gb="$(awk '/^MemTotal:/ { printf "%d", $2 / 1024 / 1024 }' /proc/meminfo)"
  fi
  if [ "$os" = Linux ] && command -v nvidia-smi >/dev/null 2>&1; then
    vram_mb="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -n 1 | tr -cd '0-9' || true)"
    if [[ "$vram_mb" =~ ^[0-9]+$ ]]; then vram_gb=$((vram_mb / 1024)); fi
  fi

  if [ "$os" = Darwin ]; then
    # macOS lets the graphics side use about two thirds of the shared memory (same rule as the web UI).
    shared_gb=$((ram_gb * 66 / 100))
    if [ "$shared_gb" -ge 22 ]; then model='qwen3-coder:30b'
    elif [ "$shared_gb" -ge 11 ]; then model='qwen2.5-coder:14b'
    elif [ "$ram_gb" -ge 16 ]; then model='qwen2.5-coder:7b'
    else model='qwen2.5-coder:3b'; fi
    info "Chose $model for $ram_gb GB unified memory; override with --model."
  else
    if [ "$vram_gb" -ge 22 ]; then model='qwen3-coder:30b'
    elif [ "$vram_gb" -ge 11 ]; then model='qwen2.5-coder:14b'
    elif [ "$vram_gb" -ge 6 ] || [ "$ram_gb" -ge 16 ]; then model='qwen2.5-coder:7b'
    else model='qwen2.5-coder:3b'; fi
    info "Chose $model for $ram_gb GB RAM and $vram_gb GB NVIDIA memory; override with --model."
  fi
fi

# --- 3. build ------------------------------------------------------------------------------------
say 'Build'
if [ "$dry_run" -eq 1 ]; then
  info "would run: (cd '$web_dir' && npm ci --no-audit --no-fund)"
  info "would run: (cd '$backend_dir' && dotnet build -nologo -v q)"
else
  (cd "$web_dir" && npm ci --no-audit --no-fund)
  ok 'Installed web UI packages'
  (cd "$backend_dir" && dotnet build -nologo -v q)
  ok 'Built the backend'
fi

# --- 4. local config -----------------------------------------------------------------------------
say 'Configuration'
env_local="$web_dir/.env.local"
if [ -e "$env_local" ]; then
  ok 'agent-fleet/.env.local already exists (left unchanged)'
elif [ "$dry_run" -eq 1 ]; then
  info 'would create agent-fleet/.env.local from .env.example, without overwriting an existing file'
else
  if (set -o noclobber; cat > "$env_local") < "$web_dir/.env.example"; then
    chmod 600 "$env_local"
    ok 'Created agent-fleet/.env.local'
  else
    ok 'agent-fleet/.env.local appeared during setup (left unchanged)'
  fi
fi

config="$web_dir/fleet.config.json"
if [ -e "$config" ]; then
  ok 'agent-fleet/fleet.config.json already exists (left unchanged)'
elif [ "$dry_run" -eq 1 ]; then
  info "would create agent-fleet/fleet.config.json for the hub model '$model', without overwriting an existing file"
else
  # Model names were validated above, and noclobber protects against a concurrent first-run writer.
  if (set -o noclobber; cat > "$config") <<EOF
{
  "triageModel": "$triage_model",
  "mode": "conservative",
  "planModeEnabled": false,
  "nodes": [
    {
      "name": "hub",
      "url": "http://127.0.0.1:11434/v1",
      "model": "$model",
      "purpose": "This machine: coding, tools and fallback.",
      "tier": "heavy",
      "fallback": true
    }
  ],
  "tools": {
    "read_file": { "enabled": true },
    "write_file": { "enabled": true },
    "edit_file": { "enabled": true },
    "list_directory": { "enabled": true },
    "find_files": { "enabled": true },
    "search_files": { "enabled": true },
    "run_git_command": { "enabled": true },
    "run_command": { "enabled": true },
    "run_sandboxed_code": { "enabled": true },
    "web_search": { "enabled": true },
    "web_fetch": { "enabled": true }
  },
  "mcpServers": {},
  "sandbox": { "mode": "auto" }
}
EOF
  then
    chmod 600 "$config"
    ok "Created agent-fleet/fleet.config.json with $model as the only node"
  else
    ok 'agent-fleet/fleet.config.json appeared during setup (left unchanged)'
  fi
fi

# --- 5. optional local model pull ----------------------------------------------------------------
if [ "$skip_model" -eq 0 ]; then
  say "Model: $model"
  if ! command -v ollama >/dev/null 2>&1; then
    warn 'Install Ollama from https://ollama.com/download, then run: ollama pull <model>'
  elif [ "$dry_run" -eq 1 ]; then
    info "would check the local Ollama server and offer to download $model"
  elif ! curl -fsS --max-time 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
    warn 'Ollama is installed but not answering on 127.0.0.1:11434. Start Ollama, then run:'
    info "ollama pull $model"
  elif ollama list 2>/dev/null | awk -v wanted="$model" 'NR > 1 && ($1 == wanted || $1 == wanted ":latest") { found=1 } END { exit !found }'; then
    ok "$model is already installed"
  elif confirm "Download $model now? This may take several GB."; then
    ollama pull "$model"
    ok "$model is installed"
  else
    warn "Skipped. Later: ollama pull $model"
  fi
fi

printf '\nThe hub is prepared. Run it as your normal user account.\n'
printf '  Start:  cd "%s" && npm run dev\n' "$web_dir"
printf '  Open:   http://localhost:3000\n'
printf '  Check:  curl http://localhost:8000/health\n'
printf '  Read:   %s/docs/security.md\n' "$repo_root"
printf '\nThe development scripts bind the backend to localhost and the web UI to this machine.\n'
printf 'Do not expose the UI, backend, or Ollama to the internet; the fleet has no login.\n'
