@echo off
setlocal
cd /d "%~dp0"
title MemberManager Build

echo ============================================
echo   MemberManager one-click build
echo   (compiler toolchain bundled, no VS needed)
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\compile-inprocess.ps1"
if errorlevel 1 goto :fail

echo.
echo ============================================
echo   [OK] Build success.
echo   Program: beta\MemberManager.exe
echo   Double-click to run. First run auto-creates
echo   the database, no installation needed.
echo ============================================
if not defined CI pause
exit /b 0

:fail
echo.
echo [FAIL] Build failed. See messages above.
echo Send the full window content (screenshot) to the developer AI.
if not defined CI pause
exit /b 1
