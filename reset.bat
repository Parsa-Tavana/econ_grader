@echo off
rem Double-click launcher for EconGrader FULL RESET (destroys data!)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0reset.ps1"
pause
