param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-DotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "dotnet was not found. Install the .NET SDK or add dotnet to PATH."
}

$dotnet = Resolve-DotNet

Write-Host "dotnet: $dotnet"
Write-Host "build: MiaoWu.sln ($Configuration, single-node)"

& $dotnet build (Join-Path $repoRoot "MiaoWu.sln") -c $Configuration -m:1
exit $LASTEXITCODE
