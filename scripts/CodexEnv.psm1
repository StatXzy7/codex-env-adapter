#Requires -Version 5.1
Set-StrictMode -Version Latest

function Get-OptionalProperty {
    param(
        $InputObject,
        [string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $prop = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Get-CodexNodeEnvironment {
    [CmdletBinding()]
    param()

    $endpoints = @(
        @{ Url = "https://ipinfo.io/json"; Kind = "ipinfo" },
        @{ Url = "https://ipapi.co/json/"; Kind = "ipapi" }
    )

    $errors = New-Object System.Collections.Generic.List[string]
    foreach ($item in $endpoints) {
        try {
            $info = Invoke-RestMethod -Uri $item.Url -TimeoutSec 12
            $tz = [string](Get-OptionalProperty $info "timezone")
            if ([string]::IsNullOrWhiteSpace($tz)) { continue }

            $country = Get-OptionalProperty $info "country"
            if ([string]::IsNullOrWhiteSpace([string]$country)) {
                $country = Get-OptionalProperty $info "country_code"
            }

            return [pscustomobject]@{
                PublicIp   = [string](Get-OptionalProperty $info "ip")
                City       = [string](Get-OptionalProperty $info "city")
                Region     = [string](Get-OptionalProperty $info "region")
                Country    = [string]$country
                Org        = [string](Get-OptionalProperty $info "org")
                Timezone   = $tz
                Source     = $item.Url
                DetectedAt = (Get-Date).ToString("s")
            }
        } catch {
            $errors.Add("$($item.Url): $($_.Exception.Message)") | Out-Null
        }
    }

    throw "无法探测出口节点。$($errors -join ' | ')"
}

function Get-WindowsProxyStatus {
    [CmdletBinding()]
    param()

    $inet = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    $winhttp = (netsh winhttp show proxy | Out-String).Trim()
    $proxyEnable = Get-OptionalProperty $inet "ProxyEnable"

    [pscustomobject]@{
        UserProxyEnable   = [bool]$proxyEnable
        UserProxyServer   = [string](Get-OptionalProperty $inet "ProxyServer")
        UserProxyOverride = [string](Get-OptionalProperty $inet "ProxyOverride")
        WinHttp           = $winhttp
        ProcessHttpProxy  = [string]$env:HTTP_PROXY
        ProcessHttpsProxy = [string]$env:HTTPS_PROXY
        ProcessAllProxy   = [string]$env:ALL_PROXY
        ProcessNoProxy    = [string]$env:NO_PROXY
    }
}

function Get-ChatGPTInstall {
    [CmdletBinding()]
    param()

    $pkg = Get-AppxPackage OpenAI.Codex | Select-Object -First 1
    if (-not $pkg) {
        throw "未找到已安装的 ChatGPT / Codex 桌面应用（包名 OpenAI.Codex）。"
    }

    $exe = Join-Path $pkg.InstallLocation "app\ChatGPT.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "未找到 ChatGPT.exe: $exe"
    }

    [pscustomobject]@{
        Name            = $pkg.Name
        Version         = [string]$pkg.Version
        PackageFullName = $pkg.PackageFullName
        InstallLocation = $pkg.InstallLocation
        Exe             = $exe
        RunningCount    = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue).Count
    }
}

function Get-WindowsTimezoneInfo {
    $tz = Get-TimeZone
    [pscustomobject]@{
        Id            = $tz.Id
        DisplayName   = $tz.DisplayName
        BaseUtcOffset = $tz.BaseUtcOffset.ToString()
        Tzutil        = (tzutil /g)
        ProcessTz     = [string]$env:TZ
        UserTz        = [string][Environment]::GetEnvironmentVariable("TZ", "User")
        MachineTz     = [string][Environment]::GetEnvironmentVariable("TZ", "Machine")
    }
}

function Get-CodexEnvironmentReport {
    [CmdletBinding()]
    param()

    $node = Get-CodexNodeEnvironment
    $osTz = Get-WindowsTimezoneInfo
    $proxy = Get-WindowsProxyStatus
    $app = $null
    $appError = $null
    try {
        $app = Get-ChatGPTInstall
    } catch {
        $appError = $_.Exception.Message
    }

    $aligned = $false
    if ($node -and $osTz) {
        $aligned = ($osTz.ProcessTz -eq $node.Timezone)
    }

    [pscustomobject]@{
        Node          = $node
        WindowsTz     = $osTz
        Proxy         = $proxy
        ChatGPT       = $app
        ChatGPTError  = $appError
        NodeTimezone  = $node.Timezone
        NeedsTzInject = -not $aligned
        Advice        = @(
            "ChatGPT 桌面端请用本仓库启动器启动，不要从开始菜单直接打开。"
            "换节点后先确认出口 IP，再重新运行启动器。"
            "不要修改 Windows 系统时区；只给 ChatGPT 进程注入 TZ。"
        )
    }
}

function Stop-ChatGPTProcesses {
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 15
    )

    $procs = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    $procs | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 400
        $left = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue)
    } while ($left.Count -gt 0 -and (Get-Date) -lt $deadline)

    if (@(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "ChatGPT 未能完全退出。请先点托盘图标退出后再试。"
    }
}

function Start-ChatGPTAligned {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Exe,

        [Parameter(Mandatory = $true)]
        [string]$Timezone
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path -Parent $Exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $null = $psi.EnvironmentVariables["TZ"]
    $psi.EnvironmentVariables["TZ"] = $Timezone
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc) {
        throw "启动 ChatGPT 失败。"
    }
    return $proc
}

function Save-CodexEnvSettings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Node,

        [Parameter(Mandatory = $true)]
        [string]$Timezone
    )

    $dir = Join-Path $env:LOCALAPPDATA "CodexEnvAdapter"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "settings.json"
    $payload = @{
        timezone    = $Timezone
        publicIp    = $Node.PublicIp
        city        = $Node.City
        region      = $Node.Region
        country     = $Node.Country
        org         = $Node.Org
        source      = $Node.Source
        systemTz    = (Get-TimeZone).Id
        updatedAt   = (Get-Date).ToString("s")
    } | ConvertTo-Json
    Set-Content -Path $path -Value $payload -Encoding UTF8
    return $path
}

Export-ModuleMember -Function @(
    "Get-CodexNodeEnvironment",
    "Get-WindowsProxyStatus",
    "Get-ChatGPTInstall",
    "Get-WindowsTimezoneInfo",
    "Get-CodexEnvironmentReport",
    "Stop-ChatGPTProcesses",
    "Start-ChatGPTAligned",
    "Save-CodexEnvSettings"
)
