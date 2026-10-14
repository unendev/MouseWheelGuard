@echo off
setlocal enabledelayedexpansion

echo ========================================================
echo   MouseWheelGuard - Native Zero-Dependency Build Tool
echo ========================================================
echo.

set CSC_PATH=""

if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set CSC_PATH="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else if exist "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set CSC_PATH="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) else (
    echo [ERROR] Could not find csc.exe in .NET Framework directory!
    pause
    exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

echo Found C# Compiler: %CSC_PATH%
echo Compiling MouseWheelGuard.cs to bin\MouseWheelGuard.exe...

%CSC_PATH% /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0bin\MouseWheelGuard.exe" "%~dp0src\MouseWheelGuard.cs"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================================
    echo   [SUCCESS] Build completed successfully!
    echo   Output: bin\MouseWheelGuard.exe
    echo ========================================================
) else (
    echo.
    echo [FAILED] Compilation failed with error code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)
