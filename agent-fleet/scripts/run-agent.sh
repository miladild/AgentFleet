#!/bin/bash
# Development run: the backend on http://localhost:8000. Its settings, conversations, durable record and plans live in
# agent-fleet/ (fleet.config.json) and agent-fleet/data/ so they survive rebuilds and are easy to find.
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root/agent" || exit 1
export FLEET_CONFIG_PATH="${FLEET_CONFIG_PATH:-$root/fleet.config.json}"
export FLEET_SESSIONS_DIR="${FLEET_SESSIONS_DIR:-$root/data/sessions}"
export FLEET_CONTEXTS_DIR="${FLEET_CONTEXTS_DIR:-$root/data/contexts}"
export FLEET_PLANS_DIR="${FLEET_PLANS_DIR:-$root/data/plans}"
echo "Starting the Agent Fleet backend on http://localhost:8000..."
ASPNETCORE_URLS="http://localhost:8000" dotnet run
