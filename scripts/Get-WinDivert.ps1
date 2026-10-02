# Downloads official WinDivert 2.2.2-A (LGPL). See third_party/windivert/LICENSE.txt.
#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dest = Join-Path $root "third_party\windivert"
$dll = Join-Path $dest "WinDivert.dll"
$sys = Join-Path $dest "WinDivert64.sys"
if ((Test-Path $dll) -and (Test-Path $sys)) {
    return
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$zip = Join-Path $env:TEMP "WinDivert-2.2.2-A.zip"
$url = "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip"
$downloaded = $false
foreach ($proxy in @("", "http://127.0.0.1:7890")) {
    try {
        if ([string]::IsNullOrWhiteSpace($proxy)) {
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        }
        else {
            Invoke-WebRequest -Uri $url -OutFile $zip -Proxy $proxy -UseBasicParsing
        }
        $downloaded = $true
        break
    }
    catch {
        Write-Host ("download failed via '" + $proxy + "': " + $_.Exception.Message)
    }
}
if (-not $downloaded) {
    throw "Could not download WinDivert. Place WinDivert.dll and WinDivert64.sys in third_party\windivert and rebuild."
}

$extract = Join-Path $env:TEMP "WinDivert-222"
if (Test-Path $extract) {
    Remove-Item -Recurse -Force $extract
}
Expand-Archive -Path $zip -DestinationPath $extract -Force
Copy-Item (Join-Path $extract "WinDivert-2.2.2-A\x64\WinDivert.dll") $dll -Force
Copy-Item (Join-Path $extract "WinDivert-2.2.2-A\x64\WinDivert64.sys") $sys -Force
$license = Join-Path $extract "WinDivert-2.2.2-A\LICENSE"
if (Test-Path $license) {
    Copy-Item $license (Join-Path $dest "LICENSE.txt") -Force
}
Write-Host ("WinDivert ready: " + $dest)
