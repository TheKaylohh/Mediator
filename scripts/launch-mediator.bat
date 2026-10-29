@echo off
rem ---------------------------------------------------------------------------------------------------------
rem  Mediator launcher (V8). Double-click it: starts the tray app from this folder. Plain cmd, so it works
rem  whatever the PowerShell execution policy is, and needs no administrator rights.
rem    launch.bat            start Mediator (Ctrl+Shift+Space opens the panel)
rem    launch.bat doctor     check the installation in this console window
rem  Published into dist\Mediator\ as launch.bat by scripts\publish-release.ps1.
rem  No parenthesised blocks: a folder such as "C:\Tools (x86)\Mediator" would end them early.
rem ---------------------------------------------------------------------------------------------------------
setlocal EnableExtensions
set "HERE=%~dp0"

if not exist "%HERE%Mediator.exe" goto missing
if /I "%~1"=="doctor" goto doctor

rem "start" returns at once, so this window closes and the app keeps running in the tray. A second launch is
rem harmless: the app is single-instance and says it is already running.
start "" /D "%HERE%." "%HERE%Mediator.exe"
exit /b 0

:doctor
"%HERE%mediator-cli.exe" doctor
echo.
pause
exit /b 0

:missing
echo Mediator.exe was not found next to this launcher:
echo   %HERE%
echo Run it from the Mediator folder (dist\Mediator), or extract the package again.
pause
exit /b 1
