#Requires -Version 5.1
param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "dist"
}

$project = Join-Path $root "src\CodexEnvAdapter\CodexEnvAdapter.csproj"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDir

$exe = Join-Path $OutputDir "CodexEnvAdapter.exe"
if (-not (Test-Path $exe)) {
    throw "Publish succeeded but $exe was not found."
}

Get-Item $exe | Select-Object FullName, Length, LastWriteTime
Write-Host "OK: $exe"
