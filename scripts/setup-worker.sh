#!/usr/bin/env bash
# Prepares a Linux machine to be a worker in the fleet: Ollama installed, listening on the network,
# and reachable only from where you say.
#
# Run it on the WORKER machine:   sudo ./setup-worker.sh --allow-from 192.168.1.10
#
# It requires an active, restrictive ufw or firewalld policy, checks that the Ollama port has no broader
# allow rule, installs Ollama (the official installer) if it is missing, makes Ollama listen on the network
# through a systemd override, opens the port only for the address you name, and can turn off Wi-Fi power
# saving. It does NOT download a model: the hub does that over the network (Add-FleetNode.ps1 -Pull), and
# the command to run is printed at the end.
#
# Ollama has no login of its own, so anyone who can reach its port can use the machine. Keep it on
# your LAN, never port-forward it, and set --allow-from to the hub's exact private IPv4 address.
#
# Options:
#   --allow-from ADDR   Required: one known RFC1918 private IPv4 peer address (usually the hub).
#                       CIDR networks, hostnames, wildcards, and whole-subnet rules are rejected.
#   --port N            Ollama's port (default 11434).
#   --wifi-powersave-off  Turn Wi-Fi power saving off now and after reboot (NetworkManager).
#   --dry-run           Print what would be done and change nothing.
#   --yes               Do not ask questions.
# A real run needs UFW with default-deny/reject inbound, or firewalld with no broader rule for Ollama's port.

set -euo pipefail

allow_from=""
port=11434
wifi_off=0
dry_run=0
assume_yes=0

while [ $# -gt 0 ]; do
  case "$1" in
    --allow-from)
      if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then echo '--allow-from requires a peer IPv4 address.' >&2; exit 2; fi
      allow_from="$2"; shift 2 ;;
    --port)
      if [ "$#" -lt 2 ] || [[ "$2" == --* ]]; then echo '--port requires a number.' >&2; exit 2; fi
      port="$2"; shift 2 ;;
    --wifi-powersave-off) wifi_off=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    --yes|-y) assume_yes=1; shift ;;
  -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
  esac
done

say()  { printf '\n== %s\n' "$*"; }
ok()   { printf '  [ ok ] %s\n' "$*"; }
warn() { printf '  [warn] %s\n' "$*"; }
info() { printf '         %s\n' "$*"; }
run() {
  if [ "$dry_run" -eq 1 ]; then info "would run: $*"; else "$@"; fi
}
confirm() {
  [ "$assume_yes" -eq 1 ] && return 0
  if [ ! -t 0 ]; then
    warn 'No interactive terminal; pass --yes to approve installing Ollama.'
    return 1
  fi
  read -r -p "$1 [Y/n] " answer
  case "${answer:-y}" in [Yy]*) return 0 ;; *) return 1 ;; esac
}

if [ -z "$allow_from" ]; then
  echo "Name the hub's private IPv4 address, for example:  --allow-from 192.168.1.10" >&2
  exit 2
fi
valid_private_ipv4() {
  printf '%s\n' "$1" | awk -F'[.]' '
    NF != 4 { exit 1 }
    {
      for (i = 1; i <= 4; i++) {
        if ($i !~ /^(0|[1-9][0-9]{0,2})$/ || $i > 255) exit 1
      }
      if ($1 == 10 || ($1 == 172 && $2 >= 16 && $2 <= 31) || ($1 == 192 && $2 == 168)) exit 0
      exit 1
    }'
}
if ! valid_private_ipv4 "$allow_from"; then
  echo "--allow-from accepts only one literal RFC1918 private IPv4 address; CIDR, hostnames, wildcards and LocalSubnet are refused: $allow_from" >&2
  exit 2
