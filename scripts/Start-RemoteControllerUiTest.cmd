@echo off
setlocal EnableExtensions

set "TEST_APP=%~dp0Rc.UiTestApp.exe"
if not exist "%TEST_APP%" (
    echo [ERROR] Rc.UiTestApp.exe was not found beside this helper.
    exit /b 1
)

echo Starting the visible RemoteController UI acceptance test application...
start "RemoteController UI Acceptance Test" "%TEST_APP%"
