@echo off
rem Same as run.ps1, but works even when PowerShell scripts are blocked by the execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
