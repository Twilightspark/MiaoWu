# 给 Release 产物与安装包做 Authenticode 签名。
#
# 开发期用自签名证书（.artifacts\signing\miaowu-dev.cer + miaowu-dev.key.json），
# 只在本机可信，不解除 SmartScreen 的“未知发布者”；对外发布必须换成受信任 CA
# 签发的代码签名证书（脚本对 PFX 也支持，见 -PfxPath）。
#
# 私钥不进证书存储：证书用 X509Certificate2 读 .cer，私钥用 RSAParameters 读 JSON，
# 再 CopyWithPrivateKey 组合成带私钥的证书对象。这样在低完整性 / 受限环境里也能签，
# 不会因为写用户密钥容器而报 NTE_PERM。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -CreateDevCertificate
#   powershell -ExecutionPolicy Bypass -File scripts\sign.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Check
#   powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 -Path a.exe,b.exe
#
# 作为 Inno Setup 的 SignTool 使用时，Inno 会把待签文件名追加到命令行末尾，
# 因此这里用 ValueFromRemainingArguments 接住它（等价于 -Path）。

param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,
    [switch]$CreateDevCertificate,
    [switch]$Check,
    [string]$SigningDirectory = "",
    [string]$TimestampUrl = "",
    [string]$Subject = "CN=MiaoWu Development, O=MiaoWu, C=CN",
    [string]$PfxPath = "",
    [string]$PfxPassword = ""
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$repoRoot = Split-Path -Parent $PSScriptRoot

# 真实证书：允许用环境变量注入，便于 CI / 发布机；开发机默认走自签名材料。
if ([string]::IsNullOrWhiteSpace($PfxPath) -and -not [string]::IsNullOrWhiteSpace($env:MIAOWU_SIGN_PFX)) {
    $PfxPath = $env:MIAOWU_SIGN_PFX
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
        $PfxPassword = $env:MIAOWU_SIGN_PFX_PASSWORD
    }
}

if ([string]::IsNullOrWhiteSpace($SigningDirectory)) {
    $SigningDirectory = Join-Path $repoRoot ".artifacts\signing"
}

$certificatePath = Join-Path $SigningDirectory "miaowu-dev.cer"
$keyPath = Join-Path $SigningDirectory "miaowu-dev.key.json"

Add-Type -AssemblyName System.Security

