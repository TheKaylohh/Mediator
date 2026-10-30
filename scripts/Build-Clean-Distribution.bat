@echo off
rem ---------------------------------------------------------------------------------------------------------
rem  Builds a clean Mediator distribution in dist\Mediator\ (framework-dependent, normal .NET bridge).
rem  Double-click it, or run it from cmd. Needs the .NET 8 SDK; no administrator rights.
rem    1. deletes dist\ and every bin\ and obj\ folder under src\ (nothing stale can end up in the package)
rem    2. runs scripts\publish-release.ps1 -NoAot -FrameworkDependent (extra arguments are passed on, e.g. -RunTests)
rem    3. zips dist\Mediator to dist\Mediator.zip when the build worked
rem  No parenthesised blocks: a folder such as "C:\Work (x86)\Mediator" would end them early.
rem ---------------------------------------------------------------------------------------------------------
setlocal EnableExtensions
set "HERE=%~dp0"
pushd "%HERE%.."

echo.
echo == Closing a Mediator that runs from dist\ (it would lock the files)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process Mediator,mediator-mcp-bridge,mediator-swx-worker -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\dist\*' } | Stop-Process -Force"
ping -n 3 127.0.0.1 >nul

echo.
echo == Cleaning old build output
if exist "dist" rmdir /s /q "dist"
if exist "dist" goto locked
for /d /r "src" %%D in (bin obj) do if exist "%%D" rmdir /s /q "%%D"

echo.
echo == Building the distribution
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%publish-release.ps1" -NoAot -FrameworkDependent %*
if errorlevel 1 goto failed

echo.
echo == Zipping
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\Mediator' -DestinationPath 'dist\Mediator.zip' -Force"
if errorlevel 1 goto failed

echo.
echo Done. Folder: %CD%\dist\Mediator   Zip: %CD%\dist\Mediator.zip
popd
pause
exit /b 0

:locked
echo.
echo dist\ could not be deleted: a file in it is still in use. Close Mediator and any Explorer window on dist\, then run this again.
popd
pause
exit /b 1

:failed
echo.
echo The build FAILED. Read the red error above.
popd
pause
exit /b 1
