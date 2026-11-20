param(
    # 保存位置；留空则写到 .artifacts\redist\ndp48-x86-x64-allos-enu.exe。
    [string]$Destination = ""
)

# 下载 .NET Framework 4.8 离线安装程序，给 scripts\publish.ps1 -BundleDotNet 用。
# 只接受微软签名的原版；下完打印大小与 SHA-256，方便和别的机器对一下。
# 需要在普通（非 Low 完整性）终端里跑：受限终端的 TLS 会因为拿不到证书私钥句柄而握手失败。

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$url = "https://go.microsoft.com/fwlink/?LinkId=2088631"
$fileName = "ndp48-x86-x64-allos-enu.exe"

# 备用直链：微软官方 download.microsoft.com 上的同名文件，fwlink 失效时用它。
$fallbackUrls = @(
    "https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe"
)

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repoRoot ".artifacts\redist\$fileName"
}

$Destination = [IO.Path]::GetFullPath($Destination)
$directory = Split-Path $Destination -Parent
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

function Get-RedistInfo {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }

    $item = Get-Item $Path
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $signer = ""
    if ($null -ne $signature.SignerCertificate) {
        $signer = $signature.SignerCertificate.Subject
    }

    return [pscustomobject]@{
        Path   = $item.FullName
        Size   = $item.Length
        Signer = $signer
        Hash   = (Get-FileHash $item.FullName -Algorithm SHA256).Hash
    }
}

function Test-RedistInfo {
    param($Info)

    if ($null -eq $Info) {
        return $false
    }

    # 4.8 离线安装程序约 105–120 MB；太小的多半是网页安装器或错误页。
    if (($Info.Size -lt 100MB) -or ($Info.Size -gt 150MB)) {
        return $false
    }

    return ($Info.Signer -match "Microsoft")
}

$existing = Get-RedistInfo -Path $Destination
if (Test-RedistInfo -Info $existing) {
    Write-Host "REDIST_EXISTS $($existing.Path)"
    Write-Host "REDIST_SIZE=$($existing.Size)"
    Write-Host "REDIST_SHA256=$($existing.Hash)"
    Write-Host "REDIST_SIGNER=$($existing.Signer)"
    exit 0
}

Write-Host "DESTINATION=$Destination"

# 依次试主链接与备用直链；每个下完都当场校验，不合格就删掉换下一个。
$sources = @($url) + $fallbackUrls
$info = $null
foreach ($source in $sources) {
    Write-Host "DOWNLOADING $source"
    $downloaded = $false
    try {
        # BITS 支持续传，网断了不白下。
        Start-BitsTransfer -Source $source -Destination $Destination -Description "MiaoWu .NET Framework 4.8 redist"
        $downloaded = $true
    }
    catch {
        Write-Host "BITS_UNAVAILABLE $($_.Exception.Message)"
    }

    if (-not $downloaded) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $source -OutFile $Destination -UseBasicParsing
        }
        catch {
            Write-Host "DOWNLOAD_FAILED $($_.Exception.Message)"
        }
    }

    $candidate = Get-RedistInfo -Path $Destination
    if (Test-RedistInfo -Info $candidate) {
        $info = $candidate
        break
    }

    if ($null -ne $candidate) {
        Remove-Item $candidate.Path -Force -ErrorAction SilentlyContinue
    }
}

if ($null -eq $info) {
    Write-Host "FETCH_INVALID 没能拿到微软签名的 4.8 离线安装程序，已清掉下载到的文件。"
    Write-Host "可手动从 https://dotnet.microsoft.com/download/dotnet-framework/net48 下载「Offline installer」"
    Write-Host "放到 .artifacts\redist\ndp48-x86-x64-allos-enu.exe 后重试。"
    exit 1
}

Write-Host "FETCH_OK $($info.Path)"
Write-Host "REDIST_SIZE=$($info.Size)"
Write-Host "REDIST_SHA256=$($info.Hash)"
Write-Host "REDIST_SIGNER=$($info.Signer)"
Write-Host "NEXT=scripts\publish.ps1 -Configuration Release -BundleDotNet"
exit 0
