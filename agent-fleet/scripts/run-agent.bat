@echo off
rem Development run: the backend on http://localhost:8000. Its settings, conversations, durable record and plans live in
rem agent-fleet\ (fleet.config.json) and agent-fleet\data\ so they survive rebuilds and are easy to find.
cd /d "%~dp0\..\agent"
if not defined FLEET_CONFIG_PATH set "FLEET_CONFIG_PATH=%~dp0..\fleet.config.json"
if not defined FLEET_SESSIONS_DIR set "FLEET_SESSIONS_DIR=%~dp0..\data\sessions"
if not defined FLEET_CONTEXTS_DIR set "FLEET_CONTEXTS_DIR=%~dp0..\data\contexts"
if not defined FLEET_PLANS_DIR set "FLEET_PLANS_DIR=%~dp0..\data\plans"
echo Starting the Agent Fleet backend on http://localhost:8000...
set ASPNETCORE_URLS=http://localhost:8000
dotnet run
