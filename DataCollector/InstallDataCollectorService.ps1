$fullpath = (Get-Item -Path ".\" -Verbose).FullName
$cred = Get-Credential -Message "This service requires domain privledges."
New-Service -Name "PerfMonDataCollector" -DisplayName "PerfMonDataCollector" -Credential $cred -StartupType Automatic -BinaryPathName $fullpath\DataCollector.exe -Description "Pulls data from performance counters and writes them to a flat file."