function New-DevCertificate {
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        $Subject,
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $usages = New-Object System.Security.Cryptography.OidCollection
    $usages.Add((New-Object System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.3"))) | Out-Null
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($usages, $false)))
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $false)))
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
    $request.CertificateExtensions.Add(
        (New-Object System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension($request.PublicKey, $false)))

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::Now.AddDays(-1),
        [DateTimeOffset]::Now.AddYears(5))

    New-Item -ItemType Directory -Force -Path $SigningDirectory | Out-Null
    [IO.File]::WriteAllBytes(
        $certificatePath,
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    $parameters = $rsa.ExportParameters($true)
    $key = [ordered]@{
        modulus  = [Convert]::ToBase64String($parameters.Modulus)
        exponent = [Convert]::ToBase64String($parameters.Exponent)
        p        = [Convert]::ToBase64String($parameters.P)
        q        = [Convert]::ToBase64String($parameters.Q)
        dp       = [Convert]::ToBase64String($parameters.DP)
        dq       = [Convert]::ToBase64String($parameters.DQ)
        inverseQ = [Convert]::ToBase64String($parameters.InverseQ)
        d        = [Convert]::ToBase64String($parameters.D)
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($keyPath, $key, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ("DEV_CERT_CREATED subject={0} thumbprint={1}" -f $certificate.Subject, $certificate.Thumbprint)
    Write-Host ("  cer={0}" -f $certificatePath)
    Write-Host ("  key={0}" -f $keyPath)
}

function Get-SigningCertificate {
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if (-not (Test-Path -LiteralPath $PfxPath)) {
            throw "找不到 PFX：$PfxPath"
        }

        $secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            (Resolve-Path -LiteralPath $PfxPath).Path, $secure)
    }

    if (-not (Test-Path -LiteralPath $certificatePath) -or -not (Test-Path -LiteralPath $keyPath)) {
        throw "签名材料缺失：$certificatePath / $keyPath。先跑 scripts\sign.ps1 -CreateDevCertificate。"
    }

    $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    $material = Get-Content -LiteralPath $keyPath -Raw | ConvertFrom-Json
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $parameters = New-Object System.Security.Cryptography.RSAParameters
    $parameters.Modulus = [Convert]::FromBase64String($material.modulus)
    $parameters.Exponent = [Convert]::FromBase64String($material.exponent)
    $parameters.P = [Convert]::FromBase64String($material.p)
    $parameters.Q = [Convert]::FromBase64String($material.q)
    $parameters.DP = [Convert]::FromBase64String($material.dp)
    $parameters.DQ = [Convert]::FromBase64String($material.dq)
    $parameters.InverseQ = [Convert]::FromBase64String($material.inverseQ)
    $parameters.D = [Convert]::FromBase64String($material.d)
    $rsa.ImportParameters($parameters)

    return [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($public, $rsa)
}

function Resolve-Targets {
    if ($Path -and $Path.Count -gt 0) {
        return @($Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $targets = New-Object System.Collections.Generic.List[string]
    $appExe = Join-Path $repoRoot "src\MiaoWu.App\bin\Release\net48\MiaoWu.exe"
    if (Test-Path -LiteralPath $appExe) {
        $targets.Add($appExe)
    }

    $installerDirectory = Join-Path $repoRoot ".artifacts\installer"
    if (Test-Path -LiteralPath $installerDirectory) {
        Get-ChildItem -LiteralPath $installerDirectory -Filter "MiaoWu-*-setup*.exe" |
            ForEach-Object { $targets.Add($_.FullName) }
    }

    return @($targets)
}

if ($CreateDevCertificate) {
    New-DevCertificate
    exit 0
}

$targets = Resolve-Targets
if ($targets.Count -eq 0) {
    Write-Host "SIGN_SKIPPED 没有可签名的目标。"
    exit 1
}

if ($Check) {
    $failed = 0
    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target)) {
            Write-Host ("VERIFY_MISSING {0}" -f $target)
            $failed++
            continue
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $target
        if ($signature.Status -eq "NotSigned" -or $null -eq $signature.SignerCertificate) {
            Write-Host ("VERIFY_NOT_SIGNED {0}" -f $target)
            $failed++
        }
        else {
            Write-Host ("VERIFY_OK {0} status={1} thumbprint={2}" -f $target, $signature.Status, $signature.SignerCertificate.Thumbprint)
        }
    }

    exit $(if ($failed -eq 0) { 0 } else { 1 })
}

$certificate = Get-SigningCertificate

$failures = 0
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host ("SIGN_MISSING {0}" -f $target)
        $failures++
        continue
    }

    try {
        $arguments = @{
            LiteralPath  = $target
            Certificate  = $certificate
            HashAlgorithm = "SHA256"
        }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $arguments.TimestampServer = $TimestampUrl
        }

        $null = Set-AuthenticodeSignature @arguments
        $signature = Get-AuthenticodeSignature -LiteralPath $target
        if ($signature.Status -eq "NotSigned" -or $null -eq $signature.SignerCertificate) {
            Write-Host ("SIGN_FAIL {0} status={1} message={2}" -f $target, $signature.Status, $signature.StatusMessage)
            $failures++
        }
        else {
            Write-Host ("SIGN_OK {0} status={1} thumbprint={2}" -f $target, $signature.Status, $signature.SignerCertificate.Thumbprint)
        }
    }
    catch {
        Write-Host ("SIGN_ERROR {0} {1}" -f $target, $_.Exception.Message)
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host ("SIGN_RESULT=FAILED failures={0}" -f $failures)
    exit 1
}

Write-Host "SIGN_RESULT=OK"
if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    Write-Host "SIGN_NOTE 自签名证书只在本机可信：把 miaowu-dev.cer 装进“当前用户\受信任的根证书颁发机构”和“受信任的发布者”后，签名才会验证为 Valid。"
}
exit 0
