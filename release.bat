@echo off
rem teamradio56 release (maintainer only) - double-click me.
rem Runs release.ps1 bypassing the PowerShell execution policy (this run only).
rem Options pass through, e.g.  release.bat -AudioDir "D:\packs" -Notes "summary"
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
