#!/usr/bin/env bash
# Get-HardwareInventory.sh
# Linux equivalent of Get-HardwareInventory.ps1, for fleet machines that aren't Windows.
# Run: bash Get-HardwareInventory.sh

set -uo pipefail

if [ -d "$HOME/Desktop" ]; then
  out="$HOME/Desktop/hardware-inventory.txt"
else
  out="$HOME/hardware-inventory.txt"
fi

{
  echo "===== HARDWARE INVENTORY ====="
  echo "Generated: $(date)"
  echo

  echo "--- OS ---"
  if [ -f /etc/os-release ]; then
    . /etc/os-release
    echo "Distro: ${PRETTY_NAME:-unknown}"
  fi
  echo "Kernel: $(uname -srm)"
  echo

  echo "--- CPU ---"
  if command -v lscpu >/dev/null 2>&1; then
    lscpu | grep -E "Model name|^CPU\(s\)|Thread|Core|MHz"
  else
    echo "lscpu not available"
  fi
  echo

  echo "--- RAM ---"
  free -h | grep -E "Mem|Swap"
  echo

  echo "--- GPU ---"
  if command -v lspci >/dev/null 2>&1; then
    lspci | grep -iE "vga|3d|display"
  else
    echo "lspci not available"
  fi
  if command -v nvidia-smi >/dev/null 2>&1; then
    echo
    echo "--- NVIDIA (nvidia-smi) ---"
    nvidia-smi --query-gpu=name,memory.total,driver_version,compute_cap --format=csv,noheader
  fi
  echo

  echo "--- STORAGE ---"
  if command -v lsblk >/dev/null 2>&1; then
    lsblk -d -o NAME,SIZE,ROTA,TYPE,MODEL 2>/dev/null
  fi
  echo
  df -h / | tail -1 | awk '{print "Free on /: " $4}'
  echo

  echo "--- NETWORK ADAPTERS ---"
  if command -v ip >/dev/null 2>&1; then
    ip -brief addr show 2>/dev/null
  else
    ifconfig
  fi
  echo

  # Ollama check via its local HTTP API rather than invoking the CLI directly.
  # The Windows script hit a bug where invoking the CLI could hang; querying the
  # API instead is instant and avoids that class of issue on any platform.
  echo "--- OLLAMA ---"
  if command -v ollama >/dev/null 2>&1; then
    echo "Installed: $(command -v ollama)"
    tags_json=$(curl -s -m 3 http://127.0.0.1:11434/api/tags 2>/dev/null)
    if [ -n "$tags_json" ]; then
      echo "Models pulled:"
      if command -v python3 >/dev/null 2>&1; then
        python3 - "$tags_json" <<'PYEOF'
import json, sys
data = json.loads(sys.argv[1])
for m in data.get("models", []):
    size_gb = round(m.get("size", 0) / (1024**3), 2)
    d = m.get("details", {})
    print(f"  {m['name']}: {size_gb} GB ({d.get('parameter_size','?')}, {d.get('quantization_level','?')})")
PYEOF
      else
        echo "$tags_json"
      fi
    else
      echo "Ollama server not responding on 127.0.0.1:11434 (service may not be running)."
    fi
  else
    echo "Ollama not found in PATH."
  fi
} | tee "$out"

echo
echo "Saved to $out. Compare it with the model sizes in docs/adding-machines.md to pick what this machine can run."
