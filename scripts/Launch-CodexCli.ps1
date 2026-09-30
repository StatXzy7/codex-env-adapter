#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Timezone,
    [switch]$NoLaunch,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CodexArgs
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$module = Join-Path $PSScriptRoot "CodexEnv.psm1"
Import-Module -Force -DisableNameChecking $module

Write-Host "正在探测当前出口节点..."
$node = Get-CodexNodeEnvironment
$cli = Get-CodexCliCommand
$osTz = Get-WindowsTimezoneInfo

if ([string]::IsNullOrWhiteSpace($Timezone)) {
    $Timezone = $node.Timezone
}

$settingsPath = Save-CodexEnvSettings -Node $node -Timezone $Timezone

Write-Host ""
Write-Host ("系统时区     : {0}（不修改）" -f $osTz.Id)
Write-Host ("出口 IP      : {0}" -f $node.PublicIp)
Write-Host ("节点位置     : {0}, {1}, {2}" -f $node.City, $node.Region, $node.Country)
Write-Host ("注入 TZ      : {0}" -f $Timezone)
Write-Host ("Codex CLI    : {0}" -f $cli.Source)
Write-Host ("设置文件     : {0}" -f $settingsPath)
Write-Host ""
Write-Host "只对当前这个终端窗口有效。关掉窗口后再开普通 PowerShell，TZ 不会还在。"
Write-Host "不要把 TZ 写进用户环境变量。系统时区也不会改。"
Write-Host ""

if ($NoLaunch) { return }

$env:TZ = $Timezone
$proxyUrl = $null
$settingsFile = Join-Path $env:LOCALAPPDATA "CodexEnvAdapter\settings.json"
$proxyEnabled = $true
$proxyServer = "127.0.0.1:7890"
if (Test-Path -LiteralPath $settingsFile) {
    try {
        $saved = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $saved.appProxyEnabled) { $proxyEnabled = [bool]$saved.appProxyEnabled }
        if ($saved.appProxyServer) { $proxyServer = [string]$saved.appProxyServer }
    } catch { }
}
foreach ($name in @("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy")) {
    Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
}
if ($proxyEnabled) {
    if ($proxyServer -match '^[A-Za-z0-9._-]{1,253}:\d{1,5}$') {
        $proxyUrl = "http://$proxyServer"
        $env:HTTP_PROXY = $proxyUrl
        $env:HTTPS_PROXY = $proxyUrl
        $env:ALL_PROXY = $proxyUrl
        $env:NO_PROXY = "localhost,127.0.0.1,::1"
        Write-Host ("应用代理     : {0}（只在这个窗口里）" -f $proxyUrl)
    } else {
        throw "应用代理地址无效：$proxyServer。请在启动器里改成 127.0.0.1:7890 这种格式。"
    }
} else {
    Write-Host "应用代理     : 关闭（这个窗口直连）"
}
if ($null -eq $CodexArgs) {
    $CodexArgs = @()
}

if ($cli.Kind -eq "Path") {
    & $cli.Command @CodexArgs
} else {
    & codex @CodexArgs
}
