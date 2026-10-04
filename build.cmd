@echo off
rem One-click build of ImeCursor.exe with the .NET Framework 4.x C# compiler (C# 5).
rem Exit ImeCursor from its tray menu first: a running ImeCursor.exe cannot be overwritten.
setlocal
cd /d "%~dp0"
set "SELF=%~nx0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found - .NET Framework 4.x is required.
  call :pause_if_double_clicked
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /warn:4 ^
  /out:ImeCursor.exe /win32manifest:app.manifest ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll ^
  ImeCursor.cs Native.cs Config.cs Detector.cs Caret.cs BadgeRenderer.cs Overlay.cs TrayApp.cs
if errorlevel 1 (
  echo BUILD FAILED
  echo If the error says ImeCursor.exe is in use, exit ImeCursor from the tray menu and build again.
  call :pause_if_double_clicked
  exit /b 1
)
echo Built "%~dp0ImeCursor.exe"
exit /b 0

:pause_if_double_clicked
rem Keep the window open when started from Explorer (cmd /c "...\build.cmd"), so the errors can be read.
rem Not when run from an open console. Set BUILD_NOPAUSE=1 to never pause.
if defined BUILD_NOPAUSE goto :eof
setlocal EnableDelayedExpansion
set "CL=!cmdcmdline!"
if /i not "!CL:%SELF%=!"=="!CL!" pause
endlocal
goto :eof
