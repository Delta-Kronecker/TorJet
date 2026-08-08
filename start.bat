@echo off
rem Portable Tor - always launches with the portable torrc next to tor.exe.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\launcher.ps1"
