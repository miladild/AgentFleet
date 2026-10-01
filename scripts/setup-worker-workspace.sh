#!/usr/bin/env bash
set -euo pipefail

user="${1:-agentfleet}"
public_key="${2:-}"
workspace_root="${3:-/home/${user}/workspaces}"
dry_run=0
[[ "${4:-}" == "--dry-run" ]] && dry_run=1

if [[ $EUID -ne 0 && $dry_run -eq 0 ]]; then echo 'Run with sudo on the worker, or pass --dry-run to preview.' >&2; exit 1; fi
[[ "$user" =~ ^[a-zA-Z0-9_.-]{1,32}$ ]] || { echo 'Use a simple local account name.' >&2; exit 2; }
[[ "$public_key" =~ ^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp[0-9]+)[[:space:]]+[^[:space:]]+ ]] || {
  echo 'Pass one complete SSH public key as the second argument; never pass the private key.' >&2; exit 2;
}
[[ "$workspace_root" = /* ]] || { echo 'Workspace root must be an absolute Linux path.' >&2; exit 2; }
if [[ $dry_run -eq 0 ]]; then
  systemctl is-active --quiet ssh || { echo 'OpenSSH Server (sshd) must already be running; this script does not alter services or firewall rules.' >&2; exit 1; }
fi

user_exists=0
id "$user" >/dev/null 2>&1 && user_exists=1
home="$(getent passwd "$user" | cut -d: -f6 || true)"
[[ -n "$home" ]] || home="/home/$user"
home="$(realpath -m -- "$home")"
workspace_root="$(realpath -m -- "$workspace_root")"
[[ "$workspace_root" == "$home"/* ]] || {
  echo 'Keep the worker workspace root strictly under this dedicated account home directory.' >&2; exit 2;
}

if [[ $dry_run -eq 1 ]]; then
  action='create'
  [[ $user_exists -eq 1 ]] && action='secure existing'
  printf "Would %s a standard account '%s', install its public key, and grant workspace access to %s.\n" "$action" "$user" "$workspace_root"
  echo 'No files, accounts, services, or firewall rules were changed.'
  exit 0
fi

if [[ $user_exists -eq 0 ]]; then
  command -v openssl >/dev/null 2>&1 || { echo 'Install openssl first; it is needed to create an unguessable account password.' >&2; exit 1; }
  random_password="$(openssl rand -base64 48)"
  password_hash="$(printf '%s' "$random_password" | openssl passwd -6 -stdin)"
  unset random_password
  useradd --create-home --user-group --shell /bin/bash --password "$password_hash" "$user"
  unset password_hash
fi

uid="$(id -u "$user")"
groups="$(id -nG "$user")"
if [[ "$uid" == 0 || " $groups " =~ [[:space:]](sudo|wheel|docker|lxd|disk)[[:space:]] ]]; then
  echo "Refusing '$user': worker SSH accounts cannot be root or belong to sudo/wheel/docker/lxd/disk." >&2
  exit 1
fi
if command -v sudo >/dev/null 2>&1 && runuser -u "$user" -- sudo -n true >/dev/null 2>&1; then
  echo "Refusing '$user': it has passwordless sudo access." >&2
  exit 1
fi

home="$(getent passwd "$user" | cut -d: -f6)"
[[ -n "$home" ]] || { echo 'Could not resolve the dedicated account home directory.' >&2; exit 2; }
home="$(realpath -m -- "$home")"
workspace_root="$(realpath -m -- "$workspace_root")"
[[ "$workspace_root" == "$home"/* ]] || {
  echo 'Keep the worker workspace root under this dedicated account home directory.' >&2; exit 2;
}
install -d -m 0700 -o "$user" -g "$user" "$home" "$home/.ssh" "$workspace_root"
authorized_keys="$home/.ssh/authorized_keys"
touch "$authorized_keys"
chmod 0600 "$authorized_keys"
chown "$user:$user" "$home/.ssh" "$authorized_keys"
if ! grep -qxF -- "$public_key" "$authorized_keys"; then
  printf '%s\n' "$public_key" >> "$authorized_keys"
fi
chown "$user:$user" "$authorized_keys"
chmod 0700 "$home/.ssh"
chmod 0700 "$workspace_root"

printf "Ready: standard account '%s' can SSH with the supplied public key and write under %s.\n" "$user" "$workspace_root"
echo 'Configure the matching SSH key path and verified SHA256 host fingerprint in Fleet > Config > Machines.'
