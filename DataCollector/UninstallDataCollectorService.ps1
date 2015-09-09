Stop-Service "PerfMonDataCollector"
Start-Sleep -Seconds 3

$service = Get-WmiObject -Class Win32_Service -Filter "Name='PerfMonDataCollector'"
$service.delete()
