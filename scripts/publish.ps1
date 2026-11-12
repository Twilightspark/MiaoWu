param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}

$appProject = Join-Path $repoRoot "src\MiaoWu.App\MiaoWu.App.csproj"
& $dotnet build $appProject -c $Configuration -m:1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$output = Join-Path $repoRoot "src\MiaoWu.App\bin\$Configuration\net48"
Write-Host "APP_OUTPUT=$output"

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
}

if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup 6 (ISCC.exe) was not found."
    Write-Host "Install it from https://jrsoftware.org/isdl.php then rerun scripts\publish.ps1."
    exit 2
}

$iss = Join-Path $repoRoot "installer\MiaoWu.iss"
& $iscc $iss
exit $LASTEXITCODE
