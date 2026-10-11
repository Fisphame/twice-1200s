@echo off
rem ------------------------------------------------------------
rem  Launcher for verify.ps1.
rem  The real work lives in the PowerShell script next to this
rem  file; this wrapper only locates powershell.exe and runs it.
rem
rem  Written by hand in ASCII on purpose -- see the README section
rem  "script file encoding" for why .cmd and .ps1 have different
rem  encoding rules on Windows.
rem ------------------------------------------------------------
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify.ps1"
pause
