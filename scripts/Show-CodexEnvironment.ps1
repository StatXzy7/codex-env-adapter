#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$module = Join-Path $PSScriptRoot "CodexEnv.psm1"
Import-Module -Force -DisableNameChecking $module

$report = Get-CodexEnvironmentReport
if ($Json) {
    $report | ConvertTo-Json -Depth 6
    return
}

$node = $report.Node
$osTz = $report.WindowsTz
$proxy = $report.Proxy

Write-Host "=== Codex 网络 / 时区环境 ==="
Write-Host ("出口 IP      : {0}" -f $node.PublicIp)
Write-Host ("节点位置     : {0}, {1}, {2}" -f $node.City, $node.Region, $node.Country)
Write-Host ("节点 ISP     : {0}" -f $node.Org)
Write-Host ("节点时区     : {0}" -f $node.Timezone)
Write-Host ("探测来源     : {0}" -f $node.Source)
Write-Host ""
Write-Host ("系统时区     : {0} ({1})" -f $osTz.Id, $osTz.BaseUtcOffset)
Write-Host ("进程 TZ      : {0}" -f $(if ($osTz.ProcessTz) { $osTz.ProcessTz } else { "(未设置)" }))
Write-Host ""
Write-Host ("用户代理开关 : {0}" -f $proxy.UserProxyEnable)
Write-Host ("用户代理地址 : {0}" -f $(if ($proxy.UserProxyServer) { $proxy.UserProxyServer } else { "(无)" }))
Write-Host ("WinHTTP      :")
Write-Host $proxy.WinHttp
Write-Host ""

if ($report.ChatGPT) {
    Write-Host ("ChatGPT 版本 : {0}" -f $report.ChatGPT.Version)
    Write-Host ("ChatGPT 路径 : {0}" -f $report.ChatGPT.Exe)
    Write-Host ("正在运行     : {0}" -f $report.ChatGPT.RunningCount)
} else {
    Write-Host ("ChatGPT 状态 : 未找到 ({0})" -f $report.ChatGPTError)
}

Write-Host ""
if ($osTz.Id -like "*China*" -and $node.Country -ne "CN") {
    Write-Host "结论: 系统时区与出口节点不一致。请用 Launch-ChatGPT.cmd 启动桌面端，只注入 TZ，不改系统时区。"
} else {
    Write-Host "结论: 请确认 ChatGPT 流量走当前节点，并用启动器注入节点时区后再使用。"
}
