@echo off
rem UxPlay portable launcher with window auto-resize on iPhone rotation
set "ROOT=%~dp0"
set "PATH=%ROOT%bin;%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\WindowsPowerShell\v1.0"
set "GST_PLUGIN_SYSTEM_PATH=%ROOT%lib\gstreamer-1.0"
set "GST_PLUGIN_PATH="
set "GST_PLUGIN_SCANNER=%ROOT%libexec\gstreamer-1.0\gst-plugin-scanner.exe"
set "GST_REGISTRY=%LOCALAPPDATA%\UxPlay-Portable\registry.bin"
start "" /b powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%ROOT%uxplay-autoresize.ps1"
rem -fps 60: smoother pointer when operating the iPhone from the PC (default is 30)
rem audio/video sync stays on (default); "-vsync no" cut display delay but made audio lag behind
"%ROOT%bin\uxplay.exe" -n "UxPlay-%COMPUTERNAME%" -nh -fps 60 %*
pause
