#Requires -Version 5.1
<#
.SYNOPSIS
    构建 miaowu_office.exe 并放进 skills/office-wps/bin/。

.DESCRIPTION
    Release + net48 + x64；不依赖 NuGet（只用 BCL），可在离线环境构建。
    构建完成后打印体积与 SHA-256，便于写进交付说明。

.PARAMETER Configuration
    默认 Release。

.PARAMETER NoCopy
    只构建，不复制到技能目录。

.PARAMETER Zip
    额外把整个技能目录打成 dist\office-wps-<version>.zip。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\miaowu-office\build.ps1 -Zip
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoCopy,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $PSScriptRoot 'MiaowuOffice.csproj'
$skillDir = Join-Path $root 'skills\office-wps'
$skillBin = Join-Path $skillDir 'bin'

function Resolve-Dotnet {
    $candidates = @(
        (Join-Path $root '.dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe',
        'C:\Program Files (x86)\dotnet\dotnet.exe'
    )
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates = @($command.Source) + $candidates }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw '找不到 dotnet.exe。装 .NET SDK 8+ 或把 dotnet 放进 PATH。'
}

$dotnet = Resolve-Dotnet
Write-Host "dotnet: $dotnet"

& $dotnet build $project -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

$output = Join-Path $PSScriptRoot "bin\$Configuration\miaowu_office.exe"
if (-not (Test-Path -LiteralPath $output)) { throw "没有找到构建产物：$output" }

$info = Get-Item -LiteralPath $output
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Host ("产物: {0}  {1:N0} 字节" -f $info.FullName, $info.Length)
Write-Host ("SHA256: {0}" -f $hash)

if (-not $NoCopy) {
    if (-not (Test-Path -LiteralPath $skillBin)) {
        New-Item -ItemType Directory -Path $skillBin -Force | Out-Null
    }
    Copy-Item -LiteralPath $output -Destination (Join-Path $skillBin 'miaowu_office.exe') -Force
    Write-Host "已复制到: $skillBin"

    # 可选依赖：如果开发机 bin 下放了 pdfium.dll，一并带过去（不进仓库，随发布手动决定）。
    $pdfium = Join-Path (Split-Path -Parent $output) 'pdfium.dll'
    if (Test-Path -LiteralPath $pdfium) {
        Copy-Item -LiteralPath $pdfium -Destination (Join-Path $skillBin 'pdfium.dll') -Force
        Write-Host "已复制可选依赖: pdfium.dll"
    }
}

if ($Zip) {
    $dist = Join-Path $root 'dist'
    if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
    $zipPath = Join-Path $dist 'office-wps-1.0.0.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    # 不用 Compress-Archive：非交互宿主里它的 Write-Progress 会抛 ReadConsoleOutput。
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $skillDir,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    $zipInfo = Get-Item -LiteralPath $zipPath
    Write-Host ("安装包: {0}  {1:N0} 字节" -f $zipInfo.FullName, $zipInfo.Length)
}
