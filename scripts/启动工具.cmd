@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "ESP32小船烧录工具.exe" (
    echo.
    echo 没有找到 ESP32小船烧录工具.exe。
    echo 请把整个 ZIP 完整解压后再运行，不要在压缩包里直接双击。
    echo.
    pause
    exit /b 1
)
start "" "ESP32小船烧录工具.exe"
