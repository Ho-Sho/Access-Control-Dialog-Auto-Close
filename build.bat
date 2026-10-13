@echo off
rem Build AccessControlDialogAutoClose.exe with the C# compiler bundled in Windows.
rem Put this file and AccessControlDialogAutoClose.cs in the same folder, then double-click.
cd /d "%~dp0"

set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo csc.exe was not found. Please check that .NET Framework 4.x is installed.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /win32icon:AccessControlDialogAutoClose.ico /win32manifest:app.manifest /resource:AccessControlDialogAutoClose.ico,app.ico /out:AccessControlDialogAutoClose.exe AccessControlDialogAutoClose.cs

if errorlevel 1 (
    echo Build FAILED.
) else (
    echo Build OK: AccessControlDialogAutoClose.exe
)
pause
