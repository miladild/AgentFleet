@echo off
rem Starts Agent Fleet from this folder: the backend and the web UI, which opens in your browser.
rem Double-click this file. Keep the window open while you use the fleet; close it to stop the fleet.
title Agent Fleet
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Fleet.ps1" %*
if errorlevel 1 (
  echo.
  echo Agent Fleet did not start. The message above says why.
  pause
)
