@echo off
setlocal

rem Installs (or reinstalls) the logon scheduled task for KeyTrace.
rem Must run as administrator - the script re-launches itself elevated.
rem
rem Why a scheduled task instead of the Startup folder:
rem   1. RunLevel Highest - otherwise keystrokes in elevated windows are missed (UIPI)
rem   2. No execution time limit - schtasks defaults to killing long-running tasks
rem
rem Assumes the repo lives at %USERPROFILE%\keytrace and is built with:
rem   dotnet build -c Release

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo  KeyTrace - install logon task
echo  -----------------------------
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe=(Join-Path $env:USERPROFILE 'keytrace\src\KeyTrace\bin\Release\net8.0-windows\keytrace.exe'); if(-not (Test-Path $exe)){ Write-Host '[ERROR] keytrace.exe not found. Run: dotnet build -c Release'; exit 1 }; Unregister-ScheduledTask -TaskName 'KeyTrace' -Confirm:$false -ErrorAction SilentlyContinue; $a=New-ScheduledTaskAction -Execute $exe -Argument 'run'; $t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); $p=New-ScheduledTaskPrincipal -UserId ('{0}\{1}' -f $env:USERDOMAIN,$env:USERNAME) -LogonType Interactive -RunLevel Highest; Register-ScheduledTask -TaskName 'KeyTrace' -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null; if(-not (Get-ScheduledTask -TaskName 'KeyTrace' -ErrorAction SilentlyContinue)){ Write-Host '[ERROR] registration failed'; exit 1 }; Write-Host '[OK] task created'; Write-Host '  trigger   : at logon'; Write-Host '  run level : highest'; Write-Host '  time limit: none'; Start-ScheduledTask -TaskName 'KeyTrace'; Write-Host '[OK] started - look for a green dot in the tray'"

echo.
echo To remove the task later, run as administrator:
echo   schtasks /Delete /TN "KeyTrace" /F
echo.
pause
