param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # 生成自带 .NET Framework 4.8 离线安装程序的全量包（约 105 MB）。
    [switch]$BundleDotNet,
    # 离线安装程序路径；留空则依次找 redist\、.artifacts\redist\。
    [string]$DotNetRedist = ""
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
    $iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
}

# 允许把 Inno Setup 装在非默认目录（目录或 ISCC.exe 路径均可）。
if (-not (Test-Path $iscc) -and -not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_DIR)) {
    $custom = $env:INNO_SETUP_DIR
    if (Test-Path $custom -PathType Container) {
        $custom = Join-Path $custom "ISCC.exe"
    }

    if (Test-Path $custom) {
        $iscc = $custom
    }
}

if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup 6 (ISCC.exe) was not found."
    Write-Host "Install it from https://jrsoftware.org/isdl.php then rerun scripts\publish.ps1."
    exit 2
}

$version = "0.0.0"
$props = Join-Path $repoRoot "Directory.Build.props"
if (Test-Path $props) {
    $match = Select-String -Path $props -Pattern "<VersionPrefix>([^<]+)</VersionPrefix>" | Select-Object -First 1
    if ($match) {
        $version = $match.Matches[0].Groups[1].Value.Trim()
    }
}

$installerDir = Join-Path $repoRoot ".artifacts\installer"
$setupSuffix = "-setup"
if ($BundleDotNet) {
    $setupSuffix = "-setup-offline"
}

$setup = Join-Path $installerDir "MiaoWu-$version$setupSuffix.exe"
$portable = Join-Path $installerDir "MiaoWu-$version-portable.zip"
$stage = Join-Path $installerDir "_portable"

# 全量包需要 ndp48-x86-x64-allos-enu.exe（约 105 MB）。这里做三件事：
# 定位文件、确认它是微软签名的原件、保证路径里没有空格（ISCC 的 /D 值不带引号）。
if ($BundleDotNet) {
    if ([string]::IsNullOrWhiteSpace($DotNetRedist)) {
        $candidates = @(
            (Join-Path $repoRoot "redist\ndp48-x86-x64-allos-enu.exe"),
            (Join-Path $repoRoot ".artifacts\redist\ndp48-x86-x64-allos-enu.exe")
        )
        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) {
                $DotNetRedist = $candidate
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($DotNetRedist) -or -not (Test-Path $DotNetRedist)) {
        Write-Host "DOTNET_REDIST_MISSING 需要 ndp48-x86-x64-allos-enu.exe"
        Write-Host "先运行 scripts\fetch-dotnet-redist.ps1，或手动放到 redist\ 再重试。"
        exit 3
    }

    $DotNetRedist = (Resolve-Path $DotNetRedist).Path
    $redistSignature = Get-AuthenticodeSignature -LiteralPath $DotNetRedist
    if (($null -eq $redistSignature.SignerCertificate) -or
        ($redistSignature.SignerCertificate.Subject -notmatch "Microsoft")) {
        Write-Host ("DOTNET_REDIST_UNTRUSTED 需要微软签名的原版，当前签名者：" +
            $(if ($null -eq $redistSignature.SignerCertificate) { "无" } else { $redistSignature.SignerCertificate.Subject }))
        exit 3
    }

    if ($DotNetRedist -match "\s") {
        # ISCC 的 /Dname=value 不能带引号，所以把带空格的路径复制到命名干净的目录。
        $stagedRedist = Join-Path $repoRoot ".artifacts\redist\ndp48-x86-x64-allos-enu.exe"
        if ($stagedRedist -match "\s") {
            Write-Host "DOTNET_REDIST_PATH 仓库路径含空格，无法作为 ISCC 宏值传入。"
            exit 3
        }

        New-Item -ItemType Directory -Path (Split-Path $stagedRedist -Parent) -Force | Out-Null
        Copy-Item $DotNetRedist $stagedRedist -Force
        $DotNetRedist = $stagedRedist
    }

    Write-Host ("DOTNET_REDIST=" + $DotNetRedist)
    Write-Host ("DOTNET_REDIST_SIZE=" + (Get-Item $DotNetRedist).Length)
}

# 签名不在这里做：应用 exe 与安装包默认保持未签名。
# 需要签名时手工跑 scripts\sign.ps1（开发自签名）或 scripts\sign.ps1 -PfxPath <CA 证书.pfx>，
# 对产物逐个签；publish.ps1 既不加 /DSIGN，也不改 Inno 的 SignedUninstaller。
$iss = Join-Path $repoRoot "installer\MiaoWu.iss"
$isccArgs = @()

if ($BundleDotNet) {
    # 这两个宏只影响 [Files]/[Code] 与产物名，小包不受影响。
    $isccArgs += "/DBUNDLE_DOTNET"
    $isccArgs += ("/DDotNetRedist=" + $DotNetRedist)
}

& $iscc @isccArgs $iss

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# 让 Windows 在进程创建时就提权：把 SetupLdr 的清单改成 requireAdministrator。
# 不改的话提权由 SetupLdr 运行时发起，低完整性入口会在解包 %TEMP% 那一步先失败（Error 5）。
$patchScript = Join-Path $repoRoot "scripts\patch-setup-manifest.ps1"
if ((Test-Path $patchScript) -and (Test-Path $setup)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $patchScript -Path $setup
    if ($LASTEXITCODE -ne 0) {
        Write-Host "MANIFEST_PATCH_FAILED"
        exit 1
    }
}

if (Test-Path $portable) {
    Remove-Item $portable -Force
}

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null
Get-ChildItem $output -Recurse -File |
    Where-Object { $_.Extension -ne ".pdb" } |
    ForEach-Object {
        $relative = $_.FullName.Substring($output.Length + 1)
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item $_.FullName $destination -Force
    }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage,
    $portable,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Remove-Item $stage -Recurse -Force
Write-Host "PORTABLE=$portable"

Write-Host ("BUNDLE_DOTNET=" + $(if ($BundleDotNet) { "yes" } else { "no" }))
if (-not $BundleDotNet) {
    # 提醒一句：本地已经有离线安装程序时，加个开关就能顺带出全量包。
    foreach ($candidate in @(
            (Join-Path $repoRoot "redist\ndp48-x86-x64-allos-enu.exe"),
            (Join-Path $repoRoot ".artifacts\redist\ndp48-x86-x64-allos-enu.exe"))) {
        if (Test-Path $candidate) {
            Write-Host "DOTNET_REDIST_AVAILABLE $candidate"
            Write-Host "HINT 加 -BundleDotNet 可同时产出内嵌 .NET 4.8 的全量包"
            break
        }
    }
}

if (Test-Path $setup) {
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    Write-Host "SETUP=$setup"
    Write-Host "SHA256=$hash"
    Write-Host ("SIZE=" + (Get-Item $setup).Length)
}

exit 0
