@echo off
chcp 65001 >nul
if not exist "%~dp0publish\win-x64\TTPlayerSerialAss.exe" (
  echo 请先运行 tools\build.ps1 生成程序。
  pause
  exit /b 1
)
start "" "%~dp0publish\win-x64\TTPlayerSerialAss.exe"
