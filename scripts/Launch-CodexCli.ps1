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
if ($null -eq $CodexArgs) {
    $CodexArgs = @()
}

if ($cli.Kind -eq "Path") {
    & $cli.Command @CodexArgs
} else {
    & codex @CodexArgs
}
