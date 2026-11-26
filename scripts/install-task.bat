@echo off
setlocal

rem Installs (or reinstalls) the KeyTrace logon task.
rem Re-launches itself elevated when needed.
rem
rem Why a scheduled task instead of the Startup folder:
rem   1. RunLevel Highest - otherwise keystrokes in elevated windows are missed (UIPI)
rem   2. No execution time limit - schtasks defaults to killing long-running tasks
rem
rem The task points at %LOCALAPPDATA%\KeyTrace, NOT at bin\Release.
rem Reason: bin\Release gets wiped by dotnet clean / rebuilt by every build,
rem so a task pointing there breaks on the first clean. Confirmed by a
rem real failure: task returned 0x80070002 (file not found) after a rebuild.

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "DEST=%LOCALAPPDATA%\KeyTrace"
for %%I in ("%~dp0..") do set "REPO=%%~fI"

echo.
echo  KeyTrace - install / update logon task
echo  ----------------------------------------
echo.

echo [1/3] stopping running instance (if any)
if exist "%DEST%\keytrace.exe" (
    "%DEST%\keytrace.exe" stop >nul 2>&1
    timeout /t 2 /nobreak >nul
)
powershell -NoProfile -Command "Get-Process -Name keytrace -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue" >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/3] installing keytrace.exe to %DEST%
if exist "%REPO%\src\KeyTrace\KeyTrace.csproj" goto install_from_source
if exist "%REPO%\keytrace.exe" goto install_from_release
echo.
echo [ERROR] neither src\KeyTrace\KeyTrace.csproj nor keytrace.exe found at %REPO%.
echo.
pause
exit /b 1

:install_from_source
dotnet publish "%REPO%\src\KeyTrace" -c Release -o "%DEST%"
if errorlevel 1 (
    echo.
    echo [ERROR] publish failed.
    echo   Check that the .NET SDK is installed: dotnet --list-sdks
    echo   Check that the repo is at %REPO%
    echo.
    pause
    exit /b 1
)
goto after_install

:install_from_release
if not exist "%DEST%" mkdir "%DEST%"
if /I "%REPO%"=="%DEST%" goto after_install
copy /Y "%REPO%\keytrace.exe" "%DEST%\keytrace.exe" >nul
if errorlevel 1 (
    echo.
    echo [ERROR] failed to copy %REPO%\keytrace.exe to %DEST%.
    echo.
    pause
    exit /b 1
)

:after_install
if not exist "%DEST%\keytrace.exe" (
    echo.
    echo [ERROR] keytrace.exe missing after install.
    echo.
    pause
    exit /b 1
)

echo [3/3] registering and starting logon task
powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe='%DEST%\keytrace.exe'; Unregister-ScheduledTask -TaskName 'KeyTrace' -Confirm:$false -ErrorAction SilentlyContinue; $a=New-ScheduledTaskAction -Execute $exe -Argument 'run'; $t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); $p=New-ScheduledTaskPrincipal -UserId ('{0}\{1}' -f $env:USERDOMAIN,$env:USERNAME) -LogonType Interactive -RunLevel Highest; Register-ScheduledTask -TaskName 'KeyTrace' -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null; if(-not (Get-ScheduledTask -TaskName 'KeyTrace' -ErrorAction SilentlyContinue)){ Write-Host '[ERROR] registration failed'; exit 1 }; Write-Host '[OK] task registered'; Write-Host '  exe       : ' $exe; Write-Host '  trigger   : at logon'; Write-Host '  run level : highest'; Write-Host '  time limit: none'; Start-ScheduledTask -TaskName 'KeyTrace'; Start-Sleep -Seconds 3; if(Get-Process -Name keytrace -ErrorAction SilentlyContinue){ Write-Host '[OK] running - KeyTrace icon in the tray' } else { Write-Host '[WARN] not running - check Task Scheduler' }"

echo.
echo To remove the task later, run as administrator:
echo   schtasks /Delete /TN "KeyTrace" /F
echo.
pause
