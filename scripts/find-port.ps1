# Print the COM port of the ESP32 board (CH340 / CP210x USB serial), or nothing.
$d = Get-CimInstance Win32_PnPEntity |
    Where-Object { $_.Name -match 'CH34|CP210|USB.?Serial|UART|USB-SERIAL' -and $_.Name -match '\(COM\d+\)' } |
    Select-Object -First 1
if ($d) { [regex]::Match($d.Name, 'COM\d+').Value }
