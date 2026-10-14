@echo off
rem Opens Agent Fleet through the tray when included; older downloads use the console starter.
if exist "%~dp0scripts\tray\Fleet-Tray.ps1" (
  start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "%~dp0scripts\tray\Fleet-Tray.ps1" --open
  exit /b
)
title Agent Fleet
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Fleet.ps1" %*
if errorlevel 1 (
  echo.
  echo Agent Fleet did not start. The message above says why.
  pause
)
