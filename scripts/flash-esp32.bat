@echo off
cd /d "%~dp0"
echo ESP32 にマウス用プログラムを書き込みます。
echo ESP32 ボードを USB ケーブルで PC につないでから、何かキーを押してください。
pause >nul

rem ESP32 の COM ポート (CH340 / CP210x などの USB シリアル) を探す
set "PORT="
for /f "usebackq delims=" %%p in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0find-port.ps1"`) do set "PORT=%%p"

if "%PORT%"=="" (
  echo.
  echo ESP32 が見つかりませんでした。
  echo  - USB ケーブルが「データ通信対応」か確認してください（充電専用は不可）
  echo  - ドライバが必要な場合があります（はじめにお読みください.txt の「ESP32 のドライバ」）
  pause
  exit /b 1
)

echo %PORT% に書き込みます...
"%~dp0esptool.exe" --chip esp32 --port %PORT% --baud 460800 write-flash 0x0 "%~dp0esp32-mouse.bin"
if errorlevel 1 (
  echo.
  echo 書き込みに失敗しました。ボードの「BOOT」ボタンを押したまま、もう一度実行してみてください。
  pause
  exit /b 1
)
echo.
echo 書き込みが完了しました。iPhone の Bluetooth 設定に「ESP32 Mouse」が出てきます。
pause
