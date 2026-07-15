@echo off
setlocal EnableExtensions

set "BROKER_SERVICE=RemoteControllerBroker"
set "AGENT_SERVICE=RemoteControllerAgent"
set "UI_TASK=RemoteControllerUiAgent"
set "FAILED=0"

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Run this file from an elevated Command Prompt or PowerShell session.
    exit /b 1
)

echo Starting RemoteController services...
call :EnsureRunning "%BROKER_SERVICE%"
if errorlevel 1 set "FAILED=1"

call :EnsureRunning "%AGENT_SERVICE%"
if errorlevel 1 set "FAILED=1"

if "%FAILED%"=="0" (
    echo.
    echo [OK] RemoteController Broker and Agent are running.
) else (
    echo.
    echo [ERROR] One or more RemoteController services did not start. Their current status is shown above.
)

echo.
echo UI Agent task:
schtasks.exe /Query /TN "%UI_TASK%" /FO LIST >nul 2>&1
if errorlevel 1 (
    echo [WARN] Task "%UI_TASK%" was not found. Re-run the installer with -UiUser.
) else (
    schtasks.exe /Query /TN "%UI_TASK%" /FO LIST
)

exit /b %FAILED%

:EnsureRunning
set "SERVICE_NAME=%~1"
sc.exe query "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Service "%SERVICE_NAME%" is not installed.
    exit /b 1
)

sc.exe start "%SERVICE_NAME%" >nul 2>&1
for /L %%I in (1,1,20) do (
    sc.exe query "%SERVICE_NAME%" | findstr.exe /R /C:"STATE *: *4 " >nul
    if not errorlevel 1 (
        echo [OK] %SERVICE_NAME% is running.
        exit /b 0
    )
    timeout.exe /t 1 /nobreak >nul
)

echo [ERROR] %SERVICE_NAME% did not reach the running state within 20 seconds.
sc.exe query "%SERVICE_NAME%"
exit /b 1
