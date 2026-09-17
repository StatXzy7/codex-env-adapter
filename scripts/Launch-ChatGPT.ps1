#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Timezone,
    [switch]$KeepRunning,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$module = Join-Path $PSScriptRoot "CodexEnv.psm1"
Import-Module -Force -DisableNameChecking $module

Write-Host "正在探测当前出口节点..."
$node = Get-CodexNodeEnvironment
$app = Get-ChatGPTInstall
$osTz = Get-WindowsTimezoneInfo
$proxy = Get-WindowsProxyStatus

if ([string]::IsNullOrWhiteSpace($Timezone)) {
    $Timezone = $node.Timezone
}

$settingsPath = Save-CodexEnvSettings -Node $node -Timezone $Timezone

Write-Host ""
Write-Host ("系统时区     : {0}" -f $osTz.Id)
Write-Host ("出口 IP      : {0}" -f $node.PublicIp)
Write-Host ("节点位置     : {0}, {1}, {2}" -f $node.City, $node.Region, $node.Country)
Write-Host ("注入 TZ      : {0}" -f $Timezone)
Write-Host ("用户代理     : enable={0} server={1}" -f $proxy.UserProxyEnable, $proxy.UserProxyServer)
Write-Host ("ChatGPT 版本 : {0}" -f $app.Version)
Write-Host ("可执行文件   : {0}" -f $app.Exe)
Write-Host ("设置文件     : {0}" -f $settingsPath)
Write-Host ""

if (-not $proxy.UserProxyEnable -and [string]::IsNullOrWhiteSpace($proxy.ProcessHttpsProxy) -and [string]::IsNullOrWhiteSpace($proxy.ProcessAllProxy)) {
    Write-Host "警告: 当前看不到用户代理。若你本来就走系统代理访问 GPT，请先打开 Clash / 代理客户端。"
}

if ($NoLaunch) { return }

if ($KeepRunning -and $app.RunningCount -gt 0) {
    throw "ChatGPT 正在运行。TZ 只在进程启动时生效，请先退出后再启动。"
}

if ($app.RunningCount -gt 0) {
    Write-Host "正在结束已运行的 ChatGPT，以便注入新时区..."
    Stop-ChatGPTProcesses
}

$proc = Start-ChatGPTAligned -Exe $app.Exe -Timezone $Timezone
Start-Sleep -Seconds 2
$alive = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue)
if ($alive.Count -eq 0) {
    throw "ChatGPT 启动后没有保持运行。"
}

Write-Host ("已用 TZ={0} 启动 ChatGPT（PID {1}）。" -f $Timezone, $proc.Id)
Write-Host "Windows 系统时区未改动。"