fi
case "$port" in ''|*[!0-9]*) echo '--port must be a number from 1 to 65535.' >&2; exit 2 ;; esac
if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then echo '--port must be a number from 1 to 65535.' >&2; exit 2; fi
port=$((10#$port))

if [ "$(uname -s)" != "Linux" ]; then
  if [ "$dry_run" -eq 1 ]; then warn "This is not Linux ($(uname -s)); continuing only because of --dry-run."
  else echo "This script is for Linux. On Windows use Setup-Worker.ps1." >&2; exit 1; fi
fi
if [ "$dry_run" -eq 0 ] && [ "$(id -u)" -ne 0 ]; then
  echo "Run it with sudo:  sudo $0 $*" >&2; exit 1
fi

# Ollama has no authentication, so fail before installing or binding it unless a
# supported firewall is active and its existing policy does not already expose
# this port more broadly than the requested peer.
firewall_backend=""
firewall_zones=()
port_spec_covers() {
  local spec="$1" protocol="tcp" first last
  if [[ "$spec" == */* ]]; then protocol="${spec##*/}"; spec="${spec%%/*}"; fi
  [ "$protocol" = tcp ] || return 1
  if [[ "$spec" =~ ^([0-9]+)-([0-9]+)$ ]]; then
    first="${BASH_REMATCH[1]}"; last="${BASH_REMATCH[2]}"
    (( port >= first && port <= last ))
  else
    [ "$spec" = "$port" ]
  fi
}

if [ "$dry_run" -eq 0 ] && command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active$' && command -v firewall-cmd >/dev/null 2>&1 && [ "$(firewall-cmd --state 2>/dev/null || true)" = 'running' ]; then
  echo 'Refusing to expose Ollama: both ufw and firewalld are active, so the effective inbound policy is ambiguous.' >&2; exit 1
fi

if [ "$dry_run" -eq 0 ]; then
  if command -v ufw >/dev/null 2>&1 && ufw_output="$(ufw status verbose 2>/dev/null)" && printf '%s\n' "$ufw_output" | grep -q '^Status: active$'; then
    firewall_backend="ufw"
    default_incoming="$(printf '%s\n' "$ufw_output" | sed -n 's/^Default: \([^ ,]*\).*/\1/p')"
    case "$default_incoming" in
      deny|reject) ;;
      *) echo "Refusing to expose Ollama: ufw's default incoming policy must be deny or reject (found: ${default_incoming:-unknown})." >&2; exit 1 ;;
    esac

    ufw_conflicts="$(ufw status numbered 2>/dev/null | awk -v port="$port" -v peer="$allow_from" '
      /^\[[[:space:]]*[0-9]+\]/ {
        original=$0; rule=$0
        sub(/^\[[^]]+\][[:space:]]*/, "", rule)
        if (rule ~ ("^" port "(/tcp)?([[:space:]]|$)") && rule ~ /ALLOW IN/) {
          source=rule
          sub(/^.*ALLOW IN[[:space:]]+/, "", source)
          sub(/[[:space:]]+\(v6\).*$/, "", source)
          if (source != peer) print original
        }
      }')"
    if [ -n "$ufw_conflicts" ]; then
      printf 'Refusing to expose Ollama: existing ufw rules allow port %s from other sources:\n%s\nReview `sudo ufw status numbered` and remove or narrow those rules first.\n' "$port" "$ufw_conflicts" >&2
      exit 1
    fi
  elif command -v firewall-cmd >/dev/null 2>&1 && [ "$(firewall-cmd --state 2>/dev/null || true)" = "running" ]; then
    firewall_backend="firewalld"
    active_zones="$(firewall-cmd --get-active-zones 2>/dev/null || true)"
    while IFS= read -r zone; do
      [ -n "$zone" ] && firewall_zones+=("$zone")
    done < <(printf '%s\n' "$active_zones" | awk 'NF && $0 !~ /^[[:space:]]/ { print $1 }')
    if [ "${#firewall_zones[@]}" -eq 0 ]; then
      echo 'Refusing to expose Ollama: firewalld is running but has no active zones.' >&2; exit 1
    fi

    for zone in "${firewall_zones[@]}"; do
      zone_details="$(firewall-cmd --zone="$zone" --list-all 2>/dev/null)" || { echo "Refusing to expose Ollama: cannot inspect firewalld zone '$zone'." >&2; exit 1; }
      zone_target="$(printf '%s\n' "$zone_details" | awk '/^[[:space:]]*target:/ { print $2; exit }')"
      if [ "$zone_target" = 'ACCEPT' ]; then
        echo "Refusing to expose Ollama: active firewalld zone '$zone' has target ACCEPT." >&2; exit 1
      fi

      zone_ports="$(printf '%s\n' "$zone_details" | sed -n 's/^[[:space:]]*ports:[[:space:]]*//p')"
      for spec in $zone_ports; do
        if port_spec_covers "$spec"; then
          echo "Refusing to expose Ollama: active firewalld zone '$zone' already allows $spec broadly." >&2; exit 1
        fi
      done

      zone_services="$(printf '%s\n' "$zone_details" | sed -n 's/^[[:space:]]*services:[[:space:]]*//p')"
      for service in $zone_services; do
        service_ports="$(firewall-cmd --info-service="$service" 2>/dev/null | sed -n 's/^[[:space:]]*ports:[[:space:]]*//p')"
        for spec in $service_ports; do
          if port_spec_covers "$spec"; then
            echo "Refusing to expose Ollama: firewalld service '$service' in zone '$zone' already allows $spec broadly." >&2; exit 1
          fi
        done
      done

      while IFS= read -r rule; do
        [[ "$rule" == *accept* && "$rule" == *'protocol="tcp"'* ]] || continue
        if [[ "$rule" =~ port[[:space:]]+port="([0-9]+(-[0-9]+)?)"[[:space:]]+protocol="tcp" ]]; then
          rule_port_spec="${BASH_REMATCH[1]}/tcp"
          if port_spec_covers "$rule_port_spec" && [[ "$rule" != *"source address=\"$allow_from\""* ]]; then
            echo "Refusing to expose Ollama: active firewalld zone '$zone' has a broader accepting rule: $rule" >&2; exit 1
          fi
        elif [[ "$rule" == *"port port=\"$port\""* && "$rule" != *"source address=\"$allow_from\""* ]]; then
          echo "Refusing to expose Ollama: active firewalld zone '$zone' has an accepting rule for port $port: $rule" >&2; exit 1
        fi
      done < <(printf '%s\n' "$zone_details" | sed -n '/^[[:space:]]*rich rules:/,$p' | sed '1d')
    done

    direct_conflicts="$(firewall-cmd --direct --get-all-rules 2>/dev/null | grep -E "(^|[^0-9])${port}([^0-9]|$)" || true)"
    if [ -n "$direct_conflicts" ]; then
      printf 'Refusing to expose Ollama: existing firewalld direct rules reference port %s:\n%s\nReview these rules before rerunning setup.\n' "$port" "$direct_conflicts" >&2
      exit 1
    fi
  else
    echo 'Refusing to expose Ollama: no active ufw or firewalld policy was found. Enable a supported firewall with a restrictive inbound policy, then rerun this script.' >&2
    exit 1
  fi
else
  warn 'Dry run does not inspect or change firewall state. A real run requires active ufw or firewalld with no broader allow rule for this port.'
fi

# --- 1. Ollama ------------------------------------------------------------------------------------
say "Ollama"
if command -v ollama >/dev/null 2>&1; then
  ok "Installed: $(ollama --version 2>&1 | head -n1)"
else
  if [ "$dry_run" -eq 1 ]; then
    info 'would ask before installing Ollama with the official installer (https://ollama.com/install.sh)'
  elif confirm "Ollama is not installed. Install it now with the official installer (https://ollama.com/install.sh)?"; then
    curl -fsSL https://ollama.com/install.sh | sh
  else
    echo "Ollama is required." >&2; exit 1
  fi
fi

# --- 2. listen on the network ---------------------------------------------------------------------
say "Making Ollama listen on the network"
if command -v systemctl >/dev/null 2>&1 && { [ "$dry_run" -eq 1 ] || systemctl list-unit-files ollama.service >/dev/null 2>&1; }; then
  dropin_dir=/etc/systemd/system/ollama.service.d
  dropin="$dropin_dir/fleet.conf"
  if [ "$dry_run" -eq 1 ]; then
    info "would write $dropin with: Environment=\"OLLAMA_HOST=0.0.0.0:$port\""
    info "would run: systemctl daemon-reload && systemctl restart ollama"
  else
    mkdir -p "$dropin_dir"
    printf '[Service]\nEnvironment="OLLAMA_HOST=0.0.0.0:%s"\n' "$port" > "$dropin"
    systemctl daemon-reload
    systemctl restart ollama
    ok "Ollama restarted, listening on 0.0.0.0:$port"
  fi
else
  warn "No systemd ollama.service found. Start Ollama yourself with: OLLAMA_HOST=0.0.0.0:$port ollama serve"
fi

# --- 3. firewall ----------------------------------------------------------------------------------
say "Firewall: allow $allow_from to reach port $port"
if [ "$dry_run" -eq 1 ]; then
  info "would add a TCP $port rule scoped to $allow_from after firewall preflight"
elif [ "$firewall_backend" = 'ufw' ]; then
  ufw allow from "$allow_from" to any port "$port" proto tcp comment 'Agent Fleet: Ollama from the hub'
  ok "ufw rule added"
elif [ "$firewall_backend" = 'firewalld' ]; then
  for zone in "${firewall_zones[@]}"; do
    firewall-cmd --permanent --zone="$zone" --add-rich-rule="rule family=ipv4 source address=$allow_from port port=$port protocol=tcp accept"
  done
  firewall-cmd --reload
  ok "firewalld rule added"
else
  echo 'No supported active firewall was selected; refusing to continue.' >&2; exit 1
fi

# --- 4. Wi-Fi power saving ------------------------------------------------------------------------
if [ "$wifi_off" -eq 1 ]; then
  say "Wi-Fi power saving"
  if command -v iw >/dev/null 2>&1; then
    for dev in $(iw dev 2>/dev/null | awk '$1=="Interface"{print $2}'); do
      run iw dev "$dev" set power_save off
      ok "power saving off on $dev (until reboot)"
    done
  fi
  if [ -d /etc/NetworkManager ]; then
    if [ "$dry_run" -eq 1 ]; then info "would write /etc/NetworkManager/conf.d/wifi-powersave-off.conf"
    else
      mkdir -p /etc/NetworkManager/conf.d
      printf '[connection]\nwifi.powersave = 2\n' > /etc/NetworkManager/conf.d/wifi-powersave-off.conf
      ok "made permanent through NetworkManager (takes effect on the next reconnect or reboot)"
    fi
  else
    warn "NetworkManager not found; the setting will not survive a reboot. A wired connection avoids the problem."
  fi
fi

# --- 5. check -------------------------------------------------------------------------------------
say "Check"
addr="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
if [ "$dry_run" -eq 1 ]; then info "dry run: nothing was changed."
elif curl -fsS --max-time 5 "http://${addr:-127.0.0.1}:$port/api/tags" >/dev/null 2>&1; then
  ok "Ollama answers on http://${addr:-127.0.0.1}:$port"
else
  warn "Ollama did not answer on http://${addr:-127.0.0.1}:$port. Check: systemctl status ollama"
fi

printf '\nNext, on the HUB (replace worker1 and the model with your choices):\n'
printf '  .\\scripts\\Add-FleetNode.ps1 -Name worker1 -Address %s -Model qwen2.5-coder:7b -Tier standard -Pull\n' "${addr:-<this-machine-address>}"
info "A fixed address for this machine (a DHCP reservation on your router) avoids surprises later."
info "A wired connection is more reliable than Wi-Fi for a machine that should be on all night."
