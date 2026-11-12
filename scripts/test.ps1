param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}

$testProject = Join-Path $repoRoot "tests\MiaoWu.Tests\MiaoWu.Tests.csproj"
& $dotnet build $testProject -c $Configuration -m:1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$runner = Join-Path $repoRoot ".nuget\packages\xunit.runner.console\2.9.2\tools\net48\xunit.console.exe"
if (-not (Test-Path $runner)) {
    throw "xunit.console.exe was not found. Run a networked NuGet restore first: dotnet restore MiaoWu.sln"
}

$assembly = Join-Path $repoRoot "tests\MiaoWu.Tests\bin\$Configuration\net48\MiaoWu.Tests.dll"
& $runner $assembly -nologo -nocolor
exit $LASTEXITCODE
