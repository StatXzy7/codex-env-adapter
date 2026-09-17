#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ShortcutName = "Codex-Timezone-Launcher.lnk"
)

$ErrorActionPreference = "Stop"
$desktop = [Environment]::GetFolderPath("Desktop")
$cmdPath = Join-Path $PSScriptRoot "Launch-ChatGPT.cmd"
if (-not (Test-Path -LiteralPath $cmdPath)) {
    throw "未找到 $cmdPath"
}

$shortcutPath = Join-Path $desktop $ShortcutName
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $cmdPath
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.WindowStyle = 1
$shortcut.Description = "Launch Codex/ChatGPT with current node timezone"

$pkg = Get-AppxPackage OpenAI.Codex | Select-Object -First 1
if ($pkg) {
    $exe = Join-Path $pkg.InstallLocation "app\ChatGPT.exe"
    if (Test-Path -LiteralPath $exe) {
        $shortcut.IconLocation = "$exe,0"
    }
}

$shortcut.Save()
Write-Host "桌面快捷方式已创建: $shortcutPath"
Write-Host "目标: $cmdPath"
