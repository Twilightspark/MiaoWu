# 把开发自签名证书装进“当前用户”的受信任根与受信任发布者，让签名验证为 Valid。
#
# 这只会影响当前用户，不需要管理员；但它等于让本机信任这把开发证书，
# 仅限测试机使用。正式发布请购买代码签名证书（OV/EV），不要分发这个脚本的产物。
#
# 用法（在普通 PowerShell 里运行，不要在受限终端 / 沙箱里跑）：
#   powershell -ExecutionPolicy Bypass -File scripts\trust-dev-cert.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\trust-dev-cert.ps1 -Remove
#
# 装好后：Get-AuthenticodeSignature <文件> 的 Status 变为 Valid，
# UAC 与文件属性里的“数字签名”会显示 CN=MiaoWu Development，不再是“未知发布者”。

param(
    [string]$CertificatePath = "",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $repoRoot ".artifacts\signing\miaowu-dev.cer"
}

if (-not (Test-Path -LiteralPath $CertificatePath)) {
    Write-Host ("CERT_MISSING {0}" -f $CertificatePath)
    Write-Host "先运行 scripts\sign.ps1 -CreateDevCertificate 生成证书。"
    exit 1
}

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
    (Resolve-Path -LiteralPath $CertificatePath).Path)

$stores = @(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher
)

try {
    foreach ($name in $stores) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
            $name,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        try {
            if ($Remove) {
                $existing = @($store.Certificates | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })
                foreach ($item in $existing) {
                    $store.Remove($item)
                }
                Write-Host ("CERT_REMOVED store={0} thumbprint={1}" -f $name, $certificate.Thumbprint)
            }
            else {
                $store.Add($certificate)
                Write-Host ("CERT_TRUSTED store={0} thumbprint={1}" -f $name, $certificate.Thumbprint)
            }
        }
        finally {
            $store.Close()
        }
    }

    Write-Host ("CERT_SUBJECT=" + $certificate.Subject)
    Write-Host ("CERT_THUMBPRINT=" + $certificate.Thumbprint)
    Write-Host ("CERT_NOT_AFTER=" + $certificate.NotAfter.ToString("yyyy-MM-dd"))
    Write-Host "TRUST_RESULT=OK"
    if (-not $Remove) {
        Write-Host "提示：这是开发证书，只在本机可信；换机器要重新导入，正式发布请换 CA 证书。"
    }
}
catch {
    Write-Host ("TRUST_FAILED " + $_.Exception.Message)
    Write-Host "证书存储写入被拒绝：请在普通桌面（Medium 完整性）的 PowerShell 里运行，或以管理员身份运行并把 -Scope 改为 Machine。"
    exit 1
}

exit 0
