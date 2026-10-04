@echo off
rem Copies ImeCursor to a permanent, user-writable folder and starts it from there.
rem Usage: install.cmd [target folder]      default: %LOCALAPPDATA%\Programs\ImeCursor
rem Existing settings (ImeCursor.ini) in the target folder are kept.
setlocal
cd /d "%~dp0"
set "SELF=%~nx0"
set "DEST=%~1"
if "%DEST%"=="" set "DEST=%LOCALAPPDATA%\Programs\ImeCursor"
if not exist "ImeCursor.exe" (
  echo ImeCursor.exe not found - run build.cmd first.
  goto :fail
)
tasklist /fi "imagename eq ImeCursor.exe" 2>nul | find /i "ImeCursor.exe" >nul
if not errorlevel 1 (
  echo ImeCursor is running. Exit it from its tray menu first, then run install.cmd again.
  goto :fail
)
if not exist "%DEST%\" mkdir "%DEST%"
if not exist "%DEST%\" (
  echo Cannot create "%DEST%".
  goto :fail
)
copy /y "ImeCursor.exe" "%DEST%\" >nul
if errorlevel 1 (
  echo Cannot copy ImeCursor.exe to "%DEST%".
  goto :fail
)
if exist "README.md" copy /y "README.md" "%DEST%\" >nul
if exist "%DEST%\ImeCursor.ini" (
  echo Kept the existing settings file "%DEST%\ImeCursor.ini".
) else (
  if exist "ImeCursor.ini" copy /y "ImeCursor.ini" "%DEST%\" >nul
)
echo Installed to "%DEST%".
start "" "%DEST%\ImeCursor.exe"
echo Started. To start it with Windows: right-click the tray icon ^> Start with Windows.
call :pause_if_double_clicked
exit /b 0

:fail
call :pause_if_double_clicked
exit /b 1

:pause_if_double_clicked
if defined BUILD_NOPAUSE goto :eof
setlocal EnableDelayedExpansion
set "CL=!cmdcmdline!"
if /i not "!CL:%SELF%=!"=="!CL!" pause
endlocal
goto :eof
